using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using SyncSyncAV;

internal static class TransportTests {
    private static int pid=Process.GetCurrentProcess().Id;
    private static List<object> results=new List<object>();
    private static Dictionary<string,object> State() {
        return new Dictionary<string,object> { {"pid",pid},{"hwnd",100},{"file","source.mpls"},{"executable","test.exe"},{"bridge_version","1.0.0"},
            {"has_media",true},{"busy",false},{"frame",3},{"frame_count",10},{"subset_count",1},{"sdk_api",2},{"readiness","timeline_only"} };
    }
    private static void Assert(bool condition) {if(!condition)throw new Exception("Assertion failed");}
    private static async Task<HostState> Exchange(Func<Dictionary<string,object>,Dictionary<string,object>> alter,bool malformed=false,bool oversize=false,int delay=0,CancellationToken token=default(CancellationToken),int timeout=2500,IntPtr? embedParent=null) {
        using(var server=new NamedPipeServerStream("vdub-sdk-"+pid,PipeDirection.InOut,1,PipeTransmissionMode.Message,PipeOptions.Asynchronous)) {
            var serving=Task.Run(async()=> {
                try {
                    await server.WaitForConnectionAsync();byte[] request=new byte[32];int count=0;
                    do {int n=await server.ReadAsync(request,count,request.Length-count);if(n==0)return;count+=n;}while(count<32);
                    Assert(BitConverter.ToUInt32(request,0)==0x31434456&&BitConverter.ToUInt16(request,4)==1);
                    if(embedParent.HasValue)Assert(BitConverter.ToUInt16(request,6)==6&&BitConverter.ToInt64(request,16)==0x81234567L);
                    var reply=new Dictionary<string,object>{{"ok",true},{"id",BitConverter.ToUInt32(request,8)},{"state",State()}};
                    if(alter!=null)reply=alter(reply);
                    if(delay>0)await Task.Delay(delay);
                    byte[] bytes=Encoding.UTF8.GetBytes(oversize?new string('x',262145):malformed?"{":new JavaScriptSerializer{MaxJsonLength=1000000}.Serialize(reply));
                    await server.WriteAsync(bytes,0,bytes.Length);
                    var ack=new byte[1];int a=await server.ReadAsync(ack,0,1);if(a>0)Assert(ack[0]==6);
                }catch(IOException){}catch(ObjectDisposedException){}
            });
            var client=new BridgeClient(timeout);
            try { return embedParent.HasValue?await client.EmbedAsync(pid,embedParent.Value,token):await client.StatusAsync(pid,token); }
            finally { server.Dispose();try{await serving;}catch(IOException){}catch(ObjectDisposedException){} }
        }
    }
    private static void Test(string name,Func<Task> action) {
        var time=Stopwatch.StartNew();action().GetAwaiter().GetResult();results.Add(new{name=name,passed=true,seconds=time.Elapsed.TotalSeconds});Console.WriteLine("PASS "+name);
    }
    private static async Task Error(Func<Task<HostState>> call,string code,bool? uncertain=null) {
        try{await call();}catch(BridgeException e){Assert(e.Code==code);if(uncertain.HasValue)Assert(e.MayHaveApplied==uncertain.Value);return;}
        throw new Exception("Expected "+code);
    }
    private static int Main(string[] args) {
        try {
            Test("message-packet-and-ack",async()=>{var s=await Exchange(null);Assert(s.Frame==3&&s.Pid==pid);});
            Test("embed-hwnd-with-high-bit",async()=>{
                var s=await Exchange(r=>{((Dictionary<string,object>)r["state"])["hwnd"]=0x81234567L;return r;},embedParent:new IntPtr(unchecked((int)0x81234567)));
                Assert(s.Hwnd==0x81234567L);
            });
            Test("driver-metadata-fraction-and-milliseconds",async()=>{
                var s=await Exchange(r=>{((Dictionary<string,object>)r["state"])["video"]=new Dictionary<string,object>{{"fps_num",24000},{"fps_den",1001},{"width",1920},{"height",1080},{"codec","h264"},{"resolved_file","G:\\BDMV\\PLAYLIST\\00001.mpls"}};return r;});
                Assert(s.Video!=null&&s.Video.Width==1920&&s.Video.Height==1080&&s.Video.Codec=="AVC"&&s.Video.RoundedMilliseconds(24)==1001&&s.Video.RoundedMilliseconds(-24)==-1001&&s.Video.RoundedMilliseconds(1)==42);
            });
            Test("null-metadata-old-bridge-compatible",async()=>{var s=await Exchange(r=>{((Dictionary<string,object>)r["state"])["video"]=null;return r;});Assert(s.Video==null&&s.Frame==3);});
            Test("invalid-metadata-rate-rejected",()=>Error(()=>Exchange(r=>{((Dictionary<string,object>)r["state"])["video"]=new Dictionary<string,object>{{"fps_num",24},{"fps_den",0},{"width",1920},{"height",1080}};return r;}),"connection_failed"));
            Test("UTF8-and-large-message",async()=>{var s=await Exchange(r=>{((Dictionary<string,object>)r["state"])["file"]=new string('Ж',14000);return r;});Assert(s.File.Length==14000);});
            Test("request-id-mismatch",()=>Error(()=>Exchange(r=>{r["id"]=Convert.ToInt64(r["id"])+1;return r;}),"identity_mismatch"));
            Test("response-pid-mismatch",()=>Error(()=>Exchange(r=>{((Dictionary<string,object>)r["state"])["pid"]=pid+1;return r;}),"identity_mismatch"));
            Test("unknown-readiness-rejected",()=>Error(()=>Exchange(r=>{((Dictionary<string,object>)r["state"])["readiness"]="ready";return r;}),"unsupported_bridge"));
            Test("exclusive-end-cursor-accepted",async()=>{var s=await Exchange(r=>{((Dictionary<string,object>)r["state"])["frame"]=10;return r;});Assert(s.Frame==s.Count);});
            Test("invalid-range-state-rejected",()=>Error(()=>Exchange(r=>{((Dictionary<string,object>)r["state"])["frame"]=11;return r;}),"invalid_state"));
            Test("missing-field-rejected",()=>Error(()=>Exchange(r=>{((Dictionary<string,object>)r["state"]).Remove("busy");return r;}),"connection_failed"));
            Test("fractional-frame-rejected",()=>Error(()=>Exchange(r=>{((Dictionary<string,object>)r["state"])["frame"]=3.5m;return r;}),"connection_failed"));
            Test("malformed-json-rejected",()=>Error(()=>Exchange(null,malformed:true),"connection_failed"));
            Test("oversized-response-rejected",()=>Error(()=>Exchange(null,oversize:true),"connection_failed"));
            Test("bridge-error-code-preserved",()=>Error(()=>Exchange(r=>{r["ok"]=false;r["error"]=new Dictionary<string,object>{{"code","busy"},{"message","занято"},{"may_have_applied",false}};return r;}),"busy",false));
            Test("uncertain-flag-preserved",()=>Error(()=>Exchange(r=>{r["ok"]=false;r["error"]=new Dictionary<string,object>{{"code","position_mismatch"},{"message","uncertain"},{"may_have_applied",true}};return r;}),"position_mismatch",true));
            Test("bounded-stalled-read",async()=>{var t=Stopwatch.StartNew();await Error(()=>Exchange(null,delay:800,timeout:200),"timeout",false);Assert(t.ElapsedMilliseconds<2000);});
            Test("cancel-stalled-read",async()=>{using(var c=new CancellationTokenSource()){c.CancelAfter(120);bool stopped=false;try{await Exchange(null,delay:600,token:c.Token);}catch(OperationCanceledException){stopped=true;}Assert(stopped);}});
            Test("reconnect-after-timeout",async()=>{var s=await Exchange(null);Assert(s.Frame==3);});
            Test("status-can-wait-for-heavy-host",async()=>{var s=await Exchange(null,delay:3400,timeout:6000);Assert(s.Frame==3);});
            File.WriteAllText(args[0],new JavaScriptSerializer().Serialize(results));Console.WriteLine("Passed "+results.Count);return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
