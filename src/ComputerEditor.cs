using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Unity.Entities;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace ApproximatelyUp.ComputerMod;

internal sealed class ComputerEditor
{
    private readonly Plugin plugin;
    private ComputerEditorBuffer buffer = new();
    private readonly Dictionary<string, ComputerEditorBuffer> retainedDrafts = new(StringComparer.Ordinal);
    private bool resetSourceFocus;
    private readonly List<BaseInputModule> suspendedModules = new();
    private static ComputerEditor? capturing;
    private static bool installed;
    private static readonly Harmony InputHooks = new(Plugin.Id + ".editor-input");
    private static GUIStyle? fillStyle;
    private bool releasePending, captureReady, captureFailed, readyLogged, editLogged;
    private CursorLockMode previousLock;
    private bool previousVisible;
    private UIManager? cursorManager;
    private int closedFrame;
    private Vector2 sourceScroll, panelScroll;
    private GUIStyle? label, title, button, sourceStyle;
    private string notice = "";
    private string prompt = "";
    private Action? discardAction;
    private bool closingPrompt;
    private TextEditor? sourceEditor;
    private int sourceControl, completionIndex, completionStart, completionEnd, escapeFrame = -1;
    private bool sourceFocused, completionVisible, backendLogged;
    private LuaEdit? completionAt;
    private string[] completions = Array.Empty<string>();
    private Rect completionRect;
    private GUIStyle? completionStyle;
    private readonly LuaEditHistory history = new();
    private string historyText = "", historyKey = "";

    internal ComputerEditor(Plugin plugin) => this.plugin = plugin;
    internal bool IsOpen { get; private set; }
    // Main must gate gameplay controls with this, including the key-release quarantine after closing.
    internal static bool CapturesInput => capturing is not null;

    internal static void Install(Harmony harmony)
    {
        if (installed) return;
        // Patch the producer, not just accessors: Burst consumers can inline the latter.
        Patch(typeof(CustomRenderPipelineInputSystem), "Update", nameof(AfterNativeInput), true);
        Patch(typeof(CustomRenderPipeline), "HandleTextInput", nameof(AllowBackgroundInput));
        Patch(typeof(CustomRenderPipeline), "PullTypedStringIntoBuffer", nameof(BeforeTypedBuffer));
        // Only background UI modules are suspended. IMGUI does not use EventSystem; devices stay enabled.
        Patch(typeof(EventSystem), "Update", nameof(BeforeEventSystem));
        Patch(typeof(UIManager), "LateUpdate", nameof(AfterLateUpdate), true);
        installed = true;

        void Patch(Type type, string method, string patch, bool postfix = false)
        {
            var original = AccessTools.DeclaredMethod(type, method) ??
                throw new MissingMethodException(type.FullName, method);
            var hook = new HarmonyMethod(typeof(ComputerEditor), patch);
            harmony.Patch(original, prefix: postfix ? null : hook, postfix: postfix ? hook : null);
        }
    }

    private static void EnableInputHooks()
    {
        // These accessors are hot during gameplay. Detour them only while the editor captures input.
        InputHooks.UnpatchSelf();
        InputHooks.Patch(AccessTools.Method(typeof(Utility), "IsAnyInputFieldFocused"), prefix: new HarmonyMethod(typeof(ComputerEditor), nameof(BeforeInputFieldFocused)));
        InputHooks.Patch(AccessTools.Method(typeof(CRPInputSingleton), "GetKeyState"), prefix: new HarmonyMethod(typeof(ComputerEditor), nameof(BeforeKeyState)));
        foreach (string method in new[] { "IsKeyDown", "IsKeyUp", "IsKeyPressed" })
            InputHooks.Patch(AccessTools.Method(typeof(CRPInputSingleton), method), prefix: new HarmonyMethod(typeof(ComputerEditor), nameof(BeforeKey)));
    }

