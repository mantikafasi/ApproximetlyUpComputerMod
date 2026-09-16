using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace ApproximatelyUp.ComputerMod;

internal sealed class GraphScreens
{
    private sealed class Screen : IDisposable
    {
        internal PortTarget Target;
        internal string? Id;
        internal bool Loaded, LabelPending;
        internal GraphSnapshot Snapshot = new();
        internal readonly GraphHistory History = new();
        internal readonly GraphSurface Surface;
        internal Entity[] Sources = new Entity[4];
        internal GraphSnapshot? Incoming;
        internal string? IncomingId;
        internal long NextDraw, ReplyId, Deadline;
        internal long ReceivedAt;
        internal Screen(EntityManager m, PortTarget target) { Target=target; Surface=new(m,target.Component); Surface.Update(Snapshot.Frame,"CONNECT A DATA CABLE"); }
        public void Dispose() => Surface.Dispose();
    }
    private readonly Plugin plugin;
    private readonly string directory;
    private readonly Dictionary<string, Screen> screens = new(StringComparer.Ordinal);
    private long nextScan, nextRemote;
    private int nextScreen;
    private string? opened;
    private (string Guid, string? Id, GraphSettings? Settings, bool Clear)? command;
    private string ymin="", ymax="";
    private string uiNotice="";
    private string session="";
    private bool wasFlight;
    private GUIStyle? title, label, button, field;
    private static GUIStyle? fill;
    internal GraphScreens(Plugin plugin,string directory) { this.plugin=plugin; this.directory=Path.Combine(directory,"graphs"); }
    internal bool Due(long now) => screens.Count!=0 || command is not null || now>=nextScan;
    internal void Reset()
    {
        foreach(var s in screens.Values) s.Dispose();
        screens.Clear(); command=null; opened=null; nextScan=nextRemote=0; session=""; wasFlight=false;
    }
    internal void StopFlight()
    {
        foreach(var s in screens.Values)
        {
            s.History.Clear(); s.Incoming=null; s.ReplyId=0;
            s.Snapshot.Frame=new(); s.Snapshot.Status="SIMULATION RESET"; s.NextDraw=0;
        }
        command=null;
    }
    internal void Tick(EntityManager manager,ComputerLink link,bool advancing,bool flight,int tick,double dt,bool rewind)
    {
        long now=Environment.TickCount64;
        if(session!=link.Session) { StopFlight(); session=link.Session; foreach(var s in screens.Values) s.Loaded=false; }
        if(rewind || flight!=wasFlight) StopFlight();
        wasFlight=flight;
        if(now>=nextScan)
        {
            nextScan=now+1000;
            Discover(manager);
        }
        if(command is { } c)
        {
            command=null;
            try
            {
                if(link.IsClient)
                {
                    var msg=new ComputerMessage {Kind=c.Clear?"graph-clear":"graph-settings",Target=c.Guid,ProgramId=c.Id,
                        Graph=c.Settings is null?null:new GraphSnapshot { Settings=c.Settings }};
                    if(!link.SendToHost(msg)) throw new IOException(link.Status);
                    if(screens.TryGetValue(c.Guid,out var s)) {s.ReplyId=msg.Id;s.Deadline=now+10000;}
                    uiNotice="Settings sent; waiting for host.";
                }
                else { Apply(manager,c.Guid,c.Id,c.Settings,c.Clear); uiNotice="Screen updated."; }
            }
            catch(Exception ex) { uiNotice=ex.Message; plugin.SetStatus("Graph: "+ex.Message); }
        }
        foreach(var s in screens.Values.ToArray())
        {
            try
            {
                var live=GraphScreen.Target(manager,s.Target.Component);
                if(live.Guid!=s.Target.Guid) throw new InvalidOperationException("Screen identity changed.");
                s.Target=live;
                if(link.IsClient)
                {
                    if(s.Incoming is { } incoming && GraphScreen.ReadId(manager,live.Component)==s.IncomingId && incoming.Sources.SequenceEqual(SourceKeys(manager,live)))
                    { s.Snapshot=incoming;s.Id=s.IncomingId;s.Loaded=true;s.Incoming=null;s.NextDraw=0;s.ReceivedAt=now; }
                    if(!s.Snapshot.Sources.SequenceEqual(SourceKeys(manager,live)))
                    {s.Snapshot.Frame=new();s.Snapshot.Status="CONNECTION CHANGED";s.Loaded=false;}
                    if(!link.Ready) s.Loaded=false;
                    if(s.ReplyId!=0 && now>s.Deadline) {s.ReplyId=0;uiNotice="Graph reply timed out; refresh before retrying a settings change.";}
                }
                else
                {
                    LoadSettings(manager,s);
                    if(s.LabelPending)
                    { try { ComputerNetwork.PublishProgramId(manager,s.Target);s.LabelPending=false; } catch { /* Current committed ID is retried, never recreated. */ } }
                    UpdateHost(manager,s,advancing,flight,tick,dt,now);
                }
                if(now>=s.NextDraw)
                {
                    s.Surface.Update(s.Snapshot.Frame,!s.Loaded?"WAITING FOR HOST":link.IsClient && now-s.ReceivedAt>2000?"STALE / WAITING FOR HOST":s.Snapshot.Status); s.NextDraw=now+100;
                }
            }
            catch(Exception ex)
            {
                if(!GraphScreen.IsScreen(manager,s.Target.Component) || !manager.HasComponent<SCGuid>(s.Target.Component) || manager.GetComponentData<SCGuid>(s.Target.Component).ToString()!=s.Target.Guid)
                {s.Dispose();screens.Remove(s.Target.Guid);}
                else { s.Snapshot.Status="GRAPH UNAVAILABLE"; if(now>=s.NextDraw) {s.Surface.Update(new(),"GRAPH UNAVAILABLE");s.NextDraw=now+1000;} }
                if(opened==s.Target.Guid) uiNotice=ex.Message;
            }
        }
        // One poll per 100 ms across all screens; current editor commands get the other transport slots.
        if(link.IsClient && link.Ready && now>=nextRemote && screens.Count!=0)
        {
            nextRemote=now+100;
            var s=screens.Values.ElementAt(nextScreen++ % screens.Count);
            if(s.ReplyId==0)
            {
                var msg=new ComputerMessage {Kind="graph-get",Target=s.Target.Guid};
                if(link.SendToHost(msg)) {s.ReplyId=msg.Id;s.Deadline=now+10000;}
            }
        }
    }
    private void Discover(EntityManager manager)
    {
        var q=manager.CreateEntityQuery(new[] {ComponentType.ReadOnly<SCGuid>(),ComponentType.ReadOnly<SCPrefab>(),ComponentType.ReadOnly<SpaceshipElectricPortRef>()});
        try
        {
            if(q.CalculateEntityCount()>4096) throw new InvalidOperationException("Graph discovery exceeds 4096 entities.");
            var es=q.ToEntityArray(Allocator.Temp);
            try
            {
                for(int i=0;i<es.Length;i++)
                {
                    if(!GraphScreen.IsScreen(manager,es[i])) continue;
                    string id=manager.GetComponentData<SCGuid>(es[i]).ToString();
                    if(screens.ContainsKey(id) || screens.Count>=16) continue;
                    var target=GraphScreen.Target(manager,es[i]); screens[id]=new(manager,target);
                }
            }
            finally {es.Dispose();}
        }
        finally {q.Dispose();}
    }
    private void LoadSettings(EntityManager manager,Screen s)
    {
        string? id=GraphScreen.ReadId(manager,s.Target.Component);
        if(s.Loaded && s.Id==id) return;
        GraphSettings settings=new();
        if(id is not null)
        {
            string path=Path.Combine(directory,id+".json");
            if(new FileInfo(path).Length>4096) throw new InvalidDataException("Oversized screen settings.");
            settings=JsonSerializer.Deserialize<GraphSettings>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Empty screen settings.");
            settings.Validate();
        }
        s.Id=id;s.Loaded=true;s.Snapshot.Settings=settings;s.History.Clear();s.NextDraw=0;
    }
    private void UpdateHost(EntityManager manager,Screen s,bool advancing,bool flight,int tick,double dt,long now)
    {
        var inputs=new double?[4];var sources=new Entity[4];
        for(int i=0;i<4;i++)
        {
            var p=manager.GetComponentData<SpaceshipElectricPort>(s.Target.Inputs[i]);
            sources[i]=p._runtimeEndEntity;
            if(p._runtimeType==SpaceshipPortType.DataInput && manager.Exists(sources[i]) && manager.HasComponent<SpaceshipElectricPort>(sources[i]))
            {
                var src=manager.GetComponentData<SpaceshipElectricPort>(sources[i]);
                if(src._runtimeType==SpaceshipPortType.DataOutput && GraphFrame.Number(p._portValue)) inputs[i]=p._portValue;
            }
        }
        if(!sources.AsSpan().SequenceEqual(s.Sources))
        {s.Sources=sources;s.History.Clear();s.Snapshot.Frame=new();s.NextDraw=0;}
        s.Snapshot.Sources=SourceKeys(manager,s.Target);
        var settings=s.Snapshot.Settings;
        if(settings.Programmed && !flight) {s.Snapshot.Status="BUILD MODE";return;}
        bool active=manager.HasComponent(s.Target.Component,ComponentType.ReadWrite<SCActive>());
        if(!active || settings.Frozen)
        {
            if(advancing && !settings.Programmed) s.History.Sample(tick,dt,new double?[4]);
            s.Snapshot.Status=active?"FROZEN":"INACTIVE";return;
        }
        if(settings.Programmed)
        {
            var frame=plugin.GraphAt(manager,sources[0]);
            s.Snapshot.Frame=frame ?? new();
            if (frame is not null && settings.YRange is not null) { s.Snapshot.Frame=frame.Copy();s.Snapshot.Frame.YRange=settings.YRange.ToArray(); }
            s.Snapshot.Status=frame is null?"NO ACTIVE COMPUTER PLOT":"PROGRAMMED / INPUT 1";
        }
        else
        {
            if(advancing) s.History.Sample(tick,dt,inputs);
            if(now>=s.NextDraw && advancing) s.Snapshot.Frame=s.History.Frame(settings);
            s.Snapshot.Status=inputs.All(v=>v is null)?"NO NUMERIC SIGNAL":advancing?"LIVE":"PAUSED";
        }
    }
    private static string[] SourceKeys(EntityManager manager,PortTarget target)
    {
        var keys=new string[4];
        for(int i=0;i<4;i++)
        {
            keys[i]="";
            var e=manager.GetComponentData<SpaceshipElectricPort>(target.Inputs[i])._runtimeEndEntity;
            if(!manager.Exists(e) || !manager.HasComponent<SpaceshipElectricPort>(e) || !manager.HasComponent<BelongingSC>(e)) continue;
            var root=manager.GetComponentData<BelongingSC>(e)._sc;
            if(manager.Exists(root) && manager.HasComponent<SCGuid>(root)) keys[i]=manager.GetComponentData<SCGuid>(root)+":"+manager.GetComponentData<SpaceshipElectricPort>(e)._index;
        }
        return keys;
    }
    private void Apply(EntityManager manager,string guid,string? expected,GraphSettings? settings,bool clear)
    {
        if(!plugin.CanGraphWrite(manager)) throw new InvalidOperationException("Graph settings require host authority.");
        if(!screens.TryGetValue(guid,out var s)) {Discover(manager);s=screens.GetValueOrDefault(guid) ?? throw new InvalidOperationException("Graph screen not found.");}
        _=GraphScreen.Target(manager,s.Target.Component);LoadSettings(manager,s);
        if(s.Id!=expected) throw new IOException("Screen settings changed; reload before saving.");
        if(clear) {s.History.Clear();s.Snapshot.Frame=new();s.NextDraw=0;return;}
        settings!.Validate();Directory.CreateDirectory(directory);
        string id=Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
        using(var file=new FileStream(Path.Combine(directory,id+".json"),FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {file.Write(JsonSerializer.SerializeToUtf8Bytes(settings));file.Flush(true);}
        GraphScreen.WriteId(manager,s.Target.Component,id);
        bool reset=settings.Programmed!=s.Snapshot.Settings.Programmed;
        s.Id=id;s.Snapshot.Settings=settings;s.NextDraw=0;s.LabelPending=true;
        if(reset) {s.History.Clear();s.Snapshot.Frame=new();}
    }
    internal void Receive(EntityManager manager,ComputerLink link,ulong peer,ComputerMessage m)
    {
        if(link.IsClient)
        {
            if(!screens.TryGetValue(m.Target,out var s) || s.ReplyId!=m.Id) return;
            s.ReplyId=0;
            if(m.Error is not null) {uiNotice=m.Error;return;}
            s.Incoming=m.Graph!;s.IncomingId=m.ProgramId;return;
        }
        try
        {
            if(!plugin.CanGraphWrite(manager)) throw new InvalidOperationException("Host authority unavailable.");
            if(!screens.ContainsKey(m.Target)) Discover(manager);
            if(m.Kind!="graph-get") Apply(manager,m.Target,m.ProgramId,m.Graph?.Settings,m.Kind=="graph-clear");
            var s=screens.GetValueOrDefault(m.Target) ?? throw new InvalidOperationException("Graph screen not found.");
            LoadSettings(manager,s);
            link.SendToPeer(peer,new ComputerMessage {Kind="graph-state",Id=m.Id,Target=m.Target,ProgramId=s.Id,Graph=s.Snapshot});
        }
        catch(Exception ex) {link.SendToPeer(peer,new ComputerMessage {Kind="graph-error",Id=m.Id,Target=m.Target,Error=ex.Message[..Math.Min(256,ex.Message.Length)]});}
    }
    internal bool Open(EntityManager manager,Entity root,ComputerEditor editor)
    {
        var t=GraphScreen.Target(manager,root);
        if(!screens.TryGetValue(t.Guid,out var s)) {if(screens.Count>=16) throw new InvalidOperationException("Sixteen screen limit reached.");screens[t.Guid]=s=new(manager,t);}
        opened=t.Guid;uiNotice="";ymin=ymax="";
        editor.OpenPanel(Draw);return editor.IsOpen;
    }
    private void Draw(float width,float height)
    {
        Styles();
        GUI.Label(new Rect(12,4,width-140,28),"GP-04 / GRAPH SCREEN",title);
        if(Btn(new Rect(width-112,4,100,28),"Close [Esc]")) plugin.CloseGraphView();
        Fill(new Rect(12,36,width-24,2), new Color(.18f,.78f,.70f));
        if(opened is null || !screens.TryGetValue(opened,out var s)) {GUI.Label(new Rect(12,50,width-24,40),"Screen removed or world changed.",label);return;}
        GUI.Label(new Rect(12,44,width-24,22),$"[{s.Target.Component.Index}:{s.Target.Component.Version}]  {s.Snapshot.Status}  ·  {(s.Id ?? "default")}",label);
        float graphH=Math.Max(220,height-168);
        float graphW=Math.Min(width-24,graphH*4/3);
        var rect=new Rect(12+(width-24-graphW)/2,72,graphW,graphH);
        Fill(new Rect(rect.x-4,rect.y-4,rect.width+8,rect.height+8), new Color(.02f,.04f,.05f));
        s.Surface.Draw(rect);
        bool enabled=GUI.enabled;GUI.enabled=enabled && s.Loaded && s.ReplyId==0 && command is null;
        var settings=s.Snapshot.Settings;
        void Change(GraphSettings next) {command=(s.Target.Guid,s.Id,next,false);}
        GraphSettings Copy() => new() {Programmed=settings.Programmed,Seconds=settings.Seconds,Frozen=settings.Frozen,YRange=settings.YRange?.ToArray()};
        float y=rect.yMax+12, x=12;
        if(Btn(new Rect(x,y,128,28),settings.Programmed?"Programmed":"History",true)) {var n=Copy();n.Programmed=!n.Programmed;Change(n);} x+=136;
        if(Btn(new Rect(x,y,88,28),settings.Frozen?"Resume":"Freeze")) {var n=Copy();n.Frozen=!n.Frozen;Change(n);} x+=96;
        if(Btn(new Rect(x,y,88,28),"Clear")) command=(s.Target.Guid,s.Id,null,true); x+=96;
        if(Btn(new Rect(x,y,72,28),settings.Seconds+"s")) {var n=Copy();n.Seconds=settings.Seconds switch {15=>30,30=>60,60=>120,_=>15};Change(n);} x+=80;
        if(Btn(new Rect(x,y,80,28),"Auto Y")) {var n=Copy();n.YRange=null;Change(n);} x+=88;
        GUI.Label(new Rect(x,y,20,28),"Y",label); x+=22;
        var bg=GUI.backgroundColor; GUI.backgroundColor=new Color(.10f,.14f,.16f);
        ymin=GUI.TextField(new Rect(x,y,70,28),ymin,24,field); x+=78;
        ymax=GUI.TextField(new Rect(x,y,70,28),ymax,24,field); x+=78;
        GUI.backgroundColor=bg;
        if(Btn(new Rect(x,y,72,28),"Apply Y"))
        {
            if(double.TryParse(ymin,NumberStyles.Float,CultureInfo.InvariantCulture,out double a) && double.TryParse(ymax,NumberStyles.Float,CultureInfo.InvariantCulture,out double b))
            { try {var n=Copy();n.YRange=new[]{a,b};n.Validate();Change(n);}catch(Exception ex){uiNotice=ex.Message;} }
            else uiNotice="Use numeric axis limits (decimal point).";
        }
        GUI.enabled=enabled;
        string hint=uiNotice.Length!=0?uiNotice:rect.Contains(Event.current.mousePosition)?Cursor(s,rect):"History: inputs 1–4. Programmed: Input 1 selects an AU-08 plot.";
        GUI.Label(new Rect(12,y+32,width-24,22),hint,label);
    }
    private static string Cursor(Screen s, Rect rect)
    {
        var (a,b)=s.Snapshot.Frame.Bounds(true);var(c,d)=s.Snapshot.Frame.Bounds(false);
        float x=(Event.current.mousePosition.x-rect.x)/rect.width*640, y=(Event.current.mousePosition.y-rect.y)/rect.height*480;
        return x is >=70 and <=610 && y is >=60 and <=380 ? "X "+GraphFrame.Format(a+(b-a)*(x-70)/540)+"   Y "+GraphFrame.Format(d-(d-c)*(y-60)/320) : "History: inputs 1–4. Programmed: Input 1 selects an AU-08 plot.";
    }
    private void Styles()
    {
        if(title is not null) return;
        title=new GUIStyle{font=GUI.skin.font,fontSize=21,fontStyle=FontStyle.Bold}; title.normal.textColor=new Color(.28f,.88f,.78f);
        label=new GUIStyle{font=GUI.skin.font,fontSize=13,wordWrap=true}; label.normal.textColor=new Color(.82f,.87f,.91f);
        button=new GUIStyle{font=GUI.skin.font,fontSize=13,alignment=TextAnchor.MiddleCenter,padding=new RectOffset(5,5,3,3)};
        foreach(var st in new[]{button.normal,button.hover,button.active,button.focused}) {st.background=Texture2D.whiteTexture;st.textColor=Color.white;}
        button.hover.textColor=button.focused.textColor=new Color(.4f,1f,.88f);
        field=new GUIStyle{font=GUI.skin.font,fontSize=13,alignment=TextAnchor.MiddleLeft,padding=new RectOffset(6,6,3,3)};
        field.normal.background=field.focused.background=Texture2D.whiteTexture;
        field.normal.textColor=field.focused.textColor=new Color(.91f,.95f,.96f);
    }
    private bool Btn(Rect rect,string text,bool accent=false)
    {
        var old=GUI.backgroundColor; GUI.backgroundColor=accent?new Color(.13f,.42f,.39f):new Color(.18f,.23f,.28f);
        bool clicked=GUI.Button(rect,text,button); GUI.backgroundColor=old; return clicked;
    }
    private static void Fill(Rect rect,Color color)
    {
        fill??=new GUIStyle(); fill.normal.background=Texture2D.whiteTexture;
        var old=GUI.backgroundColor; GUI.backgroundColor=color; GUI.Box(rect,GUIContent.none,fill); GUI.backgroundColor=old;
    }
}
