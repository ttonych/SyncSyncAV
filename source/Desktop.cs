// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SyncSyncAV {
    public static class Desktop {
        [StructLayout(LayoutKind.Sequential)] public struct Rect {
            public int Left,Top,Right,Bottom;
            public Rectangle Bounds {get{return Rectangle.FromLTRB(Left,Top,Right,Bottom);}}
        }
        [StructLayout(LayoutKind.Sequential)] public struct Placement {
            public int Length,Flags,Show;public Point Min,Max;public Rect Normal;
        }
        [DllImport("user32.dll",SetLastError=true)] public static extern bool GetWindowPlacement(IntPtr h,ref Placement p);
        [DllImport("user32.dll",SetLastError=true)] public static extern bool SetWindowPlacement(IntPtr h,ref Placement p);
        [DllImport("user32.dll",SetLastError=true)] public static extern bool GetWindowRect(IntPtr h,out Rect r);
        [DllImport("user32.dll",SetLastError=true)] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int width,int height,uint flags);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern uint MapVirtualKeyW(uint code,uint map);
        // USER handles have 32 significant bits, even in a 64-bit process.
        internal static uint WindowId(IntPtr hwnd){return unchecked((uint)hwnd.ToInt64());}
        public static bool Owned(IntPtr h,int pid){uint actual;Native.GetWindowThreadProcessId(h,out actual);return Native.IsWindow(h)&&pid>0&&actual==(uint)pid;}
        public static Rectangle WorkArea(IntPtr h){return Screen.FromHandle(h).WorkingArea;}
        public static bool Control {get{return (GetAsyncKeyState(0x11)&0x8000)!=0;}}
        public static bool Shift {get{return (GetAsyncKeyState(0x10)&0x8000)!=0;}}
    }
    public sealed class LayoutController {
        private sealed class Saved {public IntPtr Hwnd;public int Pid;public Desktop.Placement Place;}
        private readonly List<Saved> saved=new List<Saved>();private int a,b;
        public bool CanRestore {get{return saved.Count>0;}}
        private static Saved Capture(IntPtr h,int pid) {
            if(!Desktop.Owned(h,pid))throw new InvalidOperationException(Ui.Text("WindowChanged"));
            var p=new Desktop.Placement{Length=Marshal.SizeOf(typeof(Desktop.Placement))};if(!Desktop.GetWindowPlacement(h,ref p))throw new Win32Exception();
            return new Saved{Hwnd=h,Pid=pid,Place=p};
        }
        private static async Task Wait(Func<bool> condition,CancellationToken token){var s=Stopwatch.StartNew();while(!condition()){if(s.ElapsedMilliseconds>3000)throw new InvalidOperationException(Ui.Text("LayoutUnconfirmed"));await Task.Delay(30,token);}}
        private static async Task Place(IntPtr h,int pid,Rectangle r,CancellationToken token) {
            if(!Desktop.Owned(h,pid))throw new InvalidOperationException(Ui.Text("WindowChanged"));
            if(Native.IsIconic(h)||Desktop.IsZoomed(h)){Native.ShowWindowAsync(h,9);await Wait(()=>Desktop.Owned(h,pid)&&!Native.IsIconic(h)&&!Desktop.IsZoomed(h),token);}
            if(!Desktop.SetWindowPos(h,IntPtr.Zero,r.X,r.Y,r.Width,r.Height,0x4050))throw new Win32Exception();
            await Wait(()=>{Desktop.Rect actual;return Desktop.Owned(h,pid)&&Desktop.GetWindowRect(h,out actual)&&actual.Bounds==r;},token);
        }
        public async Task TileAsync(IntPtr control,HostState hostA,HostState hostB,CancellationToken token) {
            if(hostA==null||hostB==null||hostA.Pid==hostB.Pid||hostA.Busy||hostB.Busy)throw new InvalidOperationException(Ui.Text("NeedFreeWindows"));
            if(CanRestore&&!((a==hostA.Pid&&b==hostB.Pid)||(a==hostB.Pid&&b==hostA.Pid)))throw new InvalidOperationException(Ui.Text("PairChanged"));
            var ah=new IntPtr(hostA.Hwnd);var bh=new IntPtr(hostB.Hwnd);int pid=Process.GetCurrentProcess().Id;
            var work=Desktop.WorkArea(control);int height=(int)Math.Round(188*Desktop.GetDpiForWindow(control)/96.0);
            if(work.Width<1180*Desktop.GetDpiForWindow(control)/96||work.Height-height<240)throw new InvalidOperationException(Ui.Text("MonitorTooSmall"));
            if(!CanRestore){var ca=Capture(ah,hostA.Pid);var cb=Capture(bh,hostB.Pid);var cc=Capture(control,pid);saved.AddRange(new[]{ca,cb,cc});a=hostA.Pid;b=hostB.Pid;}
            int half=work.Width/2;
            await Place(ah,hostA.Pid,new Rectangle(work.Left,work.Top,half,work.Height-height),token);
            await Place(bh,hostB.Pid,new Rectangle(work.Left+half,work.Top,work.Width-half,work.Height-height),token);
            await Place(control,pid,new Rectangle(work.Left,work.Bottom-height,work.Width,height),token);
            Native.SetForegroundWindow(control);
        }
        public async Task<string> RestoreAsync(CancellationToken token) {
            int missing=0;var failed=new List<Saved>();
            foreach(var s in saved){if(!Desktop.Owned(s.Hwnd,s.Pid)){missing++;continue;}
                try {var p=s.Place;p.Flags|=4;if(!Desktop.SetWindowPlacement(s.Hwnd,ref p))throw new Win32Exception();await Wait(()=>{var q=new Desktop.Placement{Length=Marshal.SizeOf(typeof(Desktop.Placement))};return Desktop.Owned(s.Hwnd,s.Pid)&&Desktop.GetWindowPlacement(s.Hwnd,ref q)&&q.Show==s.Place.Show&&q.Normal.Bounds==s.Place.Normal.Bounds;},token);}
                catch(OperationCanceledException){throw;}catch{failed.Add(s);}
            }
            saved.Clear();saved.AddRange(failed);if(failed.Count>0)throw new InvalidOperationException(Ui.Text("PartialRestore"));
            return Ui.Text("LayoutRestored")+(missing>0?Ui.Text("SkippedClosed")+missing+".":"");
        }
    }
    public sealed class HostKeys : IDisposable {
        [StructLayout(LayoutKind.Sequential)] private struct KeyData {public uint VirtualKey,Scan,Flags,Time;public UIntPtr Extra;}
        [StructLayout(LayoutKind.Sequential)] private struct GuiInfo {public int Size,Flags;public IntPtr Active,Focus,Capture,Menu,Move,Caret;public Desktop.Rect CaretRect;}
        private delegate IntPtr Callback(int code,IntPtr message,IntPtr data);
        [DllImport("user32.dll",SetLastError=true)] private static extern IntPtr SetWindowsHookExW(int id,Callback callback,IntPtr module,uint thread);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread,ref GuiInfo info);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
        [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent,IntPtr child);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window,uint flags);
        private readonly Callback callback;private IntPtr hook;
        public bool Enabled;public HostState A,B;public Func<uint,bool,bool,bool,bool> OnKey;
        private readonly HashSet<uint> consumed=new HashSet<uint>();
        public HostKeys(){callback=Handle;hook=SetWindowsHookExW(13,callback,GetModuleHandleW(null),0);if(hook==IntPtr.Zero)throw new Win32Exception();}
        private IntPtr Handle(int code,IntPtr message,IntPtr pointer) {
            if(code>=0) {
                var key=(KeyData)Marshal.PtrToStructure(pointer,typeof(KeyData));long msg=message.ToInt64();
                bool up=msg==0x101||msg==0x105;
                uint identity=key.Scan|((key.Flags&1)<<16);
                if(up&&consumed.Remove(identity))return new IntPtr(1);
                if(!up&&Enabled&&(msg==0x100||msg==0x104)) {
                    IntPtr fg=Desktop.GetForegroundWindow();HostState host=A!=null&&Desktop.WindowId(fg)==A.Hwnd?A:B!=null&&Desktop.WindowId(fg)==B.Hwnd?B:null;
                    if(host==null) {
                        var focused=new GuiInfo{Size=Marshal.SizeOf(typeof(GuiInfo))};
                        if(GetGUIThreadInfo(0,ref focused))foreach(var candidate in new[]{A,B}) {
                            if(candidate==null)continue;var h=new IntPtr(candidate.Hwnd);
                            if(GetAncestor(h,2)==fg&&(Desktop.WindowId(focused.Focus)==candidate.Hwnd||IsChild(h,focused.Focus))){host=candidate;break;}
                        }
                    }
                    IntPtr target=host==null?IntPtr.Zero:new IntPtr(host.Hwnd);
                    if(host!=null&&Desktop.Owned(target,host.Pid)&&Desktop.IsWindowEnabled(target)&&(Desktop.GetAsyncKeyState(0x12)&0x8000)==0&&(Desktop.GetAsyncKeyState(0x5b)&0x8000)==0&&(Desktop.GetAsyncKeyState(0x5c)&0x8000)==0) {
                        uint pid;uint thread=Native.GetWindowThreadProcessId(target,out pid);var info=new GuiInfo{Size=Marshal.SizeOf(typeof(GuiInfo))};
                        if(GetGUIThreadInfo(thread,ref info)&&(info.Flags&0x1e)==0) {
                            bool ctrl=(Desktop.GetAsyncKeyState(0x11)&0x8000)!=0;
                            if(OnKey!=null&&OnKey(key.Scan,ctrl,Desktop.Shift,(key.Flags&1)!=0)){consumed.Add(identity);return new IntPtr(1);}
                        }
                    }
                }
            }
            return CallNextHookEx(hook,code,message,pointer);
        }
        public void Dispose(){if(hook!=IntPtr.Zero){UnhookWindowsHookEx(hook);hook=IntPtr.Zero;}GC.KeepAlive(callback);}
    }
}
