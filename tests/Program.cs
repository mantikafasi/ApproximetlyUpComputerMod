using ApproximatelyUp.ComputerMod;
using System.Diagnostics;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    passed++;
}
void Reject(Action action, string name, string? message = null)
{
    try { action(); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
        Check(message is null || ex.Message.Contains(message, StringComparison.Ordinal), name + ": " + ex.Message);
        return;
    }
    throw new Exception("FAIL (accepted): " + name);
}
double[] Eval(string body, double[]? inputs = null, int outputs = 1) =>
    new LuaComputer("function tick() " + body + " end", inputs?.Length ?? 0, outputs)
        .Tick(inputs ?? Array.Empty<double>(), 0.25, 7);

var timer = Stopwatch.StartNew();
var testDirectory = Path.Combine(Path.GetTempPath(), "AUComputerChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testDirectory);
try
{
    var programs = new ProgramStore(testDirectory);
    Check(programs.ReadTemplate() == ProgramStore.StarterSource, "missing template uses built-in starter without writing a file");
    Check(!File.Exists(Path.Combine(testDirectory, "computer.lua")), "starter fallback leaves user files untouched");
    File.WriteAllText(Path.Combine(testDirectory, "computer.lua"), "  \n");
    Check(programs.ReadTemplate() == ProgramStore.StarterSource, "empty template uses built-in starter");
    const string customTemplate = "function tick() output(1, 7) end";
    File.WriteAllText(Path.Combine(testDirectory, "computer.lua"), customTemplate);
    Check(programs.ReadTemplate() == customTemplate, "nonempty local template is preserved");
    var starter = new LuaComputer(ProgramStore.StarterSource, 8, 8).Tick(new[] { 3.0, 0, 0, 0, 0, 0, 0, 0 }, 1.0 / 60, 1);
    Check(starter.SequenceEqual(new[] { 3.0, 0, 0, 0, 0, 0, 0, 0 }), "starter compiles and passes Input 1 to Output 1 only");
    var id = programs.Create("function tick() output(1, 1) end");
    Check(ProgramStore.ValidId(id) && programs.Load(id).Contains("output(1, 1)"), "immutable source roundtrip");
    var fork = programs.Create("function tick() output(1, 2) end");
    Check(fork != id && programs.Load(id).Contains("output(1, 1)"), "copy edit forks without changing original");
    Check(programs.LegacyId("world", "block") is null, "unassigned legacy block");
    programs.BindLegacy("world", "block", null, id);
    Check(programs.LegacyId("world", "block") == id && programs.LegacyId("other-world", "block") is null, "legacy world isolation");
    programs.BindLegacy("world", "block", id, fork);
    Check(programs.LegacyId("world", "block") == fork, "atomic reference replacement");
    foreach (var bad in new[] { "../escape", "ABCDEFGHIJKL", "00000000000", "abcdefabcdef", "" })
    {
        try { programs.Load(bad); throw new Exception("accepted bad program path"); }
        catch (InvalidDataException) { Check(true, "program ID rejects traversal/invalid token"); }
    }
    try { programs.BindLegacy("world", "block", id, id); throw new Exception("binding conflict overwritten"); }
    catch (IOException) { Check(programs.LegacyId("world", "block") == fork, "binding conflict preserved"); }
    foreach (var bad in new[] { new string('x', 16385), "bad\0source", "\x1bLua" })
    {
        try { programs.Create(bad); throw new Exception("accepted bad source"); }
        catch (InvalidDataException) { Check(true, "source storage bounds"); }
    }
}
finally { Directory.Delete(testDirectory, true); }
foreach (int status in new[] { -1, 0, 1, 2, 3, 4, 99 })
{
    Check(!SessionSafety.CanWrite(false, status, false), "never run on a non-authoritative client " + status);
    Check(!SessionSafety.CanWrite(true, status, true), "connected peers always block " + status);
    Check(SessionSafety.CanWrite(true, status, false) == (status == 2 || status == 3), "solo native server statuses " + status);
    Check(SessionSafety.CanWrite(true, status, true, true) == (status == 2 || status == 3), "AU-08 host permits teammates " + status);
    Check(!SessionSafety.CanWrite(false, status, true, true), "AU-08 clients never execute Lua " + status);
}
Check(!SessionSafety.IsRemotePeer(true, 42, 42), "online self is not a remote peer");
Check(SessionSafety.IsRemotePeer(true, 43, 42), "online remote peer detected");
Check(!SessionSafety.IsRemotePeer(false, 43, 42), "disconnected peer record is ignored");
Check(SessionSafety.IsRemotePeer(true, 0, 0) && SessionSafety.IsRemotePeer(true, 42, 0) &&
    SessionSafety.IsRemotePeer(true, 0, 42), "unknown online identities fail closed");
LuaComputer.SelfTest();
Check(true, "SelfTest");
Check(Eval("output(1, (input(1) + input(2)) * dt + tick_id)", new[] { 2.0, 6.0 })[0] == 9, "arithmetic/dt/tick_id");
Check(Eval("output(1, input_bool(1)); output(2, input_bool(2)); output(3, input_bool(3))",
    new[] { 0.49, 0.5, -1.0 }, 3).SequenceEqual(new[] { 0.0, 1.0, 0.0 }), "boolean threshold");
Check(Eval("local v = input_vec3(2); v.y = v.y * 2; output_vec3(2, v)",
    new[] { 99.0, 1.0, 2.0, 3.0 }, 5).SequenceEqual(new[] { 0.0, 1.0, 4.0, 3.0, 0.0 }), "vector offsets/default zero");
Check(Eval("output_vec3(1, {x = true, y = false, z = 4})", outputs: 3).SequenceEqual(new[] { 1.0, 0.0, 4.0 }), "vector boolean scalars");
Check(Eval("output(1, 0)")[0] == 0 && Eval("", outputs: 0).Length == 0, "zero value/zero ports");
Check(Eval("output(8, input(8))", Enumerable.Range(1, 8).Select(i => (double)i).ToArray(), 8)[7] == 8, "eight ports");
Check(Eval("output(1, -1e20); output(2, 1e20)", outputs: 2).SequenceEqual(new[] { -1e20, 1e20 }), "output boundaries");
Check(Eval("output(1, input(1)); output(2, input(2))", new[] { -(double)1e20f, (double)1e20f }, 2)
    .SequenceEqual(new[] { -(double)1e20f, (double)1e20f }), "native float32 boundaries survive passthrough");
Check(Eval("assert(type(state) == 'table'); assert(type(input) == 'function'); output(1, math.max(math.abs(-2), math.sqrt(9)))")[0] == 3,
    "math/type/assert");
Check(Eval("output(1, math.floor(math.pi)); output(2, math.fmod(7, 4))", outputs: 2).SequenceEqual(new[] { 3.0, 3.0 }), "math constants/binary");
Check(Eval("math.randomseed(1); local a=math.random(); math.randomseed(1); output(1, a==math.random() and 1 or 0)")[0] == 1, "seeded random");
Check(Eval("math.randomseed(3); output(1, math.random(1,1))")[0] == 1, "random integer bounds");
Check(Eval("local sum=0; for i=1,100 do sum=sum+i end; output(1,sum)")[0] == 5050, "finite loop completes across VM slices");
Check(Eval("return {anything = function() end}")[0] == 0, "Lua return values never escape to host");

const string stateSource = "state.n = 10; n = 0; function tick() state.n = state.n + 1; n = n + 1; output(1, state.n + n) end";
var a = new LuaComputer(stateSource, 0, 1);
var b = new LuaComputer(stateSource, 0, 1);
var first = a.Tick(Array.Empty<double>(), 0, 0);
Check(first[0] == 12 && a.Tick(Array.Empty<double>(), 0, 1)[0] == 14 && b.Tick(Array.Empty<double>(), 0, 1)[0] == 12, "persistent and isolated state");
Check(first[0] == 12, "successful outputs not reused");
first[0] = -99;
Check(a.Tick(Array.Empty<double>(), 0, 2)[0] == 16, "caller output edits do not affect VM state");
var defaults = new LuaComputer("function tick() if tick_id == 0 then output(1, 42) end end", 0, 1);
Check(defaults.Tick(Array.Empty<double>(), 0, 0)[0] == 42 && defaults.Tick(Array.Empty<double>(), 0, 1)[0] == 0, "outputs reset each tick");

foreach (string global in new[] { "io", "os", "file", "package", "require", "load", "loadfile", "dofile", "loadsafe", "loadfilesafe",
    "debug", "coroutine", "dynamic", "json", "clr", "luanet", "CS", "import", "_MOONSHARP", "print", "collectgarbage", "pcall", "xpcall",
    "string", "table", "setmetatable", "getmetatable", "tonumber", "tostring" })
    Check(Eval("output(1, " + global + " == nil)")[0] == 1, "forbidden " + global);
Reject(() => Eval("output(1, ('x'):rep(1000000))"), "no string metatable operations");
foreach (string index in new[] { "0", "-1", "2", "1.5", "0/0", "1/0", "-1/0", "'1'", "true", "{}", "nil", "1e100" })
{
    Reject(() => Eval("output(" + index + ", 1)"), "output index " + index);
    Reject(() => Eval("output(1, input(" + index + "))", new[] { 1.0 }), "input index " + index);
    Reject(() => Eval("output(1, input_bool(" + index + "))", new[] { 1.0 }), "boolean index " + index);
}
foreach (string value in new[] { "0/0", "1/0", "-1/0", "1e21", "-1e21", "'2'", "nil", "{}", "function() end" })
    Reject(() => Eval("output(1, " + value + ")"), "invalid output " + value);
foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
{
    Reject(() => Eval("", new[] { value }), "nonfinite input");
    Reject(() => new LuaComputer("function tick() end", 0, 0).Tick(Array.Empty<double>(), value, 0), "nonfinite dt");
}
Reject(() => new LuaComputer("function tick() end", 1, 0).Tick(Array.Empty<double>(), 0, 0), "wrong input count");
Reject(() => new LuaComputer("function tick() end", 0, 0).Tick(null!, 0, 0), "null inputs");
Reject(() => Eval("input(1)", outputs: 0), "zero input ports reject access");
Reject(() => Eval("output(1, 0)", outputs: 0), "zero output ports reject access");
Reject(() => Eval("input_vec3(2)", new[] { 1.0, 2.0, 3.0 }), "vector input span");
Reject(() => Eval("output_vec3(2, {x=1, y=2, z=3})", outputs: 3), "vector output span");
Reject(() => Eval("output_vec3(1, {x=1, y=2})", outputs: 3), "missing vector component");
Reject(() => Eval("output_vec3(1, {x=1, y=2, z=0/0})", outputs: 3), "invalid vector component");
Reject(() => Eval("output_vec3(1, 3)", outputs: 3), "non-table vector");
Reject(() => Eval("output(1, math.abs('2'))"), "math rejects string coercion");
Reject(() => Eval("output(1, math.log(8, 2))"), "unary math rejects unsupported extra argument");
Reject(() => Eval("output(1, math.min(3, 2, 1))"), "binary math rejects unsupported varargs");
Reject(() => Eval("output(1, math.sqrt(-1))"), "math nonfinite result rejected at output");

foreach (int count in new[] { -1, 9, int.MinValue, int.MaxValue })
{
    Reject(() => new LuaComputer("function tick() end", count, 0), "input count bounds");
    Reject(() => new LuaComputer("function tick() end", 0, count), "output count bounds");
}
Reject(() => new LuaComputer(null!, 0, 0), "null source");
Reject(() => new LuaComputer(new string(' ', 16385), 0, 0), "source length limit");
Check(new LuaComputer("function tick() end".PadRight(16384), 0, 0).Tick(Array.Empty<double>(), 0, 0).Length == 0, "max source length accepted");
Reject(() => new LuaComputer("function tick(", 0, 0), "invalid source");
Reject(() => new LuaComputer("", 0, 0), "missing tick");
Reject(() => new LuaComputer("tick = input", 0, 0), "tick must be Lua function");
Reject(() => new LuaComputer("MoonSharp_dump_b64::AA==", 0, 0), "bytecode rejected");
Reject(() => new LuaComputer("\x1bLua", 0, 0), "native bytecode rejected");
Reject(() => new LuaComputer("input(1); function tick() end", 1, 0), "initialization port access rejected");
Reject(() => new LuaComputer("error('initialization failed'); function tick() end", 0, 0), "initialization error");
Reject(() => new LuaComputer("while true do end", 0, 0), "infinite initialization", "instruction budget");
Reject(() => Eval("while true do end"), "infinite tick", "instruction budget");
Reject(() => Eval("for i=1,10000 do end"), "finite work exceeding tick budget", "instruction budget");
Reject(() => Eval("local function f() return f() end; f()"), "infinite recursion", "instruction budget");
Reject(() => Eval("local s = 'abcdefgh'; for i=1, 30 do s = s .. s end"), "tick allocation guard", "allocation budget");
Reject(() => new LuaComputer("local s = 'abcdefgh'; for i=1, 30 do s=s..s end; function tick() end", 0, 0),
    "initialization allocation guard", "allocation budget");

foreach (string failure in new[] { "error('broken')", "output(1, 0/0)", "output_vec3(1, {x=1,y=2,z=0/0})", "while true do end",
    "local s='abcdefgh'; for i=1,30 do s=s..s end" })
{
    var broken = new LuaComputer("function tick() output(1, tick_id); if tick_id > 0 then state.changed = true; " + failure + " end end", 0, 3);
    double[] accepted = broken.Tick(Array.Empty<double>(), 0, 0);
    Reject(() => accepted = broken.Tick(Array.Empty<double>(), 0, 1), "whole tick rejected: " + failure);
    Check(accepted.SequenceEqual(new[] { 0.0, 0.0, 0.0 }), "no partial publication: " + failure);
    Reject(() => broken.Tick(Array.Empty<double>(), 0, 2), "execution failure faults instance", "faulted");
}
try { Eval("error('" + new string('x', 1000) + "')"); throw new Exception("FAIL: error accepted"); }
catch (InvalidOperationException ex) { Check(ex.Message.Length <= 261 && ex.InnerException is null, "bounded error message"); }
var retry = new LuaComputer("function tick() output(1, input(1)) end", 1, 1);
Reject(() => retry.Tick(new[] { double.NaN }, 0, 0), "invalid host input rejected before execution");
Check(retry.Tick(new[] { 2.0 }, 0, 0)[0] == 2, "host validation does not fault unexecuted VM");
Console.WriteLine($"PASS: {passed} checks in {timer.ElapsedMilliseconds} ms");
