// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace SyncSyncAV {
    public sealed class HostState {
        public int Pid;
        public long Hwnd;
        public string File, Executable, BridgeVersion;
        public bool HasMedia, Busy;
        public long? Frame;
        public long Count;
        public int Subsets;
        public DiscMetadata Video;
        public string PlaylistPath {get {
            string resolved=Video==null?null:Video.ResolvedFile;
            return HasMedia&&resolved!=null&&resolved.EndsWith(".mpls",StringComparison.OrdinalIgnoreCase)?resolved:null;
        }}
        public string Fingerprint { get { return Pid + "|" + Hwnd + "|" + Executable + "|" + File + "|" + Count + "|" + Subsets + (Video==null?"":"|"+Video.ResolvedFile+"|"+Video.FpsNumerator+"/"+Video.FpsDenominator); } }
        public string ShortName {
            get {
                string media = String.IsNullOrEmpty(File) ? Ui.Text("NoVideoShort") : File;
                return "PID " + Pid + " · " + media;
            }
        }
        public override string ToString() { return ShortName; }
        public HostState Copy() { return (HostState)MemberwiseClone(); }
    }

    public sealed class BridgeException : Exception {
        public string Code { get; private set; }
        public bool MayHaveApplied { get; private set; }
        public BridgeException(string code, string message, bool mayHaveApplied=false) : base(message) {
            Code=code; MayHaveApplied=mayHaveApplied;
        }
    }

    public interface IBridgeClient {
        Task<HostState> StatusAsync(int pid, CancellationToken token);
        Task<HostState> SeekAsync(int pid, long frame, CancellationToken token);
    }

    public sealed class BridgeClient : IBridgeClient {
        private static int nextId=Environment.TickCount;
        private const uint Magic=0x31434456;
        private readonly int timeout;
        public BridgeClient(int timeoutMilliseconds=20000) { timeout=timeoutMilliseconds; }
        public Task<HostState> StatusAsync(int pid, CancellationToken token) { return RequestAsync(pid,1,0,token); }
        public Task<HostState> SeekAsync(int pid, long frame, CancellationToken token) { return RequestAsync(pid,3,frame,token); }
        public Task<HostState> EmbedAsync(int pid, IntPtr parent, CancellationToken token) { return RequestAsync(pid,6,unchecked((uint)parent.ToInt64()),token); }

        public async Task<HostState> RequestAsync(int pid, ushort operation, long argument, CancellationToken token) {
            if(pid<=0) throw new ArgumentOutOfRangeException("pid");
            uint id=unchecked((uint)Interlocked.Increment(ref nextId));
            byte[] packet;
            using(var bytes=new MemoryStream()) {
                using(var writer=new BinaryWriter(bytes,Encoding.UTF8,true)) {
                    writer.Write(Magic); writer.Write((ushort)1); writer.Write(operation);
                    writer.Write(id); writer.Write((uint)0); writer.Write(argument);
                    writer.Write((uint)0); writer.Write((uint)0);
                }
                packet=bytes.ToArray();
            }
            // Dispose aborts a pending OS pipe read even on Framework builds where
            // cancellation alone does not interrupt an already-issued ReadAsync.
            using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token))
            using(var pipe=new NamedPipeClientStream(".","vdub-sdk-"+pid,PipeDirection.InOut,PipeOptions.Asynchronous)) {
                // Reading status also uses the host UI dispatcher. During a heavy
                // decode it must be allowed to reach the bridge's 16 s deadline.
                int limit=operation==1?Math.Min(timeout,18000):timeout;
                deadline.CancelAfter(limit);
                bool sent=false;
                using(deadline.Token.Register(()=>{ try { pipe.Dispose(); } catch(ObjectDisposedException) {} })) {
                    try {
                        // A single-instance named pipe can report ERROR_SEM_TIMEOUT
                        // while another client is finishing its response handshake.
                        // Retry only connection acquisition, before any request bytes.
                        for(;;) {
                            try {await pipe.ConnectAsync(limit,deadline.Token).ConfigureAwait(false);break;}
                            catch(IOException e) {int code=e.HResult&0xffff;if(code!=121&&code!=231&&code!=2)throw;await Task.Delay(50,deadline.Token).ConfigureAwait(false);}
                            catch(Win32Exception e) {if(e.NativeErrorCode!=121&&e.NativeErrorCode!=231&&e.NativeErrorCode!=2)throw;await Task.Delay(50,deadline.Token).ConfigureAwait(false);}
                        }
                        uint server;
                        if(!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle,out server)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        if(server!=(uint)pid) throw new BridgeException("identity_mismatch",Ui.Text("ServerPidMismatch"));
                        pipe.ReadMode=PipeTransmissionMode.Message;
                        sent=true; // Conservatively uncertain even if a partial write fails.
                        await pipe.WriteAsync(packet,0,packet.Length,deadline.Token).ConfigureAwait(false);
                        byte[] reply;
                        using(var result=new MemoryStream()) {
                            byte[] buffer=new byte[8192];
                            do {
                                int read=await pipe.ReadAsync(buffer,0,buffer.Length,deadline.Token).ConfigureAwait(false);
                                if(read==0) throw new IOException(Ui.Text("SdkClosed"));
                                if(result.Length+read>262144) throw new IOException(Ui.Text("SdkReplyTooLarge"));
                                result.Write(buffer,0,read);
                            } while(!pipe.IsMessageComplete);
                            reply=result.ToArray();
                        }
                        await pipe.WriteAsync(new byte[]{6},0,1,deadline.Token).ConfigureAwait(false);
                        var serializer=new JavaScriptSerializer { MaxJsonLength=262144, RecursionLimit=32 };
                        var json=serializer.Deserialize<Dictionary<string,object>>(new UTF8Encoding(false,true).GetString(reply));
                        if(Number(json,"id")!=id) throw new BridgeException("identity_mismatch",Ui.Text("ReplyIdMismatch"),sent);
                        if(!Boolean(json,"ok")) {
                            var error=Object(json,"error");
                            throw new BridgeException(Text(error,"code"),Ui.BridgeDetail(Text(error,"message")),Boolean(error,"may_have_applied"));
                        }
                        var s=Object(json,"state");
                        var state=new HostState {
                            Pid=checked((int)Number(s,"pid")),Hwnd=Number(s,"hwnd"),
                            File=Text(s,"file"),Executable=Text(s,"executable"),BridgeVersion=Text(s,"bridge_version"),
                            HasMedia=Boolean(s,"has_media"),Busy=Boolean(s,"busy"),
                            Frame=s["frame"]==null?(long?)null:Number(s,"frame"),Count=Number(s,"frame_count"),
                            Subsets=checked((int)Number(s,"subset_count"))
                        };
                        object video;
                        if(s.TryGetValue("video",out video)&&video is Dictionary<string,object>) {
                            var v=(Dictionary<string,object>)video;
                            int num=checked((int)Number(v,"fps_num")),den=checked((int)Number(v,"fps_den"));
                            int width=checked((int)Number(v,"width")),height=checked((int)Number(v,"height"));
                            if(num<=0||den<=0||width<=0||height<=0)throw new FormatException(Ui.Text("InvalidMetadata"));
                            string codec=Text(v,"codec");if(codec=="h264")codec="AVC";else if(codec=="hevc")codec="HEVC";else codec=codec.ToUpperInvariant();
                            state.Video=new DiscMetadata{FpsNumerator=num,FpsDenominator=den,Fps=(double)num/den,Width=width,Height=height,Codec=codec,Origin="input_driver",ResolvedFile=Text(v,"resolved_file")};
                        }
                        if(state.Pid!=pid) throw new BridgeException("identity_mismatch",Ui.Text("WrongProcess"),sent);
                        if(Number(s,"sdk_api")<2 || Text(s,"readiness")!="timeline_only") throw new BridgeException("unsupported_bridge",Ui.Text("UnsupportedBridge"),sent);
                        // The native End command selects the exclusive end cursor
                        // (frame == count). It is a valid state, not a seek target.
                        if(state.Count<0 || state.Subsets<0 || state.Hwnd<=0 || (state.HasMedia && (!state.Frame.HasValue || state.Frame<0 || state.Frame>state.Count)))
                            throw new BridgeException("invalid_state",Ui.Text("InvalidHostFrame"),sent);
                        return state;
                    } catch(BridgeException) { throw; }
                    catch(Exception e) {
                        if(token.IsCancellationRequested) throw new OperationCanceledException(token);
                        if(deadline.IsCancellationRequested || e is TimeoutException)
                            throw new BridgeException("timeout",Ui.Text("WindowPrefix")+pid+Ui.Text("TimedOut"),sent && operation!=1);
                        throw new BridgeException("connection_failed",Ui.Text("WindowPrefix")+pid+": "+e.Message,sent && operation!=1);
                    }
                }
            }
        }

        internal static Dictionary<string,object> Object(Dictionary<string,object> d,string k) {
            object value;
            if(d==null || !d.TryGetValue(k,out value) || !(value is Dictionary<string,object>)) throw new FormatException(Ui.Text("MissingObject")+k);
            return (Dictionary<string,object>)value;
        }
        internal static long Number(Dictionary<string,object> d,string k) {
            object value;
            if(d==null || !d.TryGetValue(k,out value) || !(value is int || value is long || value is decimal)) throw new FormatException(Ui.Text("MissingInteger")+k);
            decimal n=Convert.ToDecimal(value);
            if(n!=Decimal.Truncate(n) || n<Int64.MinValue || n>Int64.MaxValue) throw new FormatException(Ui.Text("InvalidInteger")+k);
            return (long)n;
        }
        internal static string Text(Dictionary<string,object> d,string k) {
            object value;
            if(d==null || !d.TryGetValue(k,out value) || !(value is string)) throw new FormatException(Ui.Text("MissingString")+k);
            return (string)value;
        }
        internal static bool Boolean(Dictionary<string,object> d,string k) {
            object value;
            if(d==null || !d.TryGetValue(k,out value) || !(value is bool)) throw new FormatException(Ui.Text("MissingBoolean")+k);
            return (bool)value;
        }
        public static List<int> EnumeratePids() {
            var result=new List<int>(); Native.FindData data;
            IntPtr h=Native.FindFirstFileW(@"\\.\pipe\*",out data);
            if(h==new IntPtr(-1)) return result;
            try {
                do {
                    const string prefix="vdub-sdk-";
                    int pid;
                    if(data.Name.StartsWith(prefix,StringComparison.Ordinal) && Int32.TryParse(data.Name.Substring(prefix.Length),out pid) && pid>0) result.Add(pid);
                } while(Native.FindNextFileW(h,out data));
            } finally { Native.FindClose(h); }
            return result.Distinct().OrderBy(x=>x).ToList();
        }
    }

    internal static class Native {
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
        internal struct FindData {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation,Access,Write;
            public uint SizeHigh,SizeLow,Reserved0,Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=14)] public string AlternateName;
        }
        [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint pid);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern IntPtr FindFirstFileW(string pattern,out FindData data);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool FindNextFileW(IntPtr h,out FindData data);
        [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool FindClose(IntPtr h);
        [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
        [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindowAsync(IntPtr h,int command);
        [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(IntPtr h);
    }
}
