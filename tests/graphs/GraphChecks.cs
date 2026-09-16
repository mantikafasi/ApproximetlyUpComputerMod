#if GRAPH_CHECKS
using ApproximatelyUp.ComputerMod;
using System.Text.Json;

int count = 0;
void Check(bool pass, string name) { if (!pass) throw new Exception(name); count++; }
void Reject(Action action, string name) { try { action(); } catch { count++; return; } throw new Exception("Accepted: " + name); }
var vm = new LuaComputer(@"
function tick()
  state.n = (state.n or 0) + 1
  if state.n == 1 then
    graph.axes(1, 'TEST', 'X', 'Y', 0, 10, -1, 2)
    local points = {{0, 0}, {2, 1}, false, {4, 2}}
    graph.series(1, 1, points, 'TRACE')
    points[1][2] = 999
    graph.marker(1, 1, 2, 1, 'POINT')
    graph.series(2, 1, {{1, 3}}, 'SECOND', 2)
  end
  output(1, 0.5)
end", 8, 8);
Check(vm.Tick(new double[8], 1d/60, 1)[0] == .5, "numeric output coexists with graph");
var first = vm.Graphs[1];
Check(first.Series[0]!.Points[0][1] == 0, "Lua table copied");
Check(first.Series[0]!.Points[2].Length == 0, "explicit gap");
Check(vm.Graphs[2].Series[0]!.Points[0][1] == 3, "output channels independent");
vm.Tick(new double[8], 1d/60, 2);
Check(ReferenceEquals(first, vm.Graphs[1]), "no plotting calls retain previous frame");
first.Validate();
var drawing = GraphDrawing.Build(first, "LIVE");
Check(drawing.Count > 100 && drawing.All(l => float.IsFinite(l.X+l.Y+l.X2+l.Y2) && l.X>=0 && l.X<640 && l.Y>=0 && l.Y<480), "bounded finite drawing");
var failing = new LuaComputer("function tick() graph.series(1,1,{{0,0},{1,1}}) error('fail') end",1,1);
Reject(() => failing.Tick(new[]{0.0},1d/60,1),"failed tick");
Check(failing.Graphs.Count==0,"failed tick publishes no graph");
Reject(() => failing.Tick(new[]{0.0},1d/60,2),"faulted VM cannot run again");
Reject(() => new LuaComputer("graph.clear(1) function tick() end",1,1),"graph outside tick");
foreach(string code in new[]{"graph.clear(9)","graph.series(1,5,{})","graph.series(1,1,{{0,0/0}})","graph.axes(1,'X','X','Y',2,1)","graph.marker(1,9,1,1,'X')"})
    Reject(() => new LuaComputer("function tick() "+code+" end",8,8).Tick(new double[8],1,1),code);
Reject(() => new LuaComputer("function tick() local p={} for i=1,257 do p[i]={i,i} end graph.series(1,1,p) end",1,1).Tick(new[]{0.0},1,1),"point limit");
var isolated = new LuaComputer("function tick() end",1,1); isolated.Tick(new[]{0.0},1,1);
Check(isolated.Graphs.Count==0,"VM graph isolation");
var clear = new LuaComputer("function tick() graph.series(1,1,{{1,1}}) graph.clear(1) end",1,1); clear.Tick(new[]{0.0},1,1);
Check(clear.Graphs[1].Series.All(s=>s is null),"clear removes series");

var history = new GraphHistory(); var settings = new GraphSettings();
for(int i=0;i<7205;i++) history.Sample(i,1d/60,new double?[]{i==7100?999:1,-i,null,0});
Check(history.Count==7201,"history is bounded");
double t=history.Time; history.Sample(7204,1d/60,new double?[4]); Check(history.Time==t,"duplicate ticks ignored");
var hf=history.Frame(settings);hf.Validate();
Check(hf.Series[0]!.Points.Any(p=>p.Length==2 && p[1]==999),"minmax reduction preserves spike");
Check(hf.Series.All(s=>s!.Points.Length<=256),"history fits frame limits");
Check(hf.Series[2]!.Points.All(p=>p.Length==0),"disconnected input is a gap");
Check(hf.Series[3]!.Points.Any(p=>p.Length==2 && p[1]==0),"valid zero is not disconnected");
history.Sample(0,1d/60,new double?[]{1,2,3,4});Check(history.Count==1,"rewind clears old history");
history.Clear();Check(history.Count==0 && history.Time==0,"clear resets history clock");
Reject(()=>new GraphSettings{Seconds=121}.Validate(),"history duration bound");
Reject(()=>new GraphFrame{YRange=new[]{0.0,double.PositiveInfinity}}.Validate(),"invalid ranges");
var same=new GraphFrame();same.Series[0]=new GraphSeries{Points=new[]{new[]{2.0,0.0},new[]{2.0,0.0}}};
Check(same.Bounds(true).Min<2 && same.Bounds(true).Max>2,"constant range expands");
var huge=new GraphFrame{XRange=new[]{0.0,1.0},YRange=new[]{0.0,1.0}};
huge.Series[0]=new GraphSeries{Points=new[]{new[]{-1e19,-1e19},new[]{1e19,1e19}}};
Check(GraphDrawing.Build(huge,"CLIPPED").All(l=>float.IsFinite(l.X+l.Y+l.X2+l.Y2)),"huge coordinates clipped");

var client = new ComputerLinkPeer(true,"",0); var host = new ComputerLinkPeer(false,ComputerPacket.NewNonce(),0);
var welcome=host.AcceptControl(client.Heartbeat()!,0)!;var ack=client.AcceptControl(welcome,0)!;var ready=host.AcceptControl(ack,0)!;
client.AcceptControl(ready,0);
var msg=new ComputerMessage{Kind="graph-state",Id=1,Target="01234567-01234567-01234567-01234567",ProgramId="012345ABCDEF",Graph=new GraphSnapshot{Frame=first}};
var packet=host.Wrap(msg,1);var bytes=ComputerPacket.Encode(packet,true);
var decoded=ComputerPacket.Decode(bytes,true);Check(decoded.Message!.Graph!.Frame.Series[0]!.Points[2].Length==0,"graph wire gap roundtrip");
Check(client.AcceptMessage(decoded,0),"authenticated snapshot accepted");Check(!client.AcceptMessage(decoded,0),"duplicate snapshot rejected");
Reject(()=>ComputerPacket.Decode(bytes,false),"client cannot publish snapshots");
msg.Graph!.Frame.Series[0]!.Points=new double[257][];Reject(()=>ComputerPacket.Encode(packet,true),"wire point limit");
msg.Graph.Frame=hf;Check(ComputerPacket.Encode(packet,true).Length<=65536,"full history fits wire cap");
msg.Graph.Frame=new GraphFrame{Title=new string('A',49)};Reject(()=>ComputerPacket.Encode(packet,true),"wire label length");
msg.Graph.Frame=first.Copy(); msg.Graph.Frame.Series=new GraphSeries?[5];Reject(()=>ComputerPacket.Encode(packet,true),"wire series count");
Console.WriteLine($"PASS: {count} graph/Lua/history/drawing/session checks");
#endif
