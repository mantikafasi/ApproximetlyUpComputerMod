# How ApproximetlyComputers Works

Internal architecture notes for the AU-08 Lua Computer plugin (0.4.4) for
Approximately Up 1.0.120. Written for mod developers; user-facing documentation
lives in [README.md](README.md).

The plugin is a single BepInEx IL2CPP plugin. It adds one native-compatible item,
runs a managed Lua VM on the main thread, and reads/writes existing game ECS data
directly. It does not register new ECS types, systems, or serializer IDs.

## Architecture at a glance

| Layer | File | Responsibility |
| --- | --- | --- |
| Bootstrap / session | `src/Plugin.cs` | Harmony install, tick hook, VM lifecycle, save/run/stop, autostart, authority, HUD text |
| ECS port access | `src/Ports.cs` | Target discovery, port read/write, target validation |
| Item registration | `src/ComputerItem.cs` | Prefab clone, geometry, label storage, program-ID read/write |
| Interaction | `src/ComputerInteraction.cs` | Native ray target, E key handling, input research |
| Editor | `src/ComputerEditor.cs`, `src/LuaEditing.cs` | IMGUI editor, drafts, input capture, completion, undo |
| Program storage | `src/ProgramStore.cs` | Immutable Lua files, template, legacy bindings |
| Lua runtime | `src/Scripting/LuaComputer.cs` | MoonSharp VM, API, budgets |
| Networking | `src/ComputerNetwork.cs`, `src/ComputerMessage.cs`, `src/ComputerLink.cs` | Native event publication, protocol, Steam channel |
| Authority | `src/SessionSafety.cs` | Host-write policy |

## 1. Load and compatibility gates

`Plugin.Load` runs before any game system exists. It:

- Verifies `GameAssembly.dll` and `global-metadata.dat` SHA256. A different game
  build is rejected before any patch is installed.
- Runs two self-tests: a Lua smoke test (`input 3 -> output 6`) and the editor
  buffer test. Failure prevents plugin startup.
- Installs a Harmony patch on the native `ComponentSystemGroup.UpdateAllSystems`
  postfix and filters for `SpaceshipComponentsTickPostGroup`. That is the only
  simulation hook; the plugin is not an ECS system.
- Installs `UIManager.Update` postfix for F8/F9/editor polling.
- Prepares item registration under a `Core.Initialize` prefix/postfix.
- Ad-hoc interaction/editor hooks are installed by their own classes.

Requires `EPC_SCLabel.Blueprint.ClassID == 35`; registration aborts otherwise.

## 2. Item registration (authoring clone)

Native item registration happens once, during `Core.Initialize`, before the game
converts its authoring array.

**Donors.** From the game's own `Core._spaceshipComponents` the plugin picks:

- an enabled, configured `EPC_SCLabel` with an owned text renderer as the label
  storage template;
- an enabled `EPC_SCSignalProcessor` of type `AdditionArray` in the Math group
  with one owned box collider and six active support polygons as the chassis.

**Clone.** The chassis is instantiated under a hidden holder, then its processor
component is deleted. A new `EPC_SCLabel` is added and base fields are copied
from the math clone: mass, colliders, categories, groups, port setup, sound and
paint data. Nothing is modified on the donor.

- Bounds become `(1, 0.25, 1)`; the support polygons are scaled in chassis space
  and the box collider is resized to match.
- The cached `_colliderBAR` blob is replaced with a fresh empty
  `BlobAssetReference<Collider>`. A generated wrapper cannot hold a `default`
  struct here; the donor's shared blob is never disposed.
- The label storage subtree is cloned separately for its remapped actionable
  references. Its text renderer is kept with `_maxLength = 16` because native
  class 35 `FromBinary` writes into that exact buffer.
- A `DOTSColliderBox` + `EPC_Collider` (Actionable filter, NoCollisions material)
  is placed on the same GameObject as the label. This is the E hit volume. It is
  deliberately not part of the structural collider list.
- Ten CRP renderers are built from `assets/computer.mesh.json` with identity
  transforms; exported geometry is already in chassis units.
- Sixteen `ElectricPortSetup` entries are generated: eight inputs on `X-` and
  eight outputs on `X+`, one per 0.125 pitch, with anchor `mouth - forward*0.0625`
  because the native glyph/cable face sits `+forward/16` from the anchor.