    private static bool AllowBackgroundInput() => !CapturesInput;
    private static bool BeforeEventSystem(EventSystem __instance)
    {
        var editor = capturing;
        if (editor is null) return true;
        try
        {
            // Skipping Update alone leaves InputSystemUIInputModule's callback-fed clicks queued.
            // OnDisable unhooks those actions and resets pointers; never disable Keyboard/Mouse devices.
            var modules = __instance.m_SystemInputModules;
            int start = editor.suspendedModules.Count;
            for (int i = 0; i < modules.Count; i++)
            {
                var module = modules[i];
                if (module is not null && module && module.enabled) editor.suspendedModules.Add(module);
            }
            // Disabling a module changes m_SystemInputModules, so iterate the managed snapshot instead.
            for (int i = start; i < editor.suspendedModules.Count; i++) editor.suspendedModules[i].enabled = false;
            if (start == 0 && editor.suspendedModules.Count != 0)
                editor.plugin.Log.LogInfo($"EDITOR UI CAPTURE: suspended {editor.suspendedModules.Count} background input module(s).");
        }
        catch (Exception ex) { editor.CaptureFailure(ex); }
        return false;
    }
    private static bool BeforeInputFieldFocused(ref bool __result)
    {
        if (!CapturesInput) return true;
        __result = true;
        return false;
    }
    private static bool BeforeKey(ref bool __result)
    {
        if (!CapturesInput) return true;
        __result = false;
        return false;
    }
    private static bool BeforeKeyState(ref CRPInputSingleton.State __result)
    {
        if (!CapturesInput) return true;
        __result = CRPInputSingleton.State.None;
        return false;
    }
    private static bool BeforeTypedBuffer(CustomRenderPipeline __instance, DynamicBuffer<CRPInputTypedString> __0)
    {
        if (!CapturesInput) return true;
        try
        {
            __0.Clear();
            var queued = __instance._typedString;
            if (queued.IsCreated) queued.Clear();
        }
        catch (Exception ex) { capturing?.CaptureFailure(ex); }
        return false;
    }
    private static void AfterNativeInput(EntityManager __0)
    {
        var editor = capturing;
        if (editor is null) return;
        try
        {
            // Native Update already obtains both singletons. Finish jobs before reacquiring them.
            // ponytail: world-wide synchronization while editing; narrow dependencies if profiled costly.
            __0.CompleteAllTrackedJobs();
            ComputerInteraction.ClearNativeInput(__0);
            if (!editor.captureReady)
            {
                editor.captureReady = true;
                editor.plugin.Log.LogInfo("EDITOR INPUT CAPTURE: native CRP snapshot cleared; key consumer consumed.");
            }
        }
        catch (Exception ex) { editor.CaptureFailure(ex); }
    }
    private static void AfterLateUpdate()
    {
        if (capturing is not { IsOpen: true } editor) return;
        try { editor.EnforceCursor(); }
        catch (Exception ex) { editor.CaptureFailure(ex); }
    }

    private void CaptureFailure(Exception ex)
    {
        if (captureFailed) return;
        captureFailed = true;
        HidePreservingDraft();
        plugin.SetStatus("Editor input capture failed; editor hidden, draft retained. " + ex.Message);
        plugin.Log.LogError(ex);
    }

    internal void Toggle()
    {
        if (IsOpen) { Close(); return; }
        if (!installed || captureFailed)
        {
            plugin.SetStatus("Editor unavailable: input capture patches must install successfully before opening.");
            return;
        }
        if (capturing is not null && !ReferenceEquals(capturing, this)) return;
        try
        {
            if (!releasePending)
            {
                previousLock = Cursor.lockState;
                previousVisible = Cursor.visible;
                cursorManager = UIManager._singleton;
            }
            releasePending = false;
            captureReady = false;
            capturing = this;
            IsOpen = true;
            EnableInputHooks();
            var world = World.DefaultGameObjectInjectionWorld;
            if (world is not null && world.IsCreated) AfterNativeInput(world.EntityManager);
            if (!IsOpen) return;
            prompt = "";
            var events = EventSystem.current;
            if (events is not null)
            {
                if (!events.alreadySelecting) events.SetSelectedGameObject(null);
                BeforeEventSystem(events);
                if (!IsOpen) return;
            }
            ObserveSelection();
            if (buffer.Dirty && buffer.Key != SelectionKey())
                Confirm("The retained-draft limit is reached. Discard this draft to open the targeted computer? Other drafts will be kept.", () =>
                { buffer.Discard(); ObserveSelection(); });
            EnforceCursor();
        }
        catch (Exception ex) { CaptureFailure(ex); }
    }

    internal void Close()
    {
        if (!IsOpen) return;
        if (!buffer.Dirty) { HidePreservingDraft(); return; }
        Confirm("Unsaved Lua draft. Nothing is saved automatically on close.", () =>
        {
            buffer.Discard();
            HidePreservingDraft();
        }, true);
    }

    // World change / forced hide must use this, not create a new editor or clear its buffer.
    internal void HidePreservingDraft()
    {
        if (!IsOpen) return;
        IsOpen = false;
        sourceFocused = completionVisible = false;
        sourceControl = 0;
        sourceEditor = null;
        prompt = "";
        discardAction = null;
        releasePending = true;
        closedFrame = Time.frameCount;
        RestoreCursor();
    }

    internal void Update()
    {
        try { UpdateCore(); }
        catch (Exception ex)
        {
            CaptureFailure(ex);
            // A broken release-key poll must not leave gameplay input captured forever.
            releasePending = false;
            if (ReferenceEquals(capturing, this)) capturing = null;
            InputHooks.UnpatchSelf();
            RestoreModules();
            RestoreCursor();
        }
    }

    private void UpdateCore()
    {
        if (releasePending)
        {
            // Do not replay the closing key/click, or a held movement key, into the game.
            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            bool held = keyboard is not null && keyboard.anyKey.isPressed || mouse is not null &&
                (mouse.leftButton.isPressed || mouse.rightButton.isPressed || mouse.middleButton.isPressed ||
                 mouse.backButton.isPressed || mouse.forwardButton.isPressed);
            if (Time.frameCount > closedFrame + 1 && !held)
            {
                releasePending = false;
                if (ReferenceEquals(capturing, this)) capturing = null;
                InputHooks.UnpatchSelf();
                RestoreModules();
                RestoreCursor();
                plugin.Log.LogInfo("EDITOR INPUT RELEASED: gameplay input restored; retained draft unchanged.");
            }
        }
        if (!IsOpen) return;
        ObserveSelection();
        EnforceCursor();
        if (Application.isFocused && Keyboard.current?.escapeKey.wasPressedThisFrame == true && escapeFrame != Time.frameCount)
        {
            escapeFrame = Time.frameCount;
            if (completionVisible && sourceFocused) completionVisible = false;
            else if (prompt.Length != 0) { prompt = ""; discardAction = null; }
            else Close();
        }
    }

