// SPDX-License-Identifier: MIT
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;

namespace SyncSyncAV {
    internal static class EmbedNative {
        [StructLayout(LayoutKind.Sequential)] internal struct PaintInfo {public IntPtr Dc;public int Erase;public Desktop.Rect Rect;public int Restore,Update;[MarshalAs(UnmanagedType.ByValArray,SizeConst=32)]public byte[] Reserved;}
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateSolidBrush(uint color);
        [DllImport("user32.dll")] internal static extern int FillRect(IntPtr dc,ref Desktop.Rect rect,IntPtr brush);
        [DllImport("user32.dll")] internal static extern IntPtr BeginPaint(IntPtr h,out PaintInfo paint);
        [DllImport("user32.dll")] internal static extern bool EndPaint(IntPtr h,ref PaintInfo paint);

        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern IntPtr CreateWindowExW(uint ex,string cls,string title,uint style,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
        [DllImport("user32.dll",SetLastError=true)] internal static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] internal static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr h,out Desktop.Rect rect);
        [DllImport("user32.dll")] internal static extern bool PostMessageW(IntPtr h,uint msg,IntPtr wp,IntPtr lp);
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
        internal struct CopyData { public IntPtr Tag; public int Bytes; [MarshalAs(UnmanagedType.LPWStr)] public string Path; }
        [DllImport("user32.dll",SetLastError=true)]
        internal static extern IntPtr SendMessageTimeoutW(IntPtr hwnd,uint msg,IntPtr sender,ref CopyData data,uint flags,uint timeout,out IntPtr result);
        [DllImport("user32.dll")] internal static extern IntPtr SetFocus(IntPtr h);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern IntPtr CreateJobObjectW(IntPtr attrs,string name);
        [DllImport("kernel32.dll",SetLastError=true)] internal static extern bool SetInformationJobObject(IntPtr job,int info,ref JobLimits value,int length);
        [DllImport("kernel32.dll",SetLastError=true)] internal static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr h);
        [StructLayout(LayoutKind.Sequential)] internal struct BasicLimits {
            public long ProcessTime,JobTime;public uint Flags;public UIntPtr MinWorking,MaxWorking;
            public uint ActiveProcesses;public UIntPtr Affinity;public uint Priority,Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct IoCounters {
            public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct JobLimits {
            public BasicLimits Basic;public IoCounters Io;public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;
        }
    }

    // Owns only processes launched by this application. Closing the job also
    // cleans up engines if the shell exits unexpectedly.
    public sealed class EngineLifetime : IDisposable {
        private IntPtr job;
        public EngineLifetime() {
            job=EmbedNative.CreateJobObjectW(IntPtr.Zero,null);
            if(job==IntPtr.Zero)throw new Win32Exception();
            var limits=new EmbedNative.JobLimits();limits.Basic.Flags=0x2000; // KILL_ON_JOB_CLOSE
            if(!EmbedNative.SetInformationJobObject(job,9,ref limits,Marshal.SizeOf(typeof(EmbedNative.JobLimits)))) {
                int error=Marshal.GetLastWin32Error();Dispose();throw new Win32Exception(error);
            }
        }
        public void Add(Process process) {
            if(job==IntPtr.Zero||!EmbedNative.AssignProcessToJobObject(job,process.Handle)) {
                int error=Marshal.GetLastWin32Error();
                try{if(!process.HasExited)process.Kill();}catch(InvalidOperationException){}
                throw new Win32Exception(error,Ui.Text("AttachFailed"));
            }
        }
        public void Dispose(){if(job!=IntPtr.Zero){EmbedNative.CloseHandle(job);job=IntPtr.Zero;}}
    }

    public sealed class EmbeddedView : HwndHost {
        private IntPtr container,child;
        private TaskCompletionSource<int> playlistChange;
        private static readonly IntPtr canvasBrush=EmbedNative.CreateSolidBrush(0x15120f);
        private Process process;
        public int Pid {get{return process==null?0:process.Id;}}
        public IntPtr Child {get{return child;}}
        public bool Running {get{return process!=null&&!process.HasExited;}}
        public event Action<EmbeddedView> EngineExited;
        public EmbeddedView(){Focusable=true;}
        protected override HandleRef BuildWindowCore(HandleRef parent) {
            container=EmbedNative.CreateWindowExW(0,"static","",0x56000000,0,0,1,1,parent.Handle,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
            if(container==IntPtr.Zero)throw new Win32Exception();
            return new HandleRef(this,container);
        }
        protected override IntPtr WndProc(IntPtr hwnd,int msg,IntPtr wp,IntPtr lp,ref bool handled) {
            if(msg==0x8000+105&&Desktop.WindowId(wp)==Desktop.WindowId(child)&&playlistChange!=null) {
                playlistChange.TrySetResult(lp.ToInt32());
                handled=true;return IntPtr.Zero;
            }
            if(msg==0x14||msg==0xf) {
                Desktop.Rect bounds;EmbedNative.GetClientRect(hwnd,out bounds);
                if(msg==0xf){EmbedNative.PaintInfo paint;var dc=EmbedNative.BeginPaint(hwnd,out paint);EmbedNative.FillRect(dc,ref bounds,canvasBrush);EmbedNative.EndPaint(hwnd,ref paint);}
                else EmbedNative.FillRect(wp,ref bounds,canvasBrush);
                handled=true;return new IntPtr(1);
            }
            return base.WndProc(hwnd,msg,wp,lp,ref handled);
        }
        protected override void DestroyWindowCore(HandleRef hwnd) {
            // Engines are stopped by the shell before the hosting HWND is freed.
            EmbedNative.DestroyWindow(hwnd.Handle);container=IntPtr.Zero;
        }
        protected override void OnWindowPositionChanged(Rect rect){base.OnWindowPositionChanged(rect);Fit();}
        protected override bool TabIntoCore(TraversalRequest request){if(child==IntPtr.Zero)return false;EmbedNative.SetFocus(child);return true;}
        public void Fit() {
            Desktop.Rect r;
            if(child!=IntPtr.Zero&&container!=IntPtr.Zero&&EmbedNative.GetClientRect(container,out r))
                Desktop.SetWindowPos(child,IntPtr.Zero,0,0,Math.Max(1,r.Right),Math.Max(1,r.Bottom),0x4014);
        }
        public async Task<HostState> StartAsync(string executable,EngineLifetime lifetime,CancellationToken token) {
            token.ThrowIfCancellationRequested();
            if(container==IntPtr.Zero)throw new InvalidOperationException(Ui.Text("PaneNotCreated"));
            if(Running)throw new InvalidOperationException(Ui.Text("PreviewAlreadyRunning"));
            if(!File.Exists(executable))throw new FileNotFoundException(Ui.Text("MissingEngine"),executable);
            if(process!=null){process.Dispose();process=null;}child=IntPtr.Zero;
            process=Process.Start(new ProcessStartInfo(executable){Arguments="/syncsyncavembedded",UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(executable),WindowStyle=ProcessWindowStyle.Hidden});
            try {
            lifetime.Add(process);
            var client=new BridgeClient(2000);HostState state=null;var watch=Stopwatch.StartNew();
            while(state==null) {
                token.ThrowIfCancellationRequested();
                if(process.HasExited)throw new InvalidOperationException(Ui.Text("EngineExitedAtStart"));
                try{state=await client.StatusAsync(process.Id,token);}
                catch(BridgeException){if(watch.ElapsedMilliseconds>20000)throw;await Task.Delay(100,token);}
            }
            child=new IntPtr(state.Hwnd);
            if(!Desktop.Owned(child,process.Id))throw new InvalidOperationException(Ui.Text("WrongEngineWindow"));
            state=await new BridgeClient().EmbedAsync(process.Id,container,token);
            if(EmbedNative.GetParent(child)!=container)throw new InvalidOperationException(Ui.Text("EmbedUnconfirmed"));
            ApplyLanguage();Fit();
            var started=process;
            process.Exited+=(s,e)=>{
                if(Dispatcher.HasShutdownStarted)return;
                try{Dispatcher.BeginInvoke(new Action(()=>{if(process!=started)return;child=IntPtr.Zero;if(playlistChange!=null)playlistChange.TrySetResult(-1);if(EngineExited!=null)EngineExited(this);}));}
                catch(InvalidOperationException){}
            };
            process.EnableRaisingEvents=true;
            return state;
            }catch{StopOwnedProcess();throw;}
        }
        internal void ApplyLanguage() {
            // The embedded host accepts WM_USER+104 only from its own parent.
            if(child!=IntPtr.Zero&&Running)EmbedNative.PostMessageW(child,0x400+104,container,new IntPtr(Ui.Code=="ru"?0x419:0x409));
        }
        public void OpenDialog() {
            if(!Running||!Desktop.Owned(child,Pid))throw new InvalidOperationException(Ui.Text("PreviewNotRunning"));
            if(!Desktop.IsWindowEnabled(child))return;
            EmbedNative.PostMessageW(child,0x111,new IntPtr(40001),IntPtr.Zero);
        }
        public Task OpenDroppedFileAsync(string path) {
            if(!Running||!Desktop.Owned(child,Pid)||!Desktop.IsWindowEnabled(child))
                throw new InvalidOperationException(Ui.Text("PreviewNotReady"));
            var target=child;var sender=container;
            return Task.Run(()=>{
                var data=new EmbedNative.CopyData{Tag=new IntPtr(0x41424631),Bytes=checked((path.Length+1)*2),Path=path};
                IntPtr accepted;
                if(EmbedNative.SendMessageTimeoutW(target,0x4a,sender,ref data,2,3000,out accepted)==IntPtr.Zero || accepted==IntPtr.Zero)
                    throw new InvalidOperationException(Ui.Text("DropRejected")+Path.GetFileName(path));
            });
        }
        public async Task<bool> ChangePlaylistAsync(CancellationToken token) {
            if(playlistChange!=null||!Running||!Desktop.Owned(child,Pid)||!Desktop.IsWindowEnabled(child))
                throw new InvalidOperationException(Ui.Text("PreviewNotReady"));
            var completion=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            playlistChange=completion;
            try {
                token.ThrowIfCancellationRequested();
                if(!EmbedNative.PostMessageW(child,0x400+105,container,IntPtr.Zero))
                    throw new InvalidOperationException(Ui.Text("PlaylistChangeFailed"));
                using(token.Register(()=>completion.TrySetCanceled())) {
                    int result=await completion.Task;
                    if(result<0)throw new InvalidOperationException(Ui.Text("PlaylistChangeFailed"));
                    return result==1;
                }
            } finally {playlistChange=null;}
        }
        public void CloseMedia() {
            // ID_FILE_CLOSEAVI closes the input and leaves the engine running.
            if(!Running||!Desktop.Owned(child,Pid)||!Desktop.IsWindowEnabled(child)||
               !EmbedNative.PostMessageW(child,0x111,new IntPtr(40016),IntPtr.Zero))
                throw new InvalidOperationException(Ui.Text("CloseMediaFailed"));
        }
        public void RequestClose(){if(Running&&Desktop.Owned(child,Pid))EmbedNative.PostMessageW(child,0x10,IntPtr.Zero,IntPtr.Zero);}
        public void StopOwnedProcess(){if(process!=null){try{if(!process.HasExited)process.Kill();}catch(InvalidOperationException){}child=IntPtr.Zero;}}
    }
}