**Publish.** Appending the clone to `Core._spaceshipComponents` is the only
registration write. The game's own `Core.Initialize` then builds conversion,
`_componentsMap`, prefab maps, availability, and serialization headers. Because
the name `SC_ProgrammableComputer` hashes to a new `SCPrefab` and class 35 is
reused, native save/blueprint serialization already understands the item.

**Verify.** The postfix re-checks the converted prefab: `SCBlueprintClass == 35`,
no `SCTypeSignalProcessor` remains, the physics collider exists, every CRP child
is parented with identity transform and matching mesh bounds, all 16 port mouths
match mesh socket rings, port tooltips resolve to `Input 1..8` / `Output 1..8`,
and the interaction volume is correct. On any mismatch the inventory entry is
hidden (categories 0, available 0, removed from maps) and execution stays off.

`GetName`/`GetDescription` prefix patches rename only the clone pointer.

## 3. Simulation hook and tick loop

`AfterGroup` runs on every game system-group update, immediately after the
`SpaceshipComponentsTickPostGroup` pass — i.e. after native input propagation and
processors, on the main thread. It asserts the thread and latches `failed` on
exceptions.

`OnTick` performs, in order:

1. Re-read the world identity from `Core._save._world` GUID. A different world
   stops every VM, clears computer state, resets networking/editor scopes.
2. `CompleteAllTrackedJobs()` — the native pass schedules Burst jobs; all data
   access happens only after tracked jobs complete.
3. `RefreshGameMode`: resolves the `SpaceshipSingleton` entity/version. Entering
   game mode enables autostart; leaving stops everything. A dedicated prefix on
   `DestroySpaceship`, `Core.ClearGame`, and `Core.Dispose` cancels managed intent
   before native restoration, even while paused.
4. Reads `UniverseCoreSingleton._physicsTickID` and `Core.Singleton._simulationSpeed`.
   Ticks are deduplicated; a lower tick ID is treated as rewind (stop all, disable
   autostart, discard outputs without writing native values). Paused or
   zero-speed passes never run Lua.
5. Editor-channel polling (20 Hz with peers, 4 Hz solo).
6. Autostart scan at most once per second while in an advancing flight.
7. For each running VM: skip unless the computer is a current flight member and
   active; require its native program reference to be unchanged; read inputs;
   `Vm.Tick`; commit outputs; on failure stop and fault that VM. Maximum eight
   running VMs.
8. Publish changed outputs/program IDs to the native event queue when peers exist.

Because outputs are committed after the native pass, the game's next simulation
pass sees them; nothing races the current pass.

## 4. ECS access

**Discovery** (`Ports.FindTargets`) uses one `EntityQuery` over `SCGuid`,
`SpaceshipElectricPortRef`, `Parent`, `SCActive`, capped at 4096 candidates.
Each candidate must belong to the registered prefab id, be placed (no `Prefab`,
no `Disabled`), and have a nonzero GUID. Ports are sorted by native `_index`, one
target per GUID, at most 64 targets and 8+8 ports.

**Read** (`ReadInputs`) uses `GetComponentData<SpaceshipElectricPort>` per port.
Values must be finite and within `±1e20f`; camera-encoded values are refused.

**Write** (`WriteOutputs`) is staged:

1. Copy every output struct out of ECS.
2. Refuse outputs that currently hold non-numeric/reserved signals.
3. Run native `SetDataValue()` on the copies (it clamps and rejects NaN).
4. Validate every value, then `SetComponentData` each port.

A failure before step 4 writes nothing. Committing after validation avoids
partially updated port sets.

`ValidateTarget` re-derives the live target before every read, write, publish and
editor open: world identity, entity existence/version, GUID match, exact port
entity arrays, and layout. No native buffer or pointer is retained between
calls.

**Raw access.** Where the interop assembly lacks a concrete generic
instantiation, the plugin reads via `GetComponentDataRawRO/RW` with unmanaged
pointers and runtime-checked sizes (`ComputerInteraction.CheckSize`): input
singleton 52 bytes, key consumer 1, actionable controller 40.

**Known IL2CPP ABI pitfalls** (all runtime-verified):

- Generated `RefRW<T>.ValueRO/ValueRW` getters return the result of
  `runtime_invoke` as a CLR ref, which does not point into ECS memory. Live ECS
  state must be read through `_Data`, raw pointers, or the checked
  `GetComponentData` path.