    private void EnforceCursor()
    {
        if (!Application.isFocused) return;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        // The native game uses a transparent cursor while locked; visible=true alone is insufficient.
        Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
    }
    private void RestoreCursor()
    {
        try
        {
            Cursor.lockState = previousLock;
            Cursor.visible = previousVisible;
            // This is only UIManager's cached overlay mask, not the actual ECS overlay state.
            // Invalidate it so native Update restores the correct texture/lock for the CURRENT menu.
            if (cursorManager is not null && cursorManager) cursorManager._cursorOverlayState = uint.MaxValue;
            var current = UIManager._singleton;
            if (current is not null && current && current != cursorManager) current._cursorOverlayState = uint.MaxValue;
        }
        catch (Exception ex) { plugin.Log.LogWarning("Editor cursor restore failed: " + ex.Message); }
    }

    // Main calls this before unpatching on plugin unload, not on world changes.
    internal void Shutdown()
    {
        HidePreservingDraft();
        if (ReferenceEquals(capturing, this)) { capturing = null; RestoreCursor(); }
        RestoreModules();
        releasePending = false;
        installed = false;
        InputHooks.UnpatchSelf();
    }

    private void RestoreModules()
    {
        foreach (var module in suspendedModules)
        {
            try { if (module is not null && module && !module.enabled) module.enabled = true; }
            catch (Exception ex) { plugin.Log.LogWarning("Editor UI input restore failed: " + ex.Message); }
        }
        suspendedModules.Clear();
    }

    private string SelectionKey()
    {
        var t = plugin.SelectedTarget;
        return t is null ? "" : plugin.EditorScope + ":" + t.Guid + ":" + t.Component.Index + ":" + t.Component.Version + ":" +
            t.OwnerWorld?.Pointer + ":" + plugin.SelectedIsComputer;
    }
    private void ObserveSelection()
    {
        if (plugin.SelectedTarget is null && !IsOpen) return;
        string key = SelectionKey();
        var target = plugin.SelectedTarget;
        string name = target is null ? "Scratch draft" : $"{target.Name} [{target.Component.Index}:{target.Component.Version}]";
        if (buffer.Key != key)
        {
            bool retained = retainedDrafts.TryGetValue(key, out var next);
            // Keep at most 64 inactive dirty documents plus the active one; never evict an unsaved draft.
            if (buffer.Dirty && !retained && retainedDrafts.Count >= 64)
            { notice = "Retained-draft limit reached. Copy/discard this draft before opening another computer."; return; }
            if (retained) retainedDrafts.Remove(key);
            if (buffer.Dirty) retainedDrafts[buffer.Key] = buffer;
            buffer = next ?? new ComputerEditorBuffer();
            if (!retained) buffer.Load(key, name, plugin.SelectedIsComputer, "");
            sourceEditor = null; sourceControl = 0; sourceFocused = completionVisible = false;
            sourceScroll = default; completionAt = null; history.Clear(); resetSourceFocus = true;
            notice = "";
            plugin.Log.LogInfo($"EDITOR DOCUMENT: {name}; restoredDraft={retained}; retained={retainedDrafts.Count}.");
        }
        // Switching happens even while the new target's host source is pending: never show A as B.
        if (target is not null && !plugin.SourceReady) return;
        buffer.Observe(key, name,
            plugin.SelectedIsComputer, plugin.SelectedSource);
    }
    private void Confirm(string message, Action discard, bool closing = false)
    {
        sourceFocused = completionVisible = false;
        prompt = message;
        discardAction = discard;
        closingPrompt = closing;
    }
    private void UseStarter()
    {
        string key = SelectionKey();
        void Replace()
        {
            if (SelectionKey() != key || buffer.Key != key)
            { notice = "Target changed. Your draft was retained; reload the current computer before choosing a starter."; return; }
            buffer.Text = plugin.StarterSource;
            notice = "Starter inserted into the draft only. Nothing saved or run.";
        }
        if (buffer.Dirty) Confirm("Replace this unsaved draft with the starter template? Nothing will be saved or run.", Replace);
        else Replace();
    }
    private void Reload()
    {
        string key = SelectionKey();
        void Request()
        {
            if (SelectionKey() != key)
            { notice = "Target changed. Your draft was retained; request reload again."; return; }
            try
            {
                // RequestReload marks SourceReady false immediately; Observe waits for refreshed source.
                if (plugin.SelectedTarget is not null) plugin.RequestReload();
            }
            catch (Exception ex) { notice = "Reload failed; draft retained: " + ex.Message; return; }
            buffer.Discard();
            ObserveSelection();
            notice = plugin.SelectedTarget is null ? "Scratch draft discarded." : "Source reload requested; see status below.";
        }
        if (buffer.Dirty) Confirm("Discard the retained draft and reload this computer's source?", Request);
        else Request();
    }

