// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SyncSyncAV {
    public sealed class PairState {
        public long A,B,Offset; public bool Linked;
        public PairState Copy() {return (PairState)MemberwiseClone();}
        public bool Same(PairState s) {return s!=null&&A==s.A&&B==s.B&&Offset==s.Offset&&Linked==s.Linked;}
    }
    public sealed class SyncPoint {
        public long A,B,Offset; public string Label="—";
    }
    public sealed class SyncAction {
        public string Kind; public long Value; public PairState Restore;
        public static SyncAction Create(string kind,long value=0) {return new SyncAction{Kind=kind,Value=value};}
    }
    public sealed class SyncPlan {
        public PairState State; public long? SeekA,SeekB; public bool Clamped;
        public string Message;
    }
    public static class SyncPlanner {
        public static long Clamp(decimal n,long lo,long hi) {return (long)Math.Max(lo,Math.Min(hi,n));}
        public static void Range(long countA,long countB,long offset,out long lo,out long hi) {
            decimal low=Math.Max(0,-(decimal)offset),high=Math.Min(countA-1m,countB-1m-offset);
            if(low>high||countA<=0||countB<=0)throw new InvalidOperationException(Ui.Text("NoSharedRange"));
            lo=(long)low;hi=(long)high;
        }
        public static bool Parse(string input,out long number,out bool relative) {
            string s=new string((input??"").Where(c=>!Char.IsWhiteSpace(c)).ToArray()).Replace('−','-').Replace('–','-').Replace('—','-').Replace("±","+");
            relative=s.StartsWith("+")||s.StartsWith("-");
            return Int64.TryParse(s,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out number);
        }
        public static SyncPlan Make(PairState current,long countA,long countB,SyncAction action) {
            var n=current.Copy();var p=new SyncPlan{State=n};long lo,hi;decimal want;
            switch(action.Kind) {
                case "step":
                    if(!n.Linked)throw new InvalidOperationException(Ui.Text("LinkRequired"));
                    Range(countA,countB,n.Offset,out lo,out hi);want=(decimal)n.A+action.Value;
                    n.A=Clamp(want,lo,hi);n.B=checked(n.A+n.Offset);p.Clamped=n.A!=want;p.SeekA=n.A;p.SeekB=n.B;p.Message=Ui.Text("SharedStep");break;
                case "nudgeA": case "nudgeB": {
                    bool b=action.Kind=="nudgeB";long count=b?countB:countA;
                    if(count<=0)throw new InvalidOperationException(Ui.Text("EmptyWindow"));
                    want=(decimal)(b?n.B:n.A)+action.Value;long target=Clamp(want,0,count-1);p.Clamped=target!=want;
                    if(b) {n.B=target;p.SeekB=target;} else {n.A=target;p.SeekA=target;}
                    if(n.Linked)n.Offset=checked(n.B-n.A);
                    p.Message=Ui.Text("NudgePrefix")+(b?"B":"A");break;
                }
                case "gotoA": case "gotoB": case "relativeA": case "relativeB": {
                    bool b=action.Kind.EndsWith("B",StringComparison.Ordinal);bool relative=action.Kind.StartsWith("relative",StringComparison.Ordinal);
                    want=relative?(decimal)(b?n.B:n.A)+action.Value:action.Value;
                    if(n.Linked) {
                        Range(countA,countB,n.Offset,out lo,out hi);decimal wa=b?want-n.Offset:want;
                        n.A=Clamp(wa,lo,hi);n.B=checked(n.A+n.Offset);p.Clamped=n.A!=wa;p.SeekA=n.A;p.SeekB=n.B;
                    } else {
                        long count=b?countB:countA;if(count<=0)throw new InvalidOperationException(Ui.Text("EmptyWindow"));
                        long target=Clamp(want,0,count-1);p.Clamped=target!=want;
                        if(b){n.B=target;p.SeekB=target;}else{n.A=target;p.SeekA=target;}
                    }
                    p.Message=Ui.Text("GoToPrefix")+(b?"B":"A");break;
                }
                case "offset":
                    n.Offset=action.Value;
                    if(n.Linked){Range(countA,countB,n.Offset,out lo,out hi);long old=n.A;n.A=Clamp(n.A,lo,hi);n.B=checked(n.A+n.Offset);p.Clamped=old!=n.A;p.SeekA=n.A;p.SeekB=n.B;}
                    p.Message=n.Linked?Ui.Text("OffsetApplied"):Ui.Text("OffsetSaved");break;
                case "link":
                    n.Linked=!n.Linked;
                    if(n.Linked){Range(countA,countB,n.Offset,out lo,out hi);long old=n.A;n.A=Clamp(n.A,lo,hi);n.B=checked(n.A+n.Offset);p.Clamped=old!=n.A;p.SeekA=n.A;p.SeekB=n.B;}
                    p.Message=n.Linked?Ui.Text("LinkOn"):Ui.Text("LinkOff");break;
                case "anchor":
                    if(n.A>=countA||n.B>=countB)throw new InvalidOperationException(Ui.Text("ChooseFrame"));
                    n.Offset=checked(n.B-n.A);n.Linked=true;p.Message=Ui.Text("PairAnchored");break;
                case "restore":
                    if(action.Restore==null)throw new InvalidOperationException(Ui.Text("MissingPair"));
                    n=action.Restore.Copy();p.State=n;
                    if(n.A<0||n.A>=countA||n.B<0||n.B>=countB)throw new InvalidOperationException(Ui.Text("PointOutsideVideo"));
                    if(n.Linked&&n.B-n.A!=n.Offset)throw new InvalidOperationException(Ui.Text("InvalidSavedLink"));
                    p.SeekA=n.A;p.SeekB=n.B;p.Message=Ui.Text("PairRestored");break;
                default:throw new InvalidOperationException(Ui.Text("UnknownAction")+action.Kind);
            }
            if(p.SeekA==current.A)p.SeekA=null;if(p.SeekB==current.B)p.SeekB=null;
            return p;
        }
    }

    // All public async operations are serialized by the caller. No queued relative seeks.
    public sealed class SyncSession {
        private readonly IBridgeClient client;
        public HostState HostA,HostB;
        public int PidA,PidB;
        public PairState State=new PairState();
        public string Message=Ui.Text("FindingWindows");
        public string Boundary="";
        public bool Warning,Error;
        // Presentation may choose either sign convention; stored offsets remain B-A.
        public Func<long,string> FormatOffset=Signed;
        public readonly List<PairState> History=new List<PairState>(),Future=new List<PairState>();
        public readonly Dictionary<string,List<SyncPoint>> PointSets=new Dictionary<string,List<SyncPoint>>(StringComparer.OrdinalIgnoreCase);
        private string identityA,identityB;
        private PairState gestureBefore;private DateTime lastExternal;
        private int following;
        private readonly Dictionary<int,string> readErrors=new Dictionary<int,string>();
        public event Action Changed;
        public SyncSession(IBridgeClient client) {this.client=client;}
        private static string PointKey(HostState host,bool resolveDisc) {
            string source=host.File;
            if(source!=null&&(source.EndsWith(".iso",StringComparison.OrdinalIgnoreCase)||(resolveDisc&&source.EndsWith(".bdmv",StringComparison.OrdinalIgnoreCase)))&&host.Video!=null&&!String.IsNullOrEmpty(host.Video.ResolvedFile))
                source=host.Video.ResolvedFile;
            return source+"|"+host.Count+"|"+host.Subsets;
        }
        private string PairPointKey(bool resolveDisc) {
            return HostA==null||HostB==null?"":PointKey(HostA,resolveDisc)+"\n"+PointKey(HostB,resolveDisc);
        }
        public string PairKey {get{return PairPointKey(true);}}
        public List<SyncPoint> Points {get {
            string key=PairKey;
            List<SyncPoint> points;
            if(PointSets.TryGetValue(key,out points))return points;
            // Earlier versions keyed BDMV folders by index.bdmv and frame count.
            // Keep those points for the first matching pair opened after upgrade.
            string legacy=PairPointKey(false);
            if(key!=legacy&&PointSets.TryGetValue(legacy,out points))PointSets.Remove(legacy);
            else points=new List<SyncPoint>();
            PointSets[key]=points;
            return points;
        }}
        public bool ReadyA {get{return Usable(HostA);}}
        public bool ReadyB {get{return Usable(HostB);}}
        public bool PairReady {get{return PidA>0&&PidB>0&&PidA!=PidB&&ReadyA&&ReadyB;}}
        public static bool Usable(HostState h) {return h!=null&&h.HasMedia&&h.Frame.HasValue&&h.Count>0&&!h.Busy;}
        public void Notify(){if(Changed!=null)Changed();}
        public void Say(string message,bool warning=false,bool error=false) {Message=message;Warning=warning;Error=error;Notify();}
        private static void Check(HostState h,string label) {if(!Usable(h))throw new InvalidOperationException(Ui.Text("WindowPrefix")+label+Ui.Text("WindowBusySuffix"));}
        private async Task<HostState> ReadOne(int pid,CancellationToken token) {
            if(pid<=0)return null;
            for(int attempt=0;attempt<2;attempt++) {
                try{var h=await client.StatusAsync(pid,token);readErrors.Remove(pid);return h;}catch(OperationCanceledException){throw;}
                catch(BridgeException e){readErrors[pid]=e.Code+": "+e.Message;if(attempt==0&&e.Code=="connection_failed"){await Task.Delay(80,token);continue;}return null;}
                catch(Exception e){readErrors[pid]=e.Message;return null;}
            }
            return null;
        }
        private async Task<bool> ReadAsync(CancellationToken token) {
            var aa=ReadOne(PidA,token);var bb=PidA==PidB?aa:ReadOne(PidB,token);await Task.WhenAll(aa,bb);
            var a=aa.Result;var b=bb.Result;
            bool changed=(identityA!=null&&a!=null&&identityA!=a.Fingerprint)||(identityB!=null&&b!=null&&identityB!=b.Fingerprint);
            if(a!=null)identityA=a.Fingerprint;if(b!=null)identityB=b.Fingerprint;
            HostA=a;HostB=b;
            if(a!=null&&a.Frame.HasValue)State.A=a.Frame.Value;if(b!=null&&b.Frame.HasValue)State.B=b.Frame.Value;
            if(changed) {ResetSourceNavigation();Say(Ui.Text("SourceChanged"),true);}
            if((PidA>0&&a==null)||(PidB>0&&b==null)) {State.Linked=false;following=0;string detail;readErrors.TryGetValue(a==null?PidA:PidB,out detail);Say(Ui.Text("NoReply")+(a==null?"A":"B")+Ui.Text("LinkOffDetail")+detail,true,true);}
            return changed;
        }
        public void ResetSourceNavigation() {
            State.Linked=false;History.Clear();Future.Clear();gestureBefore=null;following=0;Boundary="";
        }
        public async Task SelectAsync(int a,int b,bool initial,CancellationToken token) {
            PidA=a;PidB=b;identityA=identityB=null;HostA=HostB=null;ResetSourceNavigation();
            await ReadAsync(token);
            if(initial&&PairReady&&State.A<HostA.Count&&State.B<HostB.Count){State.Offset=State.B-State.A;State.Linked=true;Say(Ui.Text("InitialLink"));}
            else Say(a==b&&a>0?Ui.Text("DuplicateWindow"):Ui.Text("SelectionChanged"),true);
            Notify();
        }
        private void Remember(PairState before) {
            if(before.Same(State))return;History.Add(before.Copy());if(History.Count>300)History.RemoveAt(0);Future.Clear();
        }
        private void FinishGesture(){if(gestureBefore!=null){Remember(gestureBefore);gestureBefore=null;}}
        private async Task Send(int pid,long? target,CancellationToken token,List<string> errors) {
            if(!target.HasValue)return;
            try {var r=await client.SeekAsync(pid,target.Value,token);if(r.Frame!=target)errors.Add(Ui.Text("SeekUnconfirmed"));}
            catch(OperationCanceledException){throw;}catch(Exception e){errors.Add(e.Message);}
        }
        private async Task ExecutePlan(SyncPlan plan,CancellationToken token,bool follow=false) {
            string fa=HostA==null?null:HostA.Fingerprint,fb=HostB==null?null:HostB.Fingerprint;
            if(plan.SeekA.HasValue)Check(HostA,"A");if(plan.SeekB.HasValue)Check(HostB,"B");
            var ea=new List<string>();var eb=new List<string>();
            await Task.WhenAll(Send(PidA,plan.SeekA,token,ea),Send(PidB,plan.SeekB,token,eb));
            if(plan.SeekA.HasValue||plan.SeekB.HasValue) {
                bool changed=await ReadAsync(token);
                bool checkA=plan.SeekA.HasValue||(!follow&&plan.State.Linked),checkB=plan.SeekB.HasValue||(!follow&&plan.State.Linked);
                bool goodA=!checkA||(HostA!=null&&HostA.Fingerprint==fa&&HostA.Frame==plan.State.A);
                bool goodB=!checkB||(HostB!=null&&HostB.Fingerprint==fb&&HostB.Frame==plan.State.B);
                if(changed||ea.Count>0||eb.Count>0||!goodA||!goodB) {
                    State.Linked=false;following=0;History.Clear();Future.Clear();gestureBefore=null;
                    throw new InvalidOperationException(Ui.Text("PartialSeek")+String.Join(" ",ea.Concat(eb))+Ui.Text("NoAutoRetry"));
                }
            }
            // Preserve actual moving leader during an external follow operation.
            if(follow&&(State.A!=plan.State.A||State.B!=plan.State.B))lastExternal=DateTime.UtcNow;
            if(!follow){if(plan.SeekA.HasValue)State.A=plan.State.A;if(plan.SeekB.HasValue)State.B=plan.State.B;}
            State.Offset=plan.State.Offset;State.Linked=plan.State.Linked;
        }
        public async Task ApplyAsync(SyncAction action,CancellationToken token) {
            FinishGesture();following=0;Boundary="";
            bool changed=await ReadAsync(token);
            if(changed)throw new InvalidOperationException(Ui.Text("SourceChangedCheck"));
            if(PidA==PidB&&PidA>0)throw new InvalidOperationException(Ui.Text("SelectDistinctWindows"));
            bool localA=action.Kind=="nudgeA"||(!State.Linked&&(action.Kind=="gotoA"||action.Kind=="relativeA"));
            bool localB=action.Kind=="nudgeB"||(!State.Linked&&(action.Kind=="gotoB"||action.Kind=="relativeB"));
            if(localA&&!State.Linked)Check(HostA,"A");else if(localB&&!State.Linked)Check(HostB,"B");
            else {Check(HostA,"A");Check(HostB,"B");}
            var before=State.Copy();var p=SyncPlanner.Make(State,HostA==null?0:HostA.Count,HostB==null?0:HostB.Count,action);
            await ExecutePlan(p,token);Remember(before);
            if(action.Kind=="anchor") {
                bool exists=Points.Any(x=>x.A==State.A&&x.B==State.B);
                if(!exists){Points.Add(new SyncPoint{A=State.A,B=State.B,Offset=State.Offset});Points.Sort((x,y)=>x.A.CompareTo(y.A));}
                p.Message=exists?Ui.Text("PointAlreadySaved"):Ui.Text("PointSaved");
            }
            string edge="";
            if(p.Clamped) {
                var parts=new List<string>();
                bool local=action.Kind.StartsWith("nudge",StringComparison.Ordinal)||!State.Linked;
                if((!local||action.Kind.EndsWith("A",StringComparison.Ordinal))&&HostA!=null&&(State.A==0||State.A==HostA.Count-1)){Boundary+="A";parts.Add((State.A==0?Ui.Text("Start"):Ui.Text("End"))+" A");}
                if((!local||action.Kind.EndsWith("B",StringComparison.Ordinal))&&HostB!=null&&(State.B==0||State.B==HostB.Count-1)){Boundary+="B";parts.Add((State.B==0?Ui.Text("Start"):Ui.Text("End"))+" B");}
                edge=". "+String.Join(" · ",parts)+Ui.Text("StoppedAtBoundary");
            }
            Say(p.Message+": A "+State.A+" · B "+State.B+" · "+FormatOffset(State.Offset)+(p.Clamped?edge:"."),p.Clamped);Notify();
        }
        public async Task UndoAsync(bool redo,CancellationToken token) {
            FinishGesture();following=0;Boundary="";
            var from=redo?Future:History;var to=redo?History:Future;
            if(from.Count==0){Say(redo?Ui.Text("NothingToRedo"):Ui.Text("NothingToUndo"));return;}
            if(await ReadAsync(token))throw new InvalidOperationException(Ui.Text("HistoryCleared"));
            Check(HostA,"A");Check(HostB,"B");
            if(from.Count==0)return;var target=from[from.Count-1];var before=State.Copy();
            var p=SyncPlanner.Make(State,HostA.Count,HostB.Count,new SyncAction{Kind="restore",Restore=target});
            await ExecutePlan(p,token);from.RemoveAt(from.Count-1);to.Add(before);Say((redo?Ui.Text("Redone"):Ui.Text("Undone"))+": A "+State.A+" · B "+State.B+" · "+FormatOffset(State.Offset)+".");
        }
        public async Task RecallAsync(SyncPoint point,CancellationToken token) {await ApplyAsync(new SyncAction{Kind="restore",Restore=new PairState{A=point.A,B=point.B,Offset=point.Offset,Linked=true}},token);}
        public void DeletePoint(SyncPoint point) {Points.Remove(point);Say(Ui.Text("PointDeleted"));}
        public async Task SwapAsync(CancellationToken token) {
            FinishGesture();await ReadAsync(token);Check(HostA,"A");Check(HostB,"B");
            long inverse=checked(-State.Offset);
            var pts=Points.Select(x=>new SyncPoint{A=x.B,B=x.A,Offset=checked(-x.Offset),Label=x.Label}).ToList();
            int a=PidA;PidA=PidB;PidB=a;var h=HostA;HostA=HostB;HostB=h;
            string id=identityA;identityA=identityB;identityB=id;long frame=State.A;State.A=State.B;State.B=frame;State.Offset=inverse;
            foreach(var p in pts)if(!Points.Any(x=>x.A==p.A&&x.B==p.B))Points.Add(p);
            Points.Sort((x,y)=>x.A.CompareTo(y.A));History.Clear();Future.Clear();following=0;Say(Ui.Text("SwappedOffset")+FormatOffset(State.Offset)+".");
        }
        public async Task PollAsync(CancellationToken token) {
            var before=State.Copy();bool changed=await ReadAsync(token);
            if(changed){Notify();return;}
            bool ca=State.A!=before.A,cb=State.B!=before.B;
            if(ca||cb) {
                if(gestureBefore==null)gestureBefore=before;
                lastExternal=DateTime.UtcNow;
                if(State.Linked&&ca&&cb&&State.B-State.A!=State.Offset) {State.Linked=false;following=0;FinishGesture();Say(Ui.Text("BothMoved"),true);Notify();return;}
                following=ca?1:2;
            }
            if(State.Linked&&PairReady&&State.B-State.A!=State.Offset&&following!=0) {
                long lo,hi;SyncPlanner.Range(HostA.Count,HostB.Count,State.Offset,out lo,out hi);
                decimal desired=following==1?State.A:(decimal)State.B-State.Offset;
                long targetA=SyncPlanner.Clamp(desired,lo,hi);long targetB=checked(targetA+State.Offset);
                // Never move the manually operated source back under the user's cursor.
                if(targetA!=desired){State.Linked=false;following=0;FinishGesture();Say(Ui.Text("SharedBoundary"),true);Notify();return;}
                var p=new SyncPlan{State=new PairState{A=targetA,B=targetB,Offset=State.Offset,Linked=true},SeekA=following==2?(long?)targetA:null,SeekB=following==1?(long?)targetB:null};
                await ExecutePlan(p,token,true);
                Say(Ui.Text("FollowingWindow")+(following==1?"A":"B")+": A "+State.A+" · B "+State.B+".");
            }
            if(!ca&&!cb&&gestureBefore!=null&&(DateTime.UtcNow-lastExternal).TotalMilliseconds>=300){FinishGesture();following=0;}
            Notify();
        }
        public static string Signed(long n){return n>0?"+"+n:n<0?"−"+Math.Abs((decimal)n).ToString(CultureInfo.InvariantCulture):"±0";}
    }
}