- `sizeof()` on a generated wrapper can omit native tail padding:
  `EPC_SCLabel.Blueprint.BlueprintData` is 53 managed but 56 native;
  `NetcoreEvent_SetActionableLabel` is 42 in the wrapper but 44 native. Both
  native sizes are asserted at runtime and the label event is built in a
  size-44 explicit buffer.

## 5. Lua runtime

`LuaComputer` is one MoonSharp `Script` per computer, created with
`CoreModules.None`. Only explicit globals exist:

- `state`: per-VM table, reset on stop/fault only.
- `math`: fixed-arity `abs acos asin atan ceil cos exp floor log sin sqrt tan
  deg rad atan2 min max pow fmod pi`.
- `type`, `assert`, `error`: no coercion, no traceback access.
- `input(i)`, `input_bool(i)`, `input_vec3(i)`, `output(i, v)`,
  `output_vec3(i, v)`; one-based indices, bounds checked against the configured
  port count.
- `dt`, `tick_id`: set per tick.

No base/string/table/io/os/debug/coroutine/random/load access exists. Strings and
tables are internal Lua values only.

**Limits.** Source ≤ 16,384 chars, rejects bytecode/escape prefixes. Initialization
runs once with a 10,000-instruction budget and a 4 MiB measured allocation delta;
each tick does the same. The budget is enforced with a coroutine and
`AutoYieldCounter`, so long Lua loops fail instead of stalling the game. A failed
parse/tick permanently faults the VM; state is not rolled back and errors are
reported once with a clipped message.

Outputs are staged in a per-tick zero array; unwritten outputs stay zero. Booleans
coerce to 0/1. Values must be finite and inside the native `±1e20f` range. Port
access outside `tick()` is rejected.

This is not a hostile-code sandbox: instructions are sliced, not preempted, and
allocation checks are per-entry measurements, not a retained-heap quota. Only
trusted scripts (including code sent by trusted teammates) should be run.

## 6. Programs and persistence

`ProgramStore` keeps everything under `BepInEx/plugins/ApproximatelyUpComputer/`:

- `computer.lua` — initial template for unprogrammed computers. Never overwritten
  by deploys; an empty/missing file falls back to the built-in starter in memory.
- `scripts/<12-hex-ID>.lua` — immutable program files, created with `CreateNew`,
  written and flushed; a new ID is allocated before any native reference changes.
- `legacy/<sha256>.id` — old bindings, retained on disk, unused for execution.

The native reference is stored in the label's `ActionableLabelString`: exactly 12
uppercase hex characters plus NUL padding in 16 UTF-16 slots. Empty means
unprogrammed; malformed nonempty values raise `InvalidDataException` (strict path)
and are only repaired by an explicit editor Save, never silently rewritten.

Saving is host-only, compares both the expected program ID and the expected
source, forks to a new immutable file, updates the native label, stops the VM, and
invalidates any queued Run. Copying a computer copies the reference; the first
save of the copy forks it, leaving the original source untouched. Lua source is
never embedded in native saves or blueprints — keep the `scripts` folder with any
world backup.

## 7. Editor

`ComputerEditor` is an IMGUI window drawn from the HUD `OnGUI`. Opening is done by
E on the native ray target; the editor never executes the source.

- **Drafts**: one `ComputerEditorBuffer` per document, keyed by an editor scope
  string (world generation, host/client role, source authority) plus GUID, entity
  index/version and world pointer. Dirty drafts stay in memory (≤ 64 inactive plus
  the active one); switching documents never touches files.
- **Input capture**: while open, background UI input modules are suspended.
  Harmony hooks gate `EventSystem`, input-field focus, raw key/key-state reads and
  the native typed-text buffer so game input cannot leak. After the native input
  producer runs, `ComputerInteraction.ClearNativeInput` resets the raw
  `CRPInputSingleton` snapshot and reserves the `KeyConsumer` byte, so a held key
  cannot act in the world. Cursor lock is enforced and restored on close.
- **Editing**: caret/selection-aware edits (`LuaEditing.cs`), four-space
  Tab/Shift+Tab, indentation-preserving Enter, lexical completion for Lua
  keywords/API/identifiers, 32-step undo/redo. No evaluation, no LSP, no scope
  analysis.
