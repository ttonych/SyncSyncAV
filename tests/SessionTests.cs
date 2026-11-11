using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using SyncSyncAV;
class Fake : IBridgeClient {
 public Dictionary<int,HostState> States=new Dictionary<int,HostState>();public List<string> Writes=new List<string>();public int FailPid,ReadFailures;public Action AfterSeek;
 public Fake(){States[1]=new HostState{Pid=1,Hwnd=11,File="a.mpls",Executable="vd",HasMedia=true,Frame=20,Count=100,Subsets=1};States[2]=new HostState{Pid=2,Hwnd=22,File="b.mpls",Executable="vd",HasMedia=true,Frame=30,Count=200,Subsets=1};}
 public Task<HostState> StatusAsync(int pid,CancellationToken t){t.ThrowIfCancellationRequested();if(ReadFailures>0){ReadFailures--;throw new BridgeException("connection_failed","Transient");}return Task.FromResult(States[pid].Copy());}
 public Task<HostState> SeekAsync(int pid,long n,CancellationToken t){Writes.Add(pid+":"+n);if(FailPid==pid)throw new BridgeException("timeout","Test",true);States[pid].Frame=n;if(AfterSeek!=null)AfterSeek();return Task.FromResult(States[pid].Copy());}
}
class SessionTests {
 static readonly List<object> results=new List<object>();static readonly CancellationToken t=CancellationToken.None;
 static void Yes(bool b,string why="assert"){if(!b)throw new Exception(why);}
 static void Equal(SyncSession s,long a,long b,long off,bool linked=true){Yes(s.State.A==a&&s.State.B==b&&s.State.Offset==off&&s.State.Linked==linked,"state "+s.State.A+","+s.State.B+","+s.State.Offset+","+s.State.Linked);}
 static async Task Test(string name,Func<Task> action){await action();results.Add(new{name=name,passed=true});Console.WriteLine("PASS "+name);}
 static async Task Reject(Func<Task> fn){bool rejected=false;try{await fn();}catch(InvalidOperationException){rejected=true;}catch(OverflowException){rejected=true;}Yes(rejected,"expected rejection");}
 static async Task Run(){
 var f=new Fake();var s=new SyncSession(f);await s.SelectAsync(1,2,true,t);
 await Test("startup-no-seek",()=>{Equal(s,20,30,10);Yes(f.Writes.Count==0);return Task.CompletedTask;});
 await Test("linked-step",async()=>{await s.ApplyAsync(SyncAction.Create("step",1),t);Equal(s,21,31,10);});
 await Test("local-A-keeps-B-adjusts-offset",async()=>{f.Writes.Clear();await s.ApplyAsync(SyncAction.Create("nudgeA",1),t);Equal(s,22,31,9);Yes(f.Writes.SequenceEqual(new[]{"1:22"}));});
 await Test("local-B-keeps-A-adjusts-offset",async()=>{f.Writes.Clear();await s.ApplyAsync(SyncAction.Create("nudgeB",-1),t);Equal(s,22,30,8);Yes(f.Writes.SequenceEqual(new[]{"2:30"}));});
 await Test("undo-pair-and-offset",async()=>{await s.UndoAsync(false,t);Equal(s,22,31,9);});
 await Test("redo-pair-and-offset",async()=>{await s.UndoAsync(true,t);Equal(s,22,30,8);});
 await Test("goto-B-leads",async()=>{await s.ApplyAsync(SyncAction.Create("gotoB",50),t);Equal(s,42,50,8);});
 await Test("relative-A",async()=>{await s.ApplyAsync(SyncAction.Create("relativeA",-2),t);Equal(s,40,48,8);});
 await Test("linked-high-clamp",async()=>{await s.ApplyAsync(SyncAction.Create("gotoA",999),t);Equal(s,99,107,8);Yes(s.Warning);});
 await Test("linked-low-clamp",async()=>{await s.ApplyAsync(SyncAction.Create("relativeB",-999),t);Equal(s,0,8,8);});
 await Test("negative-offset-clamps-common-range",async()=>{await s.ApplyAsync(SyncAction.Create("offset",-5),t);Equal(s,5,0,-5);});
 await Test("offset-out-of-range-rejected-no-writes",async()=>{f.Writes.Clear();await Reject(()=>s.ApplyAsync(SyncAction.Create("offset",500),t));Equal(s,5,0,-5);Yes(f.Writes.Count==0);});
 await Test("unlink-stops-general-step",async()=>{await s.ApplyAsync(SyncAction.Create("link"),t);await Reject(()=>s.ApplyAsync(SyncAction.Create("step",1),t));Equal(s,5,0,-5,false);});
 await Test("unlinked-local-offset-retained",async()=>{await s.ApplyAsync(SyncAction.Create("nudgeB",10),t);Equal(s,5,10,-5,false);});
 await Test("unlinked-offset-stored",async()=>{f.Writes.Clear();await s.ApplyAsync(SyncAction.Create("offset",8),t);Equal(s,5,10,8,false);Yes(f.Writes.Count==0);});
 await Test("link-applies-stored-offset",async()=>{await s.ApplyAsync(SyncAction.Create("link"),t);Equal(s,5,13,8);});
 await Test("anchor-deduplicates-exact-pair",async()=>{await s.ApplyAsync(SyncAction.Create("anchor"),t);await s.ApplyAsync(SyncAction.Create("anchor"),t);Yes(s.Points.Count==1);});
 await Test("manual-A-follows-only-B",async()=>{f.Writes.Clear();f.States[1].Frame=20;await s.PollAsync(t);Equal(s,20,28,8);Yes(f.Writes.SequenceEqual(new[]{"2:28"}));});
 await Test("manual-B-follows-only-A",async()=>{f.Writes.Clear();f.States[2].Frame=40;await s.PollAsync(t);Equal(s,32,40,8);Yes(f.Writes.SequenceEqual(new[]{"1:32"}));});
 await Task.Delay(350);await s.PollAsync(t);
 await Test("drag-undo-single-entry",async()=>{await s.UndoAsync(false,t);Equal(s,5,13,8);});
 await Test("ambiguous-manual-both-unlinks",async()=>{f.Writes.Clear();f.States[1].Frame=25;f.States[2].Frame=40;await s.PollAsync(t);Equal(s,25,40,8,false);Yes(f.Writes.Count==0);});
 await Test("reanchor-current-frames",async()=>{await s.ApplyAsync(SyncAction.Create("anchor"),t);Equal(s,25,40,15);});
 await Test("manual-End-does-not-rewind-source",async()=>{f.Writes.Clear();f.States[1].Frame=100;await s.PollAsync(t);Equal(s,100,40,15,false);Yes(f.Writes.Count==0);});
 await Test("anchor-End-rejected",()=>Reject(()=>s.ApplyAsync(SyncAction.Create("anchor"),t)));
 f.States[1].Frame=20;f.States[2].Frame=30;await s.SelectAsync(1,2,true,t);
 await Test("transient-status-retry",async()=>{f.ReadFailures=1;await s.PollAsync(t);Equal(s,20,30,10);});
 await Test("partial-seek-readback-unlinks-no-retry",async()=>{f.Writes.Clear();f.FailPid=2;await Reject(()=>s.ApplyAsync(SyncAction.Create("step",1),t));Equal(s,21,30,10,false);Yes(f.Writes.Count==2);f.FailPid=0;});
 await s.SelectAsync(1,2,true,t);
 await Test("changed-media-no-command-history-cleared",async()=>{f.Writes.Clear();f.States[1].File="new.mpls";await Reject(()=>s.ApplyAsync(SyncAction.Create("step",1),t));Yes(!s.State.Linked&&f.Writes.Count==0&&s.History.Count==0);});
 await s.SelectAsync(1,2,true,t);
 await Test("busy-host-prevents-pair-command",async()=>{f.Writes.Clear();f.States[2].Busy=true;await Reject(()=>s.ApplyAsync(SyncAction.Create("step",1),t));Yes(f.Writes.Count==0);f.States[2].Busy=false;});
 await Test("swap-remaps-pair-offset-points",async()=>{await s.ApplyAsync(SyncAction.Create("anchor"),t);long a=s.State.A,b=s.State.B,off=s.State.Offset;await s.SwapAsync(t);Equal(s,b,a,-off);Yes(s.PidA==2&&s.Points.Any(x=>x.A==b&&x.B==a&&x.Offset==-off));});
 await Test("swap-overflow-atomic",async()=>{s.State.Linked=false;s.State.Offset=long.MinValue;int a=s.PidA;await Reject(()=>s.SwapAsync(t));Yes(s.PidA==a&&s.State.Offset==long.MinValue);});
 await s.SelectAsync(1,2,true,t);
 await Test("leader-moves-during-follow-keeps-actual",async()=>{f.States[1].Frame=24;f.AfterSeek=()=>{f.AfterSeek=null;f.States[1].Frame=25;};await s.PollAsync(t);Yes(s.State.A==25&&s.State.Linked);await s.PollAsync(t);Equal(s,25,34,9);});
 await Test("untargeted-linked-host-change-detected",async()=>{f.AfterSeek=()=>{f.AfterSeek=null;f.States[2].Frame=80;};await Reject(()=>s.ApplyAsync(SyncAction.Create("nudgeA",1),t));Yes(!s.State.Linked&&s.State.B==80);});
 await Test("same-PID-rejected",async()=>{await s.SelectAsync(1,1,false,t);f.Writes.Clear();await Reject(()=>s.ApplyAsync(SyncAction.Create("nudgeA",1),t));Yes(f.Writes.Count==0);});
 await Test("unicode-relative-parser",()=>{long n;bool rel;Yes(SyncPlanner.Parse(" − 100 ",out n,out rel)&&n==-100&&rel);Yes(!SyncPlanner.Parse("1.5",out n,out rel));Yes(!SyncPlanner.Parse("9999999999999999999999",out n,out rel));return Task.CompletedTask;});
 await Test("int64-range-no-wrap",()=>{long lo,hi;SyncPlanner.Range(long.MaxValue,long.MaxValue,-1,out lo,out hi);Yes(lo==1&&hi==long.MaxValue-1);return Task.CompletedTask;});
 await Test("multiple-points-same-offset-recall-swap",async()=>{
  var qf=new Fake();var qs=new SyncSession(qf);await qs.SelectAsync(1,2,true,t);
  await qs.ApplyAsync(SyncAction.Create("anchor"),t);await qs.ApplyAsync(SyncAction.Create("step",12),t);await qs.ApplyAsync(SyncAction.Create("anchor"),t);
  Yes(qs.Points.Count==2&&qs.Points.All(p=>p.Offset==10));var first=qs.Points[0];var second=qs.Points[1];
  await qs.RecallAsync(first,t);Equal(qs,20,30,10);await qs.RecallAsync(second,t);Equal(qs,32,42,10);
  await qs.ApplyAsync(SyncAction.Create("anchor"),t);Yes(qs.Points.Count==2);
  await qs.SwapAsync(t);Yes(qs.Points.Count==2&&qs.Points.All(p=>p.Offset==-10));
 });
 await Test("folder-points-upgrade-once",async()=>{
  var qf=new Fake();var qs=new SyncSession(qf);
  qf.States[1].File="index.bdmv";
  qf.States[1].Video=new DiscMetadata{ResolvedFile="00001.mpls"};
  qs.PointSets["index.bdmv|100|1\nb.mpls|200|1"]=new List<SyncPoint>{new SyncPoint{A=4,B=7,Offset=3}};
  await qs.SelectAsync(1,2,false,t);
  Yes(qs.Points.Count==1&&qs.Points[0].Offset==3);
  qf.States[1].Video=new DiscMetadata{ResolvedFile="00002.mpls"};
  await qs.PollAsync(t);Yes(qs.Points.Count==0);
 });
 await Test("resolved-playlist-change-invalidates-link",async()=>{
  var qf=new Fake();var qs=new SyncSession(qf);qf.States[1].Video=new DiscMetadata{ResolvedFile="00001.mpls",FpsNumerator=24,FpsDenominator=1};
  await qs.SelectAsync(1,2,true,t);qf.States[1].Video=new DiscMetadata{ResolvedFile="00002.mpls",FpsNumerator=24,FpsDenominator=1};await qs.PollAsync(t);Yes(!qs.State.Linked&&qf.Writes.Count==0);
 });
 }
 static int Main(string[] args){try{Run().GetAwaiter().GetResult();File.WriteAllText(args[0],new JavaScriptSerializer().Serialize(results));Console.WriteLine("Passed "+results.Count);return 0;}catch(Exception e){Console.WriteLine(e);return 1;}}
}