    internal void Draw()
    {
        if (!IsOpen) return;
        var oldColor = GUI.color;
        var oldBackground = GUI.backgroundColor;
        var oldContent = GUI.contentColor;
        var oldMatrix = GUI.matrix;
        bool oldEnabled = GUI.enabled;
        int oldDepth = GUI.depth;
        try
        {
            ObserveSelection();
            EnsureStyles();
            GUI.depth = -1000;
            GUI.color = GUI.contentColor = Color.white;
            GUI.backgroundColor = Color.white;
            GUI.enabled = Application.isFocused;
            // Keep controls reachable on small game windows; normal resolutions are never scaled up.
            float scale = Math.Min(Screen.width / 720f, Math.Clamp(Screen.height / 900f, 0.75f, 1.5f));
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            float width = Screen.width / scale, height = Screen.height / scale;
            Fill(new Rect(0, 0, width, height), new Color(0.015f, 0.022f, 0.032f, 0.97f));
            float panelWidth = width - 24;
            var panel = new Rect((width - panelWidth) / 2, 12, panelWidth, Math.Max(80, height - 24));
            float contentHeight = Math.Max(520, panel.height - 18);
            panelScroll = GUI.BeginScrollView(panel, panelScroll, new Rect(0, 0, panelWidth - 20, contentHeight));
            try { DrawPanel(panelWidth - 20, contentHeight); }
            finally { GUI.EndScrollView(); }
            // Unhandled Tab belongs to IMGUI focus traversal, not the source editor.
            if ((Event.current.type is EventType.KeyDown or EventType.KeyUp or EventType.MouseDown or
                EventType.MouseUp or EventType.ScrollWheel or EventType.MouseDrag) && Event.current.keyCode != KeyCode.Tab)
                Event.current.Use();
            if (!readyLogged && Event.current.type == EventType.Repaint)
            {
                readyLogged = true;
                plugin.Log.LogInfo("EDITOR READY: IMGUI panel repainted; text entry and capture require runtime checks.");
            }
        }
        catch (Exception ex)
        {
            HidePreservingDraft();
            plugin.SetStatus("Editor hidden after GUI error; draft retained: " + ex.Message);
            plugin.Log.LogError(ex);
        }
        finally
        {
            GUI.color = oldColor; GUI.backgroundColor = oldBackground; GUI.contentColor = oldContent;
            GUI.matrix = oldMatrix; GUI.enabled = oldEnabled; GUI.depth = oldDepth;
        }
    }