- **Commands**: Save, Save + Run, Run existing, Stop, Reload/Discard, Starter
  template. Clients route these through the link instead of local execution.
  Conflicting remote revisions are refused, not merged.

## 8. E targeting

The native game drives actionable targeting through `ActionablesControllerUpdate`
(Burst). The plugin never scans for targets; it reads the native ray result.

- A postfix after `CustomRenderPipelineInputSystem.Update` samples the configured
  action key from the raw input singleton (plus `Keyboard.current`) and tracks
  Down edges. This is the proven producer path; generated accessors are not used.
- A prefix on `WorldUnmanagedImpl.UpdateSystem` matches the exact versioned
  `SystemHandle` of `ActionablesControllerUpdate` and runs immediately before its
  Burst dispatch, while the native ray is current.
- Validation chain: overlay closed, player/controller singletons present, target
  not already assigned (except the release-recovery branch), target is a child of
  an `SCGuid` computer, `BelongingSC` links to the plugin's prefab, GUID nonzero,
  label/group/assignment components consistent. The garage hand-state gate is
  applied only outside flight, matching native behavior.
- On a matching target the plugin reserves the native `KeyConsumer` first, so a
  refused open cannot fall through into the native program-ID label typing. UI
  gates (focus, cursor lock, TMP focus) run after the reservation.
- Recovery: if an older build left the label assigned, the plugin invokes the
  audited native `OnReleaseActionable` with postcondition checks and requires a
  fresh key press on the next natural ray target.

## 9. Multiplayer

Editor traffic uses Steam `NetworkingMessages` on channel `0x41553038` — a
separate namespace from the game's Netcore packet IDs and receive queue. The
native queue is used only for well-defined game events (output and label updates).

- **Handshake**: client `hello` with a nonce; host `welcome` with session epoch
  and challenge; client `ack`; host `ready`. Every envelope carries magic,
  protocol, plugin version, game hash, prefab hash and policy; mismatches are
  rejected. Messages are deduplicated by monotonic sequence and validated against
  exact field whitelists; payload cap is 64 KiB.
- **Admission**: clients only accept the peer that matches the native lobby owner,
  using the game's own Netcore client admission plus Steam identity. Client
  source caching is memory-only and is adopted only when the native label ID
  matches the reply; local files are never substituted.
- **Authority** (`SessionSafety.CanWrite`): only the native server
  (`_isServer` + `ServerOpen`/`ServerClosed`) runs Lua. Under the selected
  trusted-lobby policy, admitted teammates may request save/run/stop through the
  host; the host performs them with expected-program-ID guards. Clients never
  run a VM.
- **Output replication**: after a successful native commit, the host re-reads the
  committed port values and enqueues native `NetcoreEvent_SyncPortValue` packets
  addressed by `NetcoreEntity` + linked-group index (never physical port index),
  changed-only up to 60 batches/s with a 1 s full refresh. Program IDs are
  published as native `SetActionableLabel` events (44-byte padded buffer,
  `ActionableLabelAsFloat.NONE` sentinel). Rewind discards cached outputs without
  writing over restored native values.

## 10. Lifecycle and resets

- World change, rewind, authority loss, flight exit, and native teardown all stop
  VMs; explicit Stop schedules zero outputs on the next safe tick. Stop/error
  outputs clear; restored native values after rewind are never overwritten.
- Autostart discovers eligible computers among current flight members, at most
  once per second, refuses malformed references, and never retries a computer
  that was already attempted or manually stopped within the same flight. A new
  flight version resets attempts.
- Pause and native distance culling preserve VM RAM but execute no ticks.
- F8 toggles the last E-targeted computer; F9 stops all. Editor capture suppresses
  gameplay controls while typing.

## 11. Verification status

Runtime-confirmed: item conversion and geometry (native mouth/mesh-ring match,
CRP identity, collider layout, port names), raw input-memory reading and key
reservation, E open in build and game mode, save and native output, automatic
start/stop on game-mode transitions, native clone/serialization roundtrip and
teardown in controlled probes. The repository also carries portable checks for
the Lua runtime, editor buffers, protocol/session code and authority policy.

Still open: actual two-account handshake/editing/output delivery, flight-edit
reference persistence after returning to build mode, full native save/load and
blueprint import roundtrip, long-session performance, rebound keys, and extended
culling/rewind behavior. See README "Limits and Diagnostics" for the user-facing
list. A native blueprint alone is not a portable program package.
