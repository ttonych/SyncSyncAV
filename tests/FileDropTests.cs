// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using SyncSyncAV;

static class FileDropTests {
    static Window window;
    static IntegratedShell shell;
    static string folder;
    static readonly List<string> passed=new List<string>();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetPropW(IntPtr h,string key);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    [DllImport("user32.dll")] static extern int GetWindowLongW(IntPtr h,int index);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Pixel screen);
    [StructLayout(LayoutKind.Sequential)] struct Pixel { public int X,Y; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int OleDrop(IntPtr self,IntPtr obj,uint keys,Pixel pt,ref uint effect);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int OleDragOver(IntPtr self,uint keys,Pixel pt,ref uint effect);

    static void Assert(bool ok,string why){if(!ok)throw new Exception(why);}
    static void Pass(string name){passed.Add(name);File.WriteAllText(Path.Combine(folder,"report.json"),new JavaScriptSerializer().Serialize(passed));}
    static async Task Wait(Func<bool> check,string why) {
        var clock=Stopwatch.StartNew();
        while(!check()) {
            if(clock.Elapsed.TotalSeconds>15)throw new Exception(why+" · "+shell.Controller.Session.Message);
            await Task.Delay(75);
        }
    }
    static T FindControl<T>(DependencyObject root,string id) where T:FrameworkElement {
        var button=root as T;
        if(button!=null&&AutomationProperties.GetAutomationId(button)==id)return button;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);++i) {
            var found=FindControl<T>(VisualTreeHelper.GetChild(root,i),id);
            if(found!=null)return found;
        }
        return null;
    }
    static Button Button(string id){return FindControl<Button>(window,id);}
    static void Click(string id) {
        var button=Button(id);
        Assert(button!=null&&button.IsVisible&&button.IsEnabled,"Unavailable button "+id);
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    }
    static bool Media(HostState state,string path) {
        return state!=null&&!state.Busy&&(path==null?!state.HasMedia:state.HasMedia&&String.Equals(state.File,path,StringComparison.OrdinalIgnoreCase));
    }
    static Task Expect(string a,string b) {
        var session=shell.Controller.Session;
        return Wait(()=>!shell.Controller.Working&&Media(session.HostA,a)&&Media(session.HostB,b),"Unexpected A/B media: "+a+" / "+b);
    }
    static T Method<T>(IntPtr target,int slot) where T:class {
        return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(target),slot*IntPtr.Size),typeof(T));
    }
    static void Drop(string side,string[] paths,bool header=false,bool accepted=true) {
        var open=Button("Open"+side);
        var toolbar=(FrameworkElement)open.Parent;
        var point=toolbar.PointToScreen(new Point(toolbar.ActualWidth/2,header?toolbar.ActualHeight/2:155));
        var pixel=new Pixel{X=(int)point.X,Y=(int)point.Y};
        // Start at the actual HWND under the point, including the native
        // decoder's child windows. OLE routes up to the registered ancestor.
        IntPtr root=new WindowInteropHelper(window).Handle;
        IntPtr hit=WindowFromPoint(pixel);
        var current=side=="A"?shell.Controller.Session.HostA:shell.Controller.Session.HostB;
        if(!header&&current.HasMedia) {
            uint pid;GetWindowThreadProcessId(hit,out pid);
            Assert(pid==current.Pid,"Drop point missed the embedded engine");
        }
        IntPtr target=IntPtr.Zero;
        for(var h=hit;h!=IntPtr.Zero;h=GetParent(h)) {
            Assert((GetWindowLongW(h,-20)&0x10)==0,"An HWND would intercept the drop via WS_EX_ACCEPTFILES");
            target=GetPropW(h,"OleDropTargetInterface");
            if(target!=IntPtr.Zero){Assert(h==root,"Unexpected drop handler in a child window");break;}
        }
        Assert(target!=IntPtr.Zero,"No OLE target under "+side+"; hit="+hit+" root="+root);
        var data=new DataObject(DataFormats.FileDrop,paths);
        var obj=Marshal.GetComInterfaceForObject(data,typeof(System.Runtime.InteropServices.ComTypes.IDataObject));
        try {
            uint effect=1;
            Marshal.ThrowExceptionForHR(Method<OleDrop>(target,3)(target,obj,1,pixel,ref effect));
            effect=1;
            Marshal.ThrowExceptionForHR(Method<OleDragOver>(target,4)(target,1,pixel,ref effect));
            Assert(effect==(accepted?1u:0u),"Unexpected drag-over effect "+effect+" ("+side+")");
            foreach(string label in new[]{"A","B"}) {
                var title=FindControl<TextBlock>(window,"MediaTitle"+label);
                int index=label==side?0:1;
                if(accepted&&index<paths.Length) {
                    var path=paths[index];
                    Assert(!FindControl<TextBlock>(window,"PlaylistName"+label).IsVisible,"Old playlist during drop preview");
                    string name=Path.GetFileName(path.TrimEnd('\\','/'));
                    Assert(title.Text==label+" ← "+(name.Length>0?name:path),"Incorrect drop preview");
                }
                else Assert(!title.Text.StartsWith(label+" ← "),"Stale drop preview");
            }
            effect=1;
            Marshal.ThrowExceptionForHR(Method<OleDrop>(target,6)(target,obj,0,pixel,ref effect));
            Assert(effect==(accepted?1u:0u),"Unexpected drop effect "+effect);
        } finally {Marshal.Release(obj);}
    }
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent,EnumWindow visitor,IntPtr data);
    delegate bool EnumWindow(IntPtr hwnd,IntPtr data);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr hwnd);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SendMessageTimeoutW(IntPtr hwnd,uint msg,IntPtr capacity,StringBuilder text,uint flags,uint timeout,out IntPtr result);

    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr hwnd,StringBuilder text,int capacity);
    static string NativeFrame(HostState host) {
        var text=new StringBuilder(256);
        EnumChildWindows(new IntPtr(host.Hwnd),(h,l)=>{
            if(GetDlgCtrlID(h)!=501)return true;
            var name=new StringBuilder(64);
            GetClassNameW(h,name,name.Capacity);
            if(name.ToString()!="Static")return true;
            IntPtr result;
            SendMessageTimeoutW(h,0x000d,new IntPtr(text.Capacity),text,2,300,out result);
            return false;
        },IntPtr.Zero);
        return text.ToString().TrimStart();
    }
    static async Task SwitchLanguage(bool russian) {
        shell.Controller.OpenConnections();
        var settings=window.OwnedWindows.Cast<Window>().Single();
        settings.UpdateLayout();
        var picker=FindControl<ComboBox>(settings,"Language");
        var template=FindControl<TextBox>(settings,"OffsetCopyTemplate");
        string draft=template.Text+" test";
        template.Text=draft;
        picker.SelectedIndex=russian?0:1;
        settings.UpdateLayout();
        Assert(settings.Title==(russian?"Настройки":"Settings"),"Settings did not update in place");
        Assert(FindControl<TextBox>(settings,"OffsetCopyTemplate").Text==draft,"Language change lost draft");
        Assert(window.Title==(russian?"SyncSyncAV · два видео":"SyncSyncAV · Dual video preview"),"Stale window title");
        Assert(AutomationProperties.GetName(shell.Controller.B("CopyOffset"))==(russian?"Скопировать смещение":"Copy offset"),"Stale XAML accessibility name");
        Assert((string)Button("OpenA").Content==(russian?"Заменить…":"Replace…"),"Stale preview header");
        Assert(((TextBlock)shell.Controller.B("Points").Content).Text.StartsWith(russian?"Точки":"Sync points"),"Stale point count");
        string frame=russian?"Кадр ":"Frame ";
        await Wait(()=>NativeFrame(shell.Controller.Session.HostA).StartsWith(frame)&&NativeFrame(shell.Controller.Session.HostB).StartsWith(frame),"Native timeline language");
        settings.Close();
    }
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow visitor,IntPtr data);
    [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr hwnd,int id);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr hwnd,uint msg,IntPtr wp,IntPtr lp);
    static string Caption(IntPtr h) {
        var text=new StringBuilder(1024);IntPtr result;
        SendMessageTimeoutW(h,13,new IntPtr(text.Capacity),text,2,300,out result);
        return text.ToString();
    }
    static IntPtr EngineDialog(int pid,int control) {
        IntPtr dialog=IntPtr.Zero;
        EnumWindows((h,l)=>{
            uint owner;GetWindowThreadProcessId(h,out owner);
            if(owner!=pid||GetDlgItem(h,control)==IntPtr.Zero)return true;
            var name=new StringBuilder(64);
            GetClassNameW(h,name,name.Capacity);
            if(name.ToString()!="#32770")return true;
            dialog=h;return false;
        },IntPtr.Zero);
        return dialog;
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SendMessageTimeoutW(IntPtr hwnd,uint msg,IntPtr wp,string text,uint flags,uint timeout,out IntPtr result);
    static string WindowClass(IntPtr hwnd) {
        var name=new StringBuilder(128);
        GetClassNameW(hwnd,name,name.Capacity);
        return name.ToString();
    }
    static IntPtr Descendant(IntPtr parent,Func<IntPtr,bool> match) {
        IntPtr found=IntPtr.Zero;
        EnumChildWindows(parent,(h,l)=>{
            if(!match(h))return true;
            found=h;return false;
        },IntPtr.Zero);
        return found;
    }
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr hwnd);
    static async Task CheckExplorerControls(IntPtr dialog) {
        var info=Descendant(dialog,h=>WindowClass(h)=="Button"&&Caption(h)=="File information...");
        var options=Descendant(dialog,h=>WindowClass(h)=="Button"&&Caption(h).StartsWith("Options"));
        var sequence=Descendant(dialog,h=>WindowClass(h)=="Button"&&Caption(h).StartsWith("Sequence"));
        var segments=Descendant(dialog,h=>WindowClass(h)=="Button"&&Caption(h)=="Linked segments");
        Assert(info!=IntPtr.Zero&&options!=IntPtr.Zero&&segments!=IntPtr.Zero&&sequence!=IntPtr.Zero,"Missing decoder or mode controls");
        await Wait(()=>IsWindowEnabled(info)&&Descendant(dialog,h=>WindowClass(h)=="Static"&&Caption(h).Contains("matroska"))!=IntPtr.Zero,"Typed file did not update decoder details");
        PostMessageW(sequence,0x00f5,IntPtr.Zero,IntPtr.Zero);
        await Wait(()=>Caption(sequence).StartsWith("Sequence:"),"Sequence file count");
        PostMessageW(segments,0x00f5,IntPtr.Zero,IntPtr.Zero);
        await Wait(()=>Caption(sequence)=="Sequence","Return to linked segments");
    }
    static async Task<IntPtr> ShowExplorer(string side,string button,bool russian=false) {
        int pid=side=="A"?shell.PidA:shell.PidB;
        await Wait(()=>Button(button+side).IsVisible&&Button(button+side).IsEnabled,"Open action available on "+side);
        Click(button+side);
        IntPtr dialog=IntPtr.Zero;
        await Wait(()=>{
            dialog=EngineDialog(pid,1);
            return dialog!=IntPtr.Zero&&Caption(dialog)==(russian?"Открыть видео":"Open video file")&&
                Descendant(dialog,h=>WindowClass(h)=="NamespaceTreeControl")!=IntPtr.Zero;
        },"Missing Explorer navigation pane on "+side);
        return dialog;
    }
    static async Task EnterFileName(IntPtr dialog,string path) {
        IntPtr edit=IntPtr.Zero,result;
        await Wait(()=>(edit=Descendant(dialog,h=>WindowClass(h)=="Edit"&&WindowClass(GetParent(h))=="ComboBox"))!=IntPtr.Zero,"File name field in Explorer");
        Assert(SendMessageTimeoutW(edit,0x000c,IntPtr.Zero,path,2,1500,out result)!=IntPtr.Zero,"Could not enter a file name");
    }
    static async Task ExplorerOpen(string side,string button,string path,bool russian=false) {
        int pid=side=="A"?shell.PidA:shell.PidB;
        var dialog=await ShowExplorer(side,button,russian);
        if(path==null) {
            PostMessageW(dialog,0x111,new IntPtr(2),IntPtr.Zero);
        } else {
            await EnterFileName(dialog,path);
            if(button=="EmptyOpen"&&Path.GetExtension(path)==".mkv")await CheckExplorerControls(dialog);
            PostMessageW(dialog,0x111,new IntPtr(1),IntPtr.Zero);
        }
        await Wait(()=>EngineDialog(pid,1)!=dialog,"Explorer dialog did not close on "+side);
        if(path==null)await Wait(()=>Button("EmptyOpen"+side).IsEnabled||Button("Open"+side).IsEnabled,"Pane ready after dialog on "+side);
    }
    static async Task SelectExplorerItem(IntPtr dialog,string name) {
        // Type-to-select in the actual Explorer file list, on the isolated test desktop.
        IntPtr list=IntPtr.Zero;
        await Wait(()=>{
            var view=Descendant(dialog,h=>WindowClass(h)=="SHELLDLL_DefView");
            if(view!=IntPtr.Zero)list=Descendant(view,h=>WindowClass(h)=="DirectUIHWND"&&IsWindowVisible(h));
            return list!=IntPtr.Zero;
        },"Explorer file list");
        // Give the shell view keyboard focus before its incremental filename search.
        var firstRow=new IntPtr((36<<16)|42);
        await Wait(()=>{
            var info=Descendant(dialog,h=>WindowClass(h)=="Button"&&Caption(h)=="File information...");
            if(DiscButton(dialog)!=IntPtr.Zero&&info!=IntPtr.Zero&&!IsWindowEnabled(info))return true;
            PostMessageW(list,0x201,new IntPtr(1),firstRow);
            PostMessageW(list,0x202,IntPtr.Zero,firstRow);
            PostMessageW(list,0x100,new IntPtr(0x24),IntPtr.Zero); // Home
            PostMessageW(list,0x101,new IntPtr(0x24),IntPtr.Zero);
            foreach(char c in name)PostMessageW(list,0x102,new IntPtr(c),new IntPtr(1));
            return false;
        },"Select disc folder "+name);
    }
    static Task InExplorerFolder(IntPtr dialog,string path) {
        return Wait(()=>Descendant(dialog,h=>WindowClass(h)=="ToolbarWindow32"&&Caption(h).EndsWith(path,StringComparison.OrdinalIgnoreCase))!=IntPtr.Zero,"Browse into "+path);
    }
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    static IntPtr DiscButton(IntPtr dialog) {
        return Descendant(dialog,h=>WindowClass(h)=="Button"&&Caption(h)=="Choose playlist..."&&IsWindowVisible(h));
    }
    static async Task CheckExplorerFolders(string first) {
        string parent=Directory.CreateDirectory(Path.Combine(folder,"Browser")).FullName;
        string root=Path.Combine(parent,"sample-disc"),bdmv=Path.Combine(root,"BDMV");
        Directory.Move(Path.Combine(folder,"sample-disc"),root);
        await CheckPlayableDisc("A",root,open:async()=>{
            var dialog=await ShowExplorer("A","EmptyOpen");
            await EnterFileName(dialog,parent);
            PostMessageW(dialog,0x111,new IntPtr(1),IntPtr.Zero);
            await InExplorerFolder(dialog,parent);
            await SelectExplorerItem(dialog,Path.GetFileName(root));
            PostMessageW(GetDlgItem(dialog,1),0x00f5,IntPtr.Zero,IntPtr.Zero);
        });
        var session=shell.Controller.Session;
        string fileA=session.HostA.File,bonus=Path.Combine(root,"bonus.mkv");
        File.Copy(first,bonus);
        await ExplorerOpen("B","EmptyOpen",bonus);
        await Expect(fileA,bonus);
        await shell.Controller.EnterField("FrameB","18");
        var before=session.HostB;
        var replace=await ShowExplorer("B","Open");
        await InExplorerFolder(replace,root);
        await Wait(()=>Descendant(replace,h=>WindowClass(h)=="Button"&&Caption(h)=="File information..."&&IsWindowEnabled(h))!=IntPtr.Zero,"Previous video selected in Replace");
        await SelectExplorerItem(replace,"BDMV");
        PostMessageW(GetDlgItem(replace,1),0x00f5,IntPtr.Zero,IntPtr.Zero);
        IntPtr picker=IntPtr.Zero;
        await Wait(()=>(picker=EngineDialog(shell.PidB,2201))!=IntPtr.Zero,"Replace B with selected BDMV");
        PostMessageW(picker,0x111,new IntPtr(2),IntPtr.Zero);
        await Wait(()=>EngineDialog(shell.PidB,2201)==IntPtr.Zero,"Cancel folder playlist");
        await session.PollAsync(CancellationToken.None);
        Assert(session.HostB.Fingerprint==before.Fingerprint&&session.HostB.Frame==18,"Cancelled disc replacement changed B");
        await CheckPlayableDisc("B",bdmv,open:async()=>{
            var dialog=await ShowExplorer("B","Open");
            IntPtr action=IntPtr.Zero;
            await Wait(()=>(action=DiscButton(dialog))!=IntPtr.Zero,"Choose from current disc folder");
            PostMessageW(action,0x00f5,IntPtr.Zero,IntPtr.Zero);
        });
        Pass("Open a disc root from the file list, replace from inside a disc, and cancel a BDMV replacement without moving either pane");
        var incomplete=Path.Combine(folder,"Incomplete","BDMV");
        File.Copy(first,Path.Combine(incomplete,"bonus.mkv"));
        var browser=await ShowExplorer("B","Open");
        await EnterFileName(browser,incomplete);
        PostMessageW(browser,0x111,new IntPtr(1),IntPtr.Zero);
        await InExplorerFolder(browser,incomplete);
        await EnterFileName(browser,"bonus.mkv");
        Assert(DiscButton(browser)==IntPtr.Zero,"Incomplete BDMV offered playlists");
        PostMessageW(browser,0x111,new IntPtr(1),IntPtr.Zero);
        await Expect(fileA,Path.Combine(incomplete,"bonus.mkv"));
        Pass("ordinary files remain accessible in an incomplete BDMV folder");
    }
    static string MakeTestDisc() {
        var bdmv=Path.Combine(folder,"Тестовый диск.2026","BDMV");
        Directory.CreateDirectory(Path.Combine(bdmv,"PLAYLIST"));
        File.WriteAllText(Path.Combine(bdmv,"index.bdmv"),"INDX0200"+new string('0',512));
        File.WriteAllText(Path.Combine(bdmv,"PLAYLIST","00001.mpls"),"MPLS");
        return bdmv;
    }
    static async Task CheckPlaylist(string side,string[] sources,bool russian,bool validIso=false) {
        Drop(side,sources);
        int pid=side=="A"?shell.PidA:shell.PidB;
        IntPtr dialog=IntPtr.Zero;
        await Wait(()=>(dialog=EngineDialog(pid,2201))!=IntPtr.Zero,"No playlist picker on "+side);
        try {
            Assert(Caption(dialog)==(russian?"Выбор плейлиста Blu-ray":"Choose Blu-ray playlist"),"Playlist dialog language");
            Assert(Caption(GetDlgItem(dialog,1))==(russian?"Открыть":"Open"),"Playlist button language");
            if(validIso)Assert(Caption(GetDlgItem(dialog,2202)).Contains("00001.mpls"),"Missing ISO playlist");
            else Assert(Caption(GetDlgItem(dialog,2202)).Contains(russian?"Недопустимый размер MPLS":"invalid MPLS size"),"Playlist diagnostic language");
        } finally {
            PostMessageW(dialog,0x111,new IntPtr(2),IntPtr.Zero);
            await Wait(()=>EngineDialog(pid,2201)==IntPtr.Zero,"Playlist picker did not close");
            // The host may report the cancelled open in its own modal dialog.
            for(int i=0;i<20;i++) {
                var notice=EngineDialog(pid,2);
                if(notice!=IntPtr.Zero)PostMessageW(notice,0x10,IntPtr.Zero,IntPtr.Zero);
                await Task.Delay(50);
            }
        }
    }
    static async Task SelectTestPlaylist(IntPtr dialog,string name) {
        // The generated disc has two equal-length titles followed by a short one.
        var list=GetDlgItem(dialog,2201);
        // Give the shell view keyboard focus before its incremental filename search.
        var firstRow=new IntPtr((36<<16)|42);
        PostMessageW(list,0x201,new IntPtr(1),firstRow);
        PostMessageW(list,0x202,IntPtr.Zero,firstRow);
        PostMessageW(list,0x100,new IntPtr(0x24),IntPtr.Zero); // Home
        int row=Int32.Parse(Path.GetFileNameWithoutExtension(name),CultureInfo.InvariantCulture)-1;
        for(int i=0;i<row;i++)PostMessageW(list,0x100,new IntPtr(0x28),IntPtr.Zero); // Down
        await Wait(()=>Caption(GetDlgItem(dialog,2202)).Split('\r','\n')[0].EndsWith("\\"+name,StringComparison.OrdinalIgnoreCase),"Select "+name);
    }
    static void CheckHeader(string side,string source,string selected) {
        var title=FindControl<TextBlock>(window,"MediaTitle"+side);
        var playlist=FindControl<TextBlock>(window,"PlaylistName"+side);
        Assert(title.Text==source,"Wrong source title on "+side+": "+title.Text);
        Assert(playlist.IsVisible==(selected!=null),"Stale or missing playlist subtitle on "+side);
        if(selected==null)return;
        Assert(playlist.Text==Path.GetFileName(selected)&&String.Equals((string)playlist.ToolTip,selected,StringComparison.OrdinalIgnoreCase),"Wrong playlist subtitle on "+side);
        Assert(String.Equals((string)title.ToolTip,selected,StringComparison.OrdinalIgnoreCase),"Full source path missing from tooltip");
    }
    static async Task SwitchPlaylist(string side,string expected,string next,bool accept) {
        var session=shell.Controller.Session;
        var before=side=="A"?session.HostA:session.HostB;
        var other=side=="A"?session.HostB:session.HostA;
        string otherSide=side=="A"?"B":"A";
        string source=FindControl<TextBlock>(window,"MediaTitle"+side).Text;
        string otherSource=FindControl<TextBlock>(window,"MediaTitle"+otherSide).Text;
        var saved=session.State.Copy();
        var history=session.History.Count;
        var points=session.Points.Count;
        Click("ChangePlaylist"+side);
        IntPtr dialog=IntPtr.Zero;
        await Wait(()=>(dialog=EngineDialog(before.Pid,2201))!=IntPtr.Zero,"Switch picker on "+side);
        string selected=Caption(GetDlgItem(dialog,2202)).Split('\r','\n')[0];
        Assert(String.Equals(selected,before.Video.ResolvedFile,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(selected)==expected,"Current playlist was not preselected");
        await SelectTestPlaylist(dialog,next);
        PostMessageW(dialog,0x111,new IntPtr(accept?1:2),IntPtr.Zero);
        await Wait(()=>!shell.Controller.Working&&EngineDialog(before.Pid,2201)==IntPtr.Zero,"Switch did not finish");
        var after=side=="A"?session.HostA:session.HostB;
        var otherAfter=side=="A"?session.HostB:session.HostA;
        Assert(other.Fingerprint==otherAfter.Fingerprint&&other.Frame==otherAfter.Frame,"Switch affected opposite pane");
        bool changed=accept&&expected!=next;
        Assert(Path.GetFileName(after.Video.ResolvedFile)==(changed?next:expected),"Wrong playlist after "+side+" "+expected+" -> "+next+" accept="+accept+": "+after.Video.ResolvedFile+"; "+session.Message+"; busy="+after.Busy);
        CheckHeader(side,source,after.Video.ResolvedFile);
        CheckHeader(otherSide,otherSource,other.PlaylistPath);
        Assert(after.Frame==Math.Min(before.Frame.Value,after.Count-1),"Unexpected position after switch");
        if(changed)Assert(!session.State.Linked&&session.History.Count==0,"Stale navigation after switch");
        else Assert(before.Fingerprint==after.Fingerprint&&saved.Same(session.State)&&history==session.History.Count&&points==session.Points.Count,"Cancel/current changed the session");
    }
    static async Task CheckPlayableDisc(string side,string path,string isoPlaylist=null,Func<Task> open=null) {
        var session=shell.Controller.Session;
        string other=side=="A"?"B":"A";
        var otherBefore=side=="A"?session.HostB:session.HostA;
        int pid=side=="A"?shell.PidA:shell.PidB;
        if(open!=null)await open();
        else Drop(side,new[]{path});
        IntPtr dialog=IntPtr.Zero;
        await Wait(()=>(dialog=EngineDialog(pid,2201))!=IntPtr.Zero,"No disc picker on "+side);
        if(isoPlaylist!=null) {
            await SelectTestPlaylist(dialog,isoPlaylist);
            string expected=Path.Combine(path,"BDMV","PLAYLIST",isoPlaylist);
            await Wait(()=>Caption(GetDlgItem(dialog,2202)).StartsWith(expected+"\r\n",StringComparison.OrdinalIgnoreCase),"ISO playlist selection");
        }
        string selected=Caption(GetDlgItem(dialog,2202)).Split('\r','\n')[0];
        Assert((isoPlaylist!=null||File.Exists(selected))&&String.Equals(Path.GetExtension(selected),".mpls",StringComparison.OrdinalIgnoreCase),"Expected a selected playlist");
        PostMessageW(dialog,0x111,new IntPtr(1),IntPtr.Zero);
        await Wait(()=>{
            var host=side=="A"?session.HostA:session.HostB;
            return host!=null&&!host.Busy&&host.HasMedia&&host.Count>1&&host.Video!=null&&
                String.Equals(host.Video.ResolvedFile,selected,StringComparison.OrdinalIgnoreCase);
        },"Selected playlist did not open in "+side);
        string sourceName;
        if(isoPlaylist!=null)sourceName=Path.GetFileName(path);
        else {
            var root=new DirectoryInfo(path);
            if(root.Name.Equals("BDMV",StringComparison.OrdinalIgnoreCase))root=root.Parent;
            sourceName=root.Name;
            if(root.Parent==null&&!root.FullName.StartsWith(@"\\",StringComparison.Ordinal)) {
                string label=new DriveInfo(root.FullName).VolumeLabel;
                sourceName=String.IsNullOrWhiteSpace(label)?root.FullName:label;
            }
        }
        CheckHeader(side,sourceName,selected);
        if(isoPlaylist!=null) {
            var host=side=="A"?session.HostA:session.HostB;
            Assert(Media(host,path)&&host.Count==48,"Unexpected ISO source or frame count");
            foreach(int frame in new[]{23,24,47,0}) {
                await shell.Controller.EnterField("Frame"+side,frame.ToString());
                Assert((side=="A"?session.HostA:session.HostB).Frame==frame,"ISO seek to "+frame);
            }
        }
        await shell.Controller.EnterField("Frame"+side,"1");
        var loaded=side=="A"?session.HostA:session.HostB;
        var otherAfter=side=="A"?session.HostB:session.HostA;
        Assert(loaded.Frame==1,"Selected playlist did not seek to frame 1");
        Assert(otherBefore.File==otherAfter.File&&otherBefore.Frame==otherAfter.Frame,"Unexpected change in "+other);
        Pass((isoPlaylist??"disc playlist")+" opens and seeks in "+side+"; other pane preserved");
    }
    static async Task Run(string first,string second,bool restart,string disc,string iso) {
        await shell.Startup;
        await Expect(null,null);
        string bdmv=MakeTestDisc();
        if(restart) {
            Assert(window.Title.StartsWith("SyncSyncAV")&&(string)Button("EmptyOpenA").Content=="Open video A…","Saved language ignored on relaunch");
            Assert(shell.Controller.BigStep==24,"Existing settings lost on relaunch");
            Pass("English survives a restart with Russian Windows locale");
            shell.Controller.OpenConnections();
            var settings=window.OwnedWindows.Cast<Window>().Single();
            settings.UpdateLayout();
            FindControl<ComboBox>(settings,"Language").SelectedIndex=0;
            settings.Close();
            await CheckPlaylist("A",new[]{bdmv.ToLowerInvariant()+Path.DirectorySeparatorChar},true);
            Pass("BDMV folder with a trailing separator opens Russian chooser on A");
            await ExplorerOpen("B","EmptyOpen",null,true);
            Pass("Russian Explorer navigation on empty B");
            await CheckPlaylist("B",new[]{iso},true,true);
            await Expect(null,null);
            Pass("cancel ISO playlist selection on B in Russian");
            return;
        }
        Assert(window.Title.StartsWith("SyncSyncAV")&&shell.Controller.BigStep==24,"Legacy preferences or system language not loaded");
        Assert(!Button("OpenA").IsVisible&&Button("EmptyOpenA").IsVisible,"Duplicate empty opener");
        Assert(!Button("ChangePlaylistA").IsVisible&&!Button("ChangePlaylistB").IsVisible,"Playlist buttons on empty panes");
        var reset=Button("ResetOffset");var copy=Button("CopyOffset");
        var distance=copy.TranslatePoint(new Point(),reset).X-reset.ActualWidth;
        Assert(distance>=16&&reset.ActualHeight>=24&&copy.ActualHeight>=24,"Offset button hit areas overlap or are too small");
        await ExplorerOpen("A","EmptyOpen",first);
        await Expect(first,null);
        await ExplorerOpen("A","Open",null);
        await Expect(first,null);
        await ExplorerOpen("A","Open",second);
        await Expect(second,null);
        Click("RemoveA");await Expect(null,null);
        Pass("Explorer navigation, Unicode file open, cancel and replace on A");
        Drop("B",new[]{first,second});await Expect(second,first);
        Assert(Button("OpenA").IsVisible&&(string)Button("OpenA").Content=="Replace…","Missing replace action");
        Assert(!Button("ChangePlaylistA").IsVisible&&!Button("ChangePlaylistB").IsVisible,"Playlist buttons on regular files");
        CheckHeader("A",Path.GetFileName(second),null);
        CheckHeader("B",Path.GetFileName(first),null);
        Pass("two files on empty B open in B then A");
        await shell.Controller.EnterField("FrameB","18");
        Click("Anchor");await shell.Controller.LastAction;
        Assert(shell.Controller.Session.State.Linked,"Anchor failed");
        var session=shell.Controller.Session;
        var saved=session.State.Copy();
        int pidA=shell.PidA,pidB=shell.PidB,points=session.Points.Count;
        await SwitchLanguage(true);
        await SwitchLanguage(false);
        Assert(shell.PidA==pidA&&shell.PidB==pidB&&session.State.Same(saved)&&session.Points.Count==points,"Language switch changed video state");
        Pass("switch both languages with two open videos, preserve frames, link, offset, sync points and template draft");
        Drop("A",new[]{first});await Expect(first,first);
        Assert(!shell.Controller.Session.State.Linked&&shell.Controller.Session.HostB.Frame==18,"Other frame changed on replacement");
        Pass("replace over native video, unlink, keep other frame");
        Drop("B",new[]{Path.Combine(folder,"ignored.txt")},accepted:false);
        Drop("B",new[]{first,second,first},accepted:false);await Expect(first,first);
        Pass("non-video and three-file groups rejected");
        Click("RemoveA");await Expect(null,first);
        Assert(File.Exists(first)&&Button("EmptyOpenA").IsVisible&&shell.Controller.Session.HostB.Frame==18,"Remove side effects");
        Pass("remove A keeps file and B position, restores empty view");
        Drop("A",new[]{second},header:true);await Expect(second,first);
        Assert(shell.Controller.Session.Points.Any(p=>p.B==18),"Saved point did not return with the original pair");
        Click("Swap");await shell.Controller.LastAction;await Expect(first,second);
        Click("RemoveB");await Expect(first,null);
        Click("RemoveA");await Expect(null,null);
        Pass("header accepts a file, removal follows swapped panes");
        Drop("A",new[]{first});await Expect(first,null);
        Assert(File.Exists(second),"Source file disappeared");
        Pass("reopen the same file after clearing both sides");
        var incomplete=Directory.CreateDirectory(Path.Combine(folder,"Incomplete","BDMV"));
        File.WriteAllText(Path.Combine(incomplete.FullName,"index.bdmv"),"INDX");
        Drop("A",new[]{second,incomplete.FullName},accepted:false);
        Drop("B",new[]{Path.Combine(bdmv,"PLAYLIST")},accepted:false);
        await Expect(first,null);
        Pass("unrelated or incomplete disc folder rejects the entire drop");
        await CheckPlaylist("B",new[]{Path.Combine(bdmv,"index.bdmv")},false);
        Pass("direct index.bdmv opens the English chooser on B");
        await CheckPlaylist("B",new[]{Path.GetDirectoryName(bdmv),second},false);
        await Expect(second,null);
        Pass("disc root opens chooser on B and companion video opens on A");
        await CheckPlayableDisc("A",iso,"00001.mpls");
        await CheckPlayableDisc("B",iso,"00002.mpls",()=>ExplorerOpen("B","EmptyOpen",iso));
        double width=window.Width;
        window.Width=window.MinWidth;
        await SwitchLanguage(true);
        window.UpdateLayout();
        foreach(string side in new[]{"A","B"}) {
            var title=FindControl<TextBlock>(window,"MediaTitle"+side);
            var subtitle=FindControl<TextBlock>(window,"PlaylistName"+side);
            var row=new TextBlock{Text=subtitle.Text,FontSize=subtitle.FontSize,FontFamily=subtitle.FontFamily};
            row.Measure(new Size(Double.PositiveInfinity,Double.PositiveInfinity));
            Assert(subtitle.ActualWidth>=row.DesiredSize.Width&&subtitle.ActualHeight+1>=row.DesiredSize.Height,"Playlist clipped at minimum width on "+side+": allocated "+subtitle.RenderSize+" / text "+row.DesiredSize);
            var header=(FrameworkElement)Button("Open"+side).Parent;
            var top=title.TranslatePoint(new Point(),header).Y;
            var bottom=subtitle.TranslatePoint(new Point(),header).Y;
            Assert(top>=-1&&top+title.ActualHeight<=bottom+1&&bottom+subtitle.ActualHeight<=header.ActualHeight+1,"Two-line header exceeds available height");
        }
        await SwitchLanguage(false);
        window.Width=width;window.UpdateLayout();
        Pass("both headers show the ISO and the full active playlist at minimum window width");
        await shell.Controller.EnterField("FrameA","32");
        await shell.Controller.EnterField("FrameB","40");
        Click("Anchor");await shell.Controller.LastAction;
        string pair=session.PairKey;
        await SwitchPlaylist("B","00002.mpls","00001.mpls",false);
        await SwitchPlaylist("B","00002.mpls","00002.mpls",true);
        await SwitchPlaylist("B","00002.mpls","00001.mpls",true);
        Assert(pair!=session.PairKey,"Equal-length playlists shared sync points");
        await SwitchPlaylist("B","00001.mpls","00002.mpls",true);
        Assert(session.PairKey==pair&&session.Points.Count>0,"Switch back did not restore saved ISO points");
        await SwitchPlaylist("A","00001.mpls","00003.mpls",true);
        Assert(session.HostA.Frame==23,"Shorter ISO playlist did not clamp to its last frame");
        Pass("ISO switch preselects current, cancel/current keep anchor, shorter title clamps and switching back restores points");

        var discFolder=Path.Combine(folder,"sample-disc","BDMV");
        await CheckPlayableDisc("A",discFolder);
        Click("Swap");await shell.Controller.LastAction;
        await Expect(iso,Path.Combine(discFolder,"index.bdmv"));
        CheckHeader("A",Path.GetFileName(iso),session.HostA.Video.ResolvedFile);
        CheckHeader("B","sample-disc",session.HostB.Video.ResolvedFile);
        string folderPair=session.PairKey;
        await SwitchPlaylist("B","00001.mpls","00002.mpls",true);
        Assert(session.PairKey!=folderPair,"Folder sync points mixed equal-length titles");
        await SwitchPlaylist("B","00002.mpls","00001.mpls",true);
        Assert(session.PairKey==folderPair,"Folder playlist identity changed after switching back");
        Pass("BDMV and direct MPLS switch on swapped pane");
        var playlists=Path.Combine(discFolder,"PLAYLIST");
        var invalid=File.ReadAllBytes(Path.Combine(playlists,"00003.mpls"));
        int clip=Encoding.ASCII.GetString(invalid).IndexOf("00001M2TS",StringComparison.Ordinal);
        Assert(clip>=0,"Missing fixture clip");
        Encoding.ASCII.GetBytes("99999").CopyTo(invalid,clip);
        File.WriteAllBytes(Path.Combine(playlists,"00004.mpls"),invalid);
        var playing=session.HostB;
        Click("ChangePlaylistB");
        IntPtr dialog=IntPtr.Zero;
        await Wait(()=>(dialog=EngineDialog(playing.Pid,2201))!=IntPtr.Zero,"Open picker for unavailable clip");
        await SelectTestPlaylist(dialog,"00004.mpls");
        PostMessageW(dialog,0x111,new IntPtr(1),IntPtr.Zero);
        IntPtr notice=IntPtr.Zero;
        await Wait(()=>{
            if(EngineDialog(playing.Pid,2201)!=IntPtr.Zero)return false;
            notice=EngineDialog(playing.Pid,2);
            if(notice==IntPtr.Zero)notice=EngineDialog(playing.Pid,1);
            return notice!=IntPtr.Zero;
        },"Unavailable clip error");
        PostMessageW(notice,0x10,IntPtr.Zero,IntPtr.Zero);
        await Wait(()=>!shell.Controller.Working,"Dismiss unavailable clip error");
        Assert(session.HostB.Fingerprint==playing.Fingerprint&&session.HostB.Frame==playing.Frame,"Failed switch lost current input");
        await shell.Controller.EnterField("FrameB","24");
        Assert(session.HostB.Frame==24,"Original title no longer seekable");
        Pass("unavailable replacement clip preserves playable source");
        Click("Swap");await shell.Controller.LastAction;
        Click("RemoveA");await Expect(null,iso);
        Click("RemoveB");await Expect(null,null);
        CheckHeader("A","",null);CheckHeader("B","",null);
        using(File.Open(iso,FileMode.Open,FileAccess.Read,FileShare.None)) {}
        Pass("same ISO in both panes, alternate playlists, clip-boundary seeks, separate sync points and file released");
        await CheckExplorerFolders(first);
        if(disc!=null) {
            await CheckPlayableDisc("A",disc,open:()=>ExplorerOpen("A","Open",disc));
            string discBdmv=Path.Combine(disc,"BDMV");
            await CheckPlayableDisc("B",discBdmv,open:()=>ExplorerOpen("B","Open",discBdmv));
        }
    }
    [STAThread] public static int Main(string[] args) {
        folder=Path.GetDirectoryName(args[1]);
        bool restart=Array.IndexOf(args,"--restart")>=0;
        Thread.CurrentThread.CurrentUICulture=CultureInfo.GetCultureInfo(restart?"ru-RU":"en-US");
        if(!restart)File.WriteAllText(Path.Combine(folder,"settings.json"),"{\"Version\":1,\"BigStep\":24}");
        else passed.AddRange(new JavaScriptSerializer().Deserialize<List<string>>(File.ReadAllText(Path.Combine(folder,"report.json"))));
        var app=new Application();
        using(var stream=typeof(Program).Assembly.GetManifestResourceStream("MainWindow.xaml"))window=(Window)XamlReader.Load(stream);
        shell=new IntegratedShell(window,args[0],Path.Combine(folder,"settings.json"),Path.Combine(folder,"session.jsonl"));
        window.Loaded+=async(s,e)=>{
            try {await Run(args[1],args[2],restart,Program.Option(args,"--disc"),Program.Option(args,"--iso"));}
            catch(Exception error){
                File.WriteAllText(Path.Combine(folder,"failure.txt"),error.ToString());
                Environment.ExitCode=1;
                EnumWindows((h,l)=>{
                    uint pid;GetWindowThreadProcessId(h,out pid);
                    if((pid==shell.PidA||pid==shell.PidB)&&WindowClass(h)=="#32770")
                        PostMessageW(h,0x111,new IntPtr(2),IntPtr.Zero);
                    return true;
                },IntPtr.Zero);
            }
            finally {window.Close();}
        };
        app.Run(window);return Environment.ExitCode;
    }
}