    private void DrawPanel(float width, float height)
    {
        GUI.Label(new Rect(12, 4, width - 130, 30), "LUA / COMPUTER TERMINAL", title!);
        if (Button(new Rect(width - 112, 4, 100, 30), "Close [Esc]")) Close();
        var target = plugin.SelectedTarget;
        string selected = target is null ? "Scratch draft (no computer targeted)" :
            $"{target.Name} [{target.Component.Index}:{target.Component.Version}]";
        GUI.Label(new Rect(12, 39, width - 24, 24),
            selected + (plugin.SelectedTarget is null ? "" : plugin.IsSelectedRunning ? "  /  RUNNING" : "  /  STOPPED"), label!);
        Fill(new Rect(12, 68, width - 24, 2), new Color(0.18f, 0.78f, 0.70f));
        float x = 12, editorWidth = width - 24;
        float editTop = 146, editHeight = height - editTop - 222;
        bool sourceReady = plugin.SelectedTarget is null || plugin.SourceReady;
        bool canEdit = Application.isFocused && prompt.Length == 0 && captureReady && sourceReady &&
            buffer.Text.Length <= ComputerEditorBuffer.MaxLength;
        if (historyKey != buffer.Key || historyText != buffer.Text)
        {
            history.Clear();
            completionAt = null;
            completionVisible = false;
            historyKey = buffer.Key;
            historyText = buffer.Text;
        }
        if (!canEdit) sourceFocused = completionVisible = false;
        var current = Event.current;
        if (canEdit && sourceFocused && current.type == EventType.KeyDown &&
            (current.keyCode == KeyCode.Tab || current.character == '\t') && sourceControl != 0)
            GUIUtility.keyboardControl = sourceControl;
        if (canEdit && sourceFocused && completionVisible && current.type == EventType.MouseDown &&
            current.button == 0 && completionRect.Contains(current.mousePosition) && sourceEditor is not null &&
            GUIUtility.keyboardControl == sourceControl)
        {
            int row = (int)((current.mousePosition.y - completionRect.y - 4) / 22);
            if (row >= 0 && row < completions.Length) AcceptCompletion(sourceEditor, row);
            current.Use();
        }
        bool sourceClick = canEdit && current.type == EventType.MouseDown && current.button == 0 &&
            new Rect(x, editTop, editorWidth - 18, editHeight).Contains(current.mousePosition);
        if (current.type == EventType.MouseDown && !sourceClick)
        {
            sourceFocused = completionVisible = false;
            if (sourceControl != 0 && GUIUtility.keyboardControl == sourceControl) GUIUtility.keyboardControl = 0;
        }
        GUI.Label(new Rect(x, 80, editorWidth, 24),
            !sourceReady ? "WAITING FOR SOURCE / reload or host request pending" :
            "AU-08 / host execution, trusted teammate editing", label!);
        bool detached = buffer.Key != SelectionKey();
        bool sourceChanged = !detached && buffer.Dirty && buffer.Baseline != plugin.SelectedSource;
        GUI.Label(new Rect(x, 108, editorWidth, 34), buffer.Name + (buffer.Dirty ? "  * UNSAVED" : "  / saved snapshot") +
            (detached ? "  [draft for another target; save blocked]" : ""), label!);
        float textHeight = Math.Max(editHeight - 18, sourceStyle!.CalcHeight(new GUIContent(buffer.Text + "\n"), editorWidth - 30));
        sourceScroll = GUI.BeginScrollView(new Rect(x, editTop, editorWidth, editHeight), sourceScroll,
            new Rect(0, 0, editorWidth - 20, textHeight));
        try
        {
            GUI.enabled = canEdit;
            // Oversized disk text is retained intact and read-only, never silently truncated.
            var previousBackground = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.055f, 0.075f, 0.095f);
            try { DrawSource(new Rect(0, 0, editorWidth - 22, textHeight), canEdit, sourceClick); }
            finally { GUI.backgroundColor = previousBackground; }
        }
        finally { GUI.EndScrollView(); }
        GUI.enabled = Application.isFocused && prompt.Length == 0;
        float y = editTop + editHeight + 5;
        GUI.Label(new Rect(x, y, editorWidth, 22),
            $"{buffer.LineCount} lines | {buffer.Text.Length}/{ComputerEditorBuffer.MaxLength} chars | Click source to type", label!);
        y += 27;
        bool actionable = GUI.enabled && captureReady && !detached && plugin.SelectedTarget is not null &&
            plugin.SourceReady && !plugin.CommandPending;
        GUI.enabled = actionable && !sourceChanged && buffer.Text.Length <= ComputerEditorBuffer.MaxLength;
        float cell = (editorWidth - 18) / 4;
        if (Button(new Rect(x, y, cell, 30), "Save", true)) Save(false);
        if (Button(new Rect(x + cell + 6, y, cell, 30), "Save + Run", true)) Save(true);
        GUI.enabled = actionable;
        if (Button(new Rect(x + (cell + 6) * 2, y, cell, 30), "Run existing")) plugin.RequestRun();
        GUI.enabled = Application.isFocused && prompt.Length == 0 && plugin.SelectedTarget is not null;
        if (Button(new Rect(x + (cell + 6) * 3, y, cell, 30), "Stop")) plugin.RequestStop();
        y += 36;
        float third = (editorWidth - 12) / 3;
        GUI.enabled = Application.isFocused && prompt.Length == 0 && captureReady && !plugin.CommandPending;
        if (Button(new Rect(x, y, third, 28), "Reload / Discard")) Reload();
        GUI.enabled = Application.isFocused && prompt.Length == 0 && captureReady && sourceReady && !detached;
        if (Button(new Rect(x + third + 6, y, third, 28), "Starter template")) UseStarter();
        GUI.enabled = Application.isFocused && prompt.Length == 0;
        if (Button(new Rect(x + (third + 6) * 2, y, third, 28), "Copy draft"))
        { GUIUtility.systemCopyBuffer = buffer.Text; notice = "Draft copied to clipboard. No file written."; }
        GUI.Label(new Rect(12, height - 74, width - 24, 32),
            "API: input[_bool/_vec3](i), output(i,v), output_vec3(i,{x=0,y=0,z=0}) | state, dt, tick_id\n" +
            "Tab/Shift+Tab: 4 spaces | Ctrl+Space: suggest; arrows, Tab/Enter, Esc | Ctrl+Z/Y: undo/redo | host Lua", label!);
        string message = !captureReady ? "Waiting for native input capture. Editing is disabled until the producer hook succeeds." :
            !sourceReady ? "Waiting for refreshed source. Editing and Save/Run are blocked; see status, or retry Reload after an error." :
            sourceChanged ? "Cached source changed outside this draft. Save blocked: copy the draft, then Reload / Discard before merging." :
            detached ? "Your previous computer's draft is retained. Copy it or Reload / Discard to edit the current E target." : notice;
        GUI.Label(new Rect(x, y + 31, editorWidth, 36), message, label!);
        GUI.Label(new Rect(12, height - 39, width - 24, 38), plugin.Status + "\n" + plugin.NetworkStatus, label!);
        if (canEdit && sourceFocused && completionVisible && sourceEditor is not null)
            DrawCompletions(x, editTop, editorWidth, editHeight, height);
        if (prompt.Length != 0) DrawConfirmation(width, height);
    }

    private void DrawSource(Rect rect, bool canEdit, bool clicked)
    {
        if (resetSourceFocus)
        {
            // A different document must not inherit the previous TextArea's active keyboard/IME state.
            GUIUtility.keyboardControl = 0;
            resetSourceFocus = false;
        }
        if (GUIUtility.keyboardControl != sourceControl) sourceFocused = completionVisible = false;
        var before = new LuaEdit(buffer.Text, 0, 0);
        if (canEdit && sourceFocused && sourceEditor is not null && sourceEditor.text == buffer.Text)
        {
            before = ReadSource(sourceEditor, buffer.Text);
            RefreshCompletions(before);
            HandleSourceKey(sourceEditor, before);
            before = ReadSource(sourceEditor, buffer.Text);
        }
        // Keep Unity's actual text widget: mouse selection, clipboard, IME and ordinary navigation remain native.
        string edited = GUI.TextArea(rect, buffer.Text, Math.Max(ComputerEditorBuffer.MaxLength, buffer.Text.Length), sourceStyle!);
        int id = GUIUtility.keyboardControl;
        TextEditor? candidate = null;
        // A keyboard Tab can focus the area too. Inspect existing state; never create TextEditors for buttons.
        if (canEdit && id != 0 && GUIStateObjects.s_StateCache.TryGetValue(id, out var state))
            candidate = state.TryCast<TextEditor>();
        var position = candidate?.position ?? default;
        bool matches = candidate is not null && candidate.controlID == id && candidate.m_HasFocus &&
            position.x == rect.x && position.y == rect.y && position.width == rect.width && position.height == rect.height &&
            candidate.text == edited;
        if (canEdit && matches)
        {
            var native = GUIUtility.GetStateObject(Il2CppType.Of<TextEditor>(), id).Cast<TextEditor>();
            sourceControl = id;
            sourceEditor = native;
            sourceFocused = true;
            if (edited != buffer.Text)
            {
                if (edited.Length > ComputerEditorBuffer.MaxLength)
                {
                    ApplySource(native, before, "length limit", false);
                    notice = "Edit refused: source is limited to 16384 characters.";
                }
                else
                {
                    var after = ReadSource(native, edited);
                    history.Record(before, after);
                    buffer.Text = historyText = edited;
                    if (!editLogged) { editLogged = true; plugin.Log.LogInfo("EDITOR TEXT ENTRY: source buffer changed."); }
                }
            }
            RefreshCompletions(ReadSource(native, buffer.Text));
            if (!backendLogged)
            {
                backendLogged = true;
                plugin.Log.LogInfo("EDITOR TEXT BACKEND: native TextEditor state and UTF-16 caret access verified; custom edits use the same text area.");
            }
        }
        else if (canEdit && (edited != buffer.Text || clicked && Event.current.type == EventType.Used))
        {
            // No caret guessing: a native focus change must be identified before publishing an edit.
            throw new InvalidOperationException("Text changed without a verified source caret. Click the source area before typing.");
        }
        else sourceFocused = completionVisible = false;
    }

    private static LuaEdit ReadSource(TextEditor native, string text)
    {
        var edit = new LuaEdit(text, native.stringCursorIndex, native.stringSelectIndex);
        if (!LuaEditing.Valid(edit)) throw new InvalidOperationException("Native source selection has invalid UTF-16 offsets or length.");
        return edit;
    }

    private void ApplySource(TextEditor native, LuaEdit edit, string action, bool record = true)
    {
        if (!LuaEditing.Valid(edit)) throw new InvalidOperationException("Invalid source edit.");
        var before = record ? ReadSource(native, buffer.Text) : edit;
        native.text = edit.Text;
        native.UpdateTextHandle();
        // cursorIndex/selectIndex count rendered code points in Unity 6, not UTF-16 characters.
        native.m_TextEditing.stringCursorIndex = edit.Cursor;
        native.m_TextEditing.stringSelectIndex = edit.Anchor;
        if (ReadSource(native, edit.Text) != edit)
            throw new InvalidOperationException("Native TextEditor did not preserve the requested source selection.");
        if (record) history.Record(before, edit);
        buffer.Text = historyText = edit.Text;
        completionAt = edit;
        completionVisible = false;
        GUI.changed = true;
        plugin.Log.LogInfo($"EDITOR LUA {action}: chars={edit.Text.Length}, caret={edit.Cursor}, anchor={edit.Anchor}.");
    }

    private void RefreshCompletions(LuaEdit edit, bool force = false)
    {
        if (!force && completionAt == edit) return;
        completionAt = edit;
        completions = LuaEditing.Complete(edit, force, out completionStart, out completionEnd);
        completionIndex = 0;
        completionVisible = completions.Length != 0;
        if (force && !completionVisible) notice = "No completions at this caret (comments, strings and unknown members are excluded).";
    }

    private void AcceptCompletion(TextEditor native, int index)
    {
        var edit = ReadSource(native, buffer.Text);
        if (completionAt != edit || index < 0 || index >= completions.Length) { completionVisible = false; return; }
        if (LuaEditing.Replace(edit, completionStart, completionEnd, completions[index], out var result))
            ApplySource(native, result, "completion");
        else notice = "Completion refused: source is limited to 16384 characters.";
    }

    private void HandleSourceKey(TextEditor native, LuaEdit edit)
    {
        var e = Event.current;
        if (e.type != EventType.KeyDown) return;
        if (native.m_TextEditing.isCompositionActive) { completionVisible = false; return; }
        // Windows can emit a character event after the handled virtual key. Do not insert a second newline/tab.
        if (e.keyCode == KeyCode.None && e.character is '\t' or '\r' or '\n' or '\x1a' or '\x19')
        { e.Use(); return; }
        bool shortcut = (e.control || e.command) && !e.alt;
        if (e.keyCode == KeyCode.Escape)
        {
            if (completionVisible) { completionVisible = false; escapeFrame = Time.frameCount; }
            e.Use();
            return;
        }
        if (shortcut && e.keyCode == KeyCode.Space)
        {
            RefreshCompletions(edit, true);
            e.Use();
            return;
        }
        if (shortcut && e.keyCode is KeyCode.Z or KeyCode.Y)
        {
            bool redo = e.keyCode == KeyCode.Y || e.shift;
            ApplySource(native, history.Move(edit, redo), redo ? "redo" : "undo", false);
            e.Use();
            return;
        }
        if (e.control || e.command || e.alt) return;
        if (completionVisible && !e.shift)
        {
            if (e.keyCode is KeyCode.UpArrow or KeyCode.DownArrow)
            {
                completionIndex = (completionIndex + (e.keyCode == KeyCode.DownArrow ? 1 : completions.Length - 1)) % completions.Length;
                e.Use();
                return;
            }
            if (e.keyCode is KeyCode.Tab or KeyCode.Return or KeyCode.KeypadEnter)
            {
                AcceptCompletion(native, completionIndex);
                e.Use();
                return;
            }
        }
        if (e.keyCode is KeyCode.Tab or KeyCode.Return or KeyCode.KeypadEnter)
        {
            bool tab = e.keyCode == KeyCode.Tab;
            LuaEdit result;
            bool valid = tab ? LuaEditing.Indent(edit, e.shift, out result) : LuaEditing.Newline(edit, out result);
            if (valid) ApplySource(native, result, tab ? (e.shift ? "unindent" : "indent") : "newline");
            else notice = "Edit refused: source is limited to 16384 characters.";
            e.Use();
        }
    }

    private void DrawCompletions(float x, float top, float width, float editHeight, float panelHeight)
    {
        float w = Math.Min(310, width - 20), h = completions.Length * 22 + 24;
        Vector2 caret = sourceEditor!.graphicalCursorPos - sourceEditor.scrollOffset - sourceScroll;
        float left = Math.Clamp(x + caret.x, x, x + width - w - 18);
        float below = top + caret.y + 22;
        float y = below + h <= top + editHeight ? below : top + caret.y - h;
        y = Math.Clamp(y, top, Math.Max(top, panelHeight - h - 8));
        completionRect = new Rect(left, y, w, h);
        Fill(completionRect, new Color(0.07f, 0.11f, 0.15f));
        for (int i = 0; i < completions.Length; i++)
        {
            var row = new Rect(left + 3, y + 4 + i * 22, w - 6, 22);
            if (i == completionIndex) Fill(row, new Color(0.12f, 0.34f, 0.32f));
            GUI.Label(row, (i == completionIndex ? "> " : "  ") + completions[i], completionStyle!);
        }
        GUI.Label(new Rect(left + 3, y + h - 20, w - 6, 20), "Tab / Enter: insert     Esc: dismiss", completionStyle!);
    }

    private void Save(bool run)
    {
        try
        {
            plugin.RequestSave(buffer.Text, run);
            buffer.Submitted();
            notice = "Save requested; the draft remains unsaved until the cached source confirms it. See status for file conflicts.";
        }
        catch (Exception ex) { plugin.SetStatus("Save request failed; draft retained: " + ex.Message); }
    }

    private void DrawConfirmation(float width, float height)
    {
        GUI.enabled = Application.isFocused;
        float w = Math.Min(620, width - 24), x = (width - w) / 2, y = Math.Min(180, height / 3);
        Fill(new Rect(0, 74, width, height - 74), new Color(0.01f, 0.014f, 0.02f, 0.92f));
        Fill(new Rect(x, y, w, 174), new Color(0.12f, 0.16f, 0.20f));
        GUI.Label(new Rect(x + 16, y + 12, w - 32, 62), prompt, label!);
        if (Button(new Rect(x + 16, y + 86, w - 32, 30), "Keep editing / cancel", true))
        { prompt = ""; discardAction = null; }
        if (closingPrompt)
        {
            float half = (w - 40) / 2;
            if (Button(new Rect(x + 16, y + 124, half, 30), "Keep draft & close")) HidePreservingDraft();
            if (Button(new Rect(x + 24 + half, y + 124, half, 30), "Discard & close")) ApplyDiscard();
        }
        else if (Button(new Rect(x + 16, y + 124, w - 32, 30), "Discard & continue")) ApplyDiscard();
    }
    private void ApplyDiscard()
    {
        var action = discardAction;
        discardAction = null;
        prompt = "";
        action?.Invoke();
    }
    private bool Button(Rect rect, string text, bool accent = false)
    {
        var old = GUI.backgroundColor;
        GUI.backgroundColor = accent ? new Color(0.13f, 0.42f, 0.39f) : new Color(0.18f, 0.23f, 0.28f);
        bool clicked = GUI.Button(rect, text, button!);
        GUI.backgroundColor = old;
        return clicked;
    }
    private static void Fill(Rect rect, Color color)
    {
        fillStyle ??= new GUIStyle();
        fillStyle.normal.background = Texture2D.whiteTexture;
        var old = GUI.backgroundColor;
        GUI.backgroundColor = color;
        GUI.Box(rect, GUIContent.none, fillStyle);
        GUI.backgroundColor = old;
    }
    private void EnsureStyles()
    {
        if (label is not null) return;
        label = new GUIStyle { font = GUI.skin.font, fontSize = 13, wordWrap = true, richText = false };
        label.normal.textColor = new Color(0.82f, 0.87f, 0.91f);
        title = new GUIStyle { font = GUI.skin.font, fontSize = 21, fontStyle = FontStyle.Bold, richText = false };
        title.normal.textColor = new Color(0.28f, 0.88f, 0.78f);
        button = new GUIStyle { font = GUI.skin.font, fontSize = 13, alignment = TextAnchor.MiddleCenter,
            wordWrap = true, richText = false, padding = new RectOffset(5, 5, 3, 3) };
        foreach (var state in new[] { button.normal, button.hover, button.active, button.focused })
        { state.background = Texture2D.whiteTexture; state.textColor = Color.white; }
        button.hover.textColor = button.focused.textColor = new Color(0.4f, 1f, 0.88f);
        button.active.textColor = new Color(1f, 0.8f, 0.4f);
        sourceStyle = new GUIStyle { font = GUI.skin.font, fontSize = 14, wordWrap = true,
            richText = false, padding = new RectOffset(8, 8, 8, 8), alignment = TextAnchor.UpperLeft };
        sourceStyle.normal.background = Texture2D.whiteTexture;
        sourceStyle.focused.background = Texture2D.whiteTexture;
        sourceStyle.normal.textColor = sourceStyle.focused.textColor = new Color(0.91f, 0.95f, 0.96f);
        completionStyle = new GUIStyle { font = GUI.skin.font, fontSize = 13, richText = false,
            wordWrap = false, padding = new RectOffset(6, 6, 2, 2) };
        completionStyle.normal.textColor = new Color(0.88f, 0.96f, 0.94f);
    }
}

// Pure CLR buffer logic: disk/VM ownership remains entirely in Plugin.
internal sealed class ComputerEditorBuffer
{
    internal const int MaxLength = LuaEditing.MaxLength;
    internal string Key = "", Name = "Scratch draft", Text = "", Baseline = "";
    internal bool Native;
    private string? submitted;
    internal bool Dirty => Text != Baseline;
    internal int LineCount
    {
        get
        {
            int count = 1;
            for (int i = 0; i < Text.Length; i++)
                if (Text[i] == '\n' || Text[i] == '\r' && (i + 1 == Text.Length || Text[i + 1] != '\n')) count++;
            return count;
        }
    }
    internal void Load(string key, string name, bool native, string source)
    {
        Key = key; Name = name; Native = native; Text = Baseline = source; submitted = null;
    }
    internal void Observe(string key, string name, bool native, string source)
    {
        if (Key == key && submitted is not null && submitted == source)
        { Baseline = source; submitted = null; }
        if (!Dirty) Load(key, name, native, source);
    }
    internal void Submitted() => submitted = Text;
    internal void Discard() { Text = Baseline; submitted = null; }
    internal static void SelfTest()
    {
        var b = new ComputerEditorBuffer();
        b.Load("a", "A", true, "original\r\n");
        b.Text = "draft";
        b.Observe("b", "B", false, "other");
        Require(b.Text == "draft" && b.Key == "a" && b.Dirty, "dirty selection change");
        b.Observe("", "none", false, "");
        Require(b.Text == "draft" && b.Dirty, "world change");
        b.Observe("a", "A", true, "external edit");
        Require(b.Text == "draft" && b.Baseline == "original\r\n" && b.Dirty, "external file conflict retains original baseline");
        b.Submitted();
        b.Observe("a", "A", true, "original\r\n");
        Require(b.Dirty, "failed/pending save retains draft");
        b.Text = "newer";
        b.Observe("a", "A", true, "draft");
        Require(b.Text == "newer" && b.Baseline == "draft" && b.Dirty, "save acknowledgement preserves subsequent edits");
        b.Submitted();
        b.Observe("a", "A", true, "newer");
        Require(!b.Dirty, "save acknowledgement clears dirty");
        b.Observe("b", "B", false, "other");
        Require(b.Key == "b" && b.Text == "other", "clean selection change");
        b.Text = "unsaved"; b.Discard();
        Require(!b.Dirty && b.Text == "other", "explicit discard");
        b.Load("a", "A", true, "one\rtwo\r\nthree\n");
        Require(b.LineCount == 4 && b.Text == "one\rtwo\r\nthree\n", "line count and source preservation");
        b.Load("a", "A", true, new string('x', MaxLength + 1));
        Require(b.Text.Length == MaxLength + 1, "oversized source is not silently truncated");
        b.Load("a", "A", true, "original");
        b.Text = "function tick() output(1, input(1)) end";
        Require(b.Dirty && b.Baseline == "original", "explicit starter is draft-only, preserving saved baseline");
        b.Observe("a", "A", true, "original");
        Require(b.Text.StartsWith("function tick()", StringComparison.Ordinal), "starter draft survives source observation");
        b.Discard(); b.Observe("a", "A", true, "refreshed");
        Require(!b.Dirty && b.Text == "refreshed", "confirmed reload adopts refreshed source");
        b.Load("", "Scratch draft", false, ""); b.Text = "scratch";
        b.Observe("", "Scratch draft", false, "");
        Require(b.Dirty && b.Text == "scratch", "no-target editor permits a retained scratch draft");
        static void Require(bool pass, string test)
        { if (!pass) throw new InvalidOperationException("Editor buffer check failed: " + test); }
    }
}
