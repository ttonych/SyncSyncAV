// SPDX-License-Identifier: MIT
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Shell;
using ShapePath=System.Windows.Shapes.Path;

namespace SyncSyncAV {
    public sealed class IntegratedShell {
        public readonly Window Window;
        public readonly AppController Controller;
        private readonly EngineLifetime engines=new EngineLifetime();
        private readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        private readonly Grid previews=new Grid();
        private readonly string executable;
        private EmbeddedView a=new EmbeddedView(),b=new EmbeddedView();
        private readonly Button openA=new Button(),openB=new Button();
        private readonly Button playlistA=new Button(),playlistB=new Button();
        private readonly Button removeA=new Button(),removeB=new Button();
        private static readonly HashSet<string> videoExtensions=new HashSet<string>(
            ".avi .mp4 .m4v .mov .mkv .webm .flv .mpeg .mpg .m2v .ts .mts .m2ts .m2t .vob .wmv .asf .mxf .nut .y4m .mpls .bdmv .iso .ogv .3gp .3g2 .divx .f4v .h264 .264 .h265 .265 .hevc .av1 .ivf .vro .evo .wtv .rm .rmvb .dv".Split(' '),StringComparer.OrdinalIgnoreCase);
        private readonly TextBlock playlistNameA=new TextBlock(),playlistNameB=new TextBlock();
        private readonly TextBlock titleA=new TextBlock(),titleB=new TextBlock();
        private readonly Border emptyA=new Border(),emptyB=new Border();
        private readonly Button emptyOpenA=new Button(),emptyOpenB=new Button();
        private readonly TextBlock emptyTitleA=new TextBlock(),emptyTitleB=new TextBlock();
        private string openingSide,dropNameA,dropNameB;
        private string[] dragSources;
        private bool dragAccepted;
        private bool closing,closed,starting=true;
        public Task Startup {get;private set;}
        public int PidA {get{return a.Pid;}}
        public int PidB {get{return b.Pid;}}
        [StructLayout(LayoutKind.Sequential)] private struct PointI {public int X,Y;}
        [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo {public PointI Reserved,MaxSize,MaxPosition,MinTrack,MaxTrack;}
        [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo {public int Size;public Desktop.Rect Monitor,Work;public uint Flags;}
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd,uint flags);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr monitor,ref MonitorInfo info);

        public IntegratedShell(Window window,string executable,string data,string log) {
            Window=window;this.executable=executable;
            // Register before AppController so its timer survives a cancelled close.
            Window.Closing+=OnClosing;
            Controller=new AppController(window,data,log,true);
            WindowChrome.SetWindowChrome(Window,new WindowChrome{CaptionHeight=36,ResizeBorderThickness=new Thickness(5),GlassFrameThickness=new Thickness(0),CornerRadius=new CornerRadius(0),UseAeroCaptionButtons=false});
            Window.WindowStyle=WindowStyle.None;
            Window.SourceInitialized+=(s,e)=>((HwndSource)PresentationSource.FromVisual(Window)).AddHook(WindowBounds);
            Window.SetResourceReference(Window.TitleProperty,"L.WindowTitle");
            Window.Height=900;Window.MinHeight=620;Window.Width=1600;
            Window.Topmost=false;Window.WindowStartupLocation=WindowStartupLocation.CenterScreen;
            foreach(string name in new[]{"Tile","RestoreLayout","CloseButton","Minimize"})Controller.B(name).Visibility=Visibility.Collapsed;
            var controls=(UIElement)Window.Content;Window.Content=null;
            var root=new Grid();root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(36)});
            root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
            root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(188)});
            root.Children.Add(CreateTitleBar());
            Grid.SetRow(controls,2);root.Children.Add(controls);
            Window.Content=new Border{BorderBrush=Paint("#343a41"),BorderThickness=new Thickness(1),Child=root};
            previews.Background=Paint("#171a1e");Grid.SetRow(previews,1);
            previews.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
            previews.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(6)});
            previews.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
            previews.RowDefinitions.Add(new RowDefinition{Height=new GridLength(46)});
            previews.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
            root.Children.Add(previews);
            previews.AllowDrop=true;
            previews.PreviewDragEnter+=FileDrag;previews.PreviewDragOver+=FileDrag;
            previews.PreviewDragLeave+=(s,e)=>ClearDropPreview();
            previews.PreviewDrop+=FileDrop;
            AddHeader("A",0,openA,titleA,playlistNameA);AddHeader("B",2,openB,titleB,playlistNameB);
            Grid.SetRow(a,1);Grid.SetColumn(a,0);Grid.SetRow(b,1);Grid.SetColumn(b,2);
            previews.Children.Add(a);previews.Children.Add(b);
            AddEmpty("A",0,emptyA,emptyOpenA,emptyTitleA);AddEmpty("B",2,emptyB,emptyOpenB,emptyTitleB);
            a.EngineExited+=EngineExited;b.EngineExited+=EngineExited;
            Controller.Rendered+=RenderHeaders;
            Controller.LanguageChanged+=()=>{a.ApplyLanguage();b.ApplyLanguage();};
            Controller.SwapEmbedded=async()=>{
                await Controller.Run(Ui.Text("Swapping"),async()=>{
                    await Controller.Session.SwapAsync(lifetime.Token);
                    var old=a;a=b;b=old;Grid.SetColumn(a,0);Grid.SetColumn(b,2);
                    a.Fit();b.Fit();RenderHeaders();
                });
            };
            Window.Loaded+=(s,e)=>{Startup=StartAsync();};
            Window.Closed+=(s,e)=>{closed=true;lifetime.Cancel();engines.Dispose();};
        }
        private static Brush Paint(string color){return (Brush)new BrushConverter().ConvertFromString(color);}
        private IntPtr WindowBounds(IntPtr hwnd,int message,IntPtr wp,IntPtr lp,ref bool handled) {
            if(message==0x24) {
                var monitor=new MonitorInfo{Size=Marshal.SizeOf(typeof(MonitorInfo))};
                if(GetMonitorInfoW(MonitorFromWindow(hwnd,2),ref monitor)) {
                    var bounds=(MinMaxInfo)Marshal.PtrToStructure(lp,typeof(MinMaxInfo));
                    bounds.MaxPosition.X=monitor.Work.Left-monitor.Monitor.Left;bounds.MaxPosition.Y=monitor.Work.Top-monitor.Monitor.Top;
                    bounds.MaxSize.X=monitor.Work.Right-monitor.Work.Left;bounds.MaxSize.Y=monitor.Work.Bottom-monitor.Work.Top;
                    Marshal.StructureToPtr(bounds,lp,false);handled=true;
                }
            }
            return IntPtr.Zero;
        }
        private Border CreateTitleBar() {
            var bar=new Border{Background=Paint("#121518"),BorderBrush=Paint("#262b31"),BorderThickness=new Thickness(0,0,0,1)};
            var row=new DockPanel{Margin=new Thickness(12,0,4,0)};bar.Child=row;
            var actions=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
            WindowChrome.SetIsHitTestVisibleInChrome(actions,true);DockPanel.SetDock(actions,Dock.Right);row.Children.Add(actions);
            actions.Children.Add(ChromeButton("WindowMinimize","Minimize","M2,8 L12,8",()=>SystemCommands.MinimizeWindow(Window)));
            var maximize=ChromeButton("WindowMaximize","Maximize","M2,2 L12,2 12,12 2,12 Z",()=>{
                if(Window.WindowState==WindowState.Maximized)SystemCommands.RestoreWindow(Window);else SystemCommands.MaximizeWindow(Window);
            });
            actions.Children.Add(maximize);
            Window.StateChanged+=(s,e)=>{
                bool full=Window.WindowState==WindowState.Maximized;
                ((ShapePath)maximize.Content).Data=Geometry.Parse(full?"M5,2 L12,2 12,9 M2,5 L9,5 9,12 2,12 Z":"M2,2 L12,2 12,12 2,12 Z");
                SetChromeLabel(maximize,full?"RestoreSize":"Maximize");
            };
            actions.Children.Add(ChromeButton("WindowClose","CloseApp","M3,3 L11,11 M11,3 L3,11",()=>Window.Close()));
            var brand=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
            brand.Children.Add(new Image{Width=20,Height=20,Source=AppIcon.ForSize(20),SnapsToDevicePixels=true});
            var name=new TextBlock{Margin=new Thickness(8,0,0,0),FontSize=12,FontWeight=FontWeights.SemiBold,Foreground=Paint("#a8b0b8"),VerticalAlignment=VerticalAlignment.Center};
            name.SetResourceReference(TextBlock.TextProperty,"L.AppName");brand.Children.Add(name);
            row.Children.Add(brand);return bar;
        }
        private static void SetChromeLabel(Button button,string key) {
            button.SetResourceReference(FrameworkElement.ToolTipProperty,"L."+key);
            button.SetResourceReference(AutomationProperties.NameProperty,"L."+key);
        }
        private Button ChromeButton(string id,string tooltip,string geometry,Action action) {
            var button=new Button{Style=(Style)Window.FindResource("ToolButton"),Width=42,Height=28,Padding=new Thickness(0)};
            var icon=new ShapePath{Width=14,Height=14,StrokeThickness=1.15,Data=Geometry.Parse(geometry),Stretch=Stretch.None};
            icon.SetBinding(ShapePath.StrokeProperty,new System.Windows.Data.Binding("Foreground"){Source=button});
            button.Content=icon;AutomationProperties.SetAutomationId(button,id);SetChromeLabel(button,tooltip);
            button.Click+=(s,e)=>action();return button;
        }
        private void AddHeader(string side,int column,Button button,TextBlock title,TextBlock playlistName) {
            var frame=new Border{Background=Paint("#171a1e"),BorderBrush=Paint("#343a41"),BorderThickness=new Thickness(0,0,0,1)};
            var panel=new DockPanel{Margin=new Thickness(20,5,20,5)};frame.Child=panel;
            Grid.SetColumn(frame,column);previews.Children.Add(frame);
            var remove=side=="A"?removeA:removeB;
            remove.SetResourceReference(ContentControl.ContentProperty,"L.Remove");remove.Height=30;remove.FontSize=12;remove.MinWidth=70;remove.Margin=new Thickness(6,0,0,0);
            remove.Visibility=Visibility.Collapsed;remove.SetResourceReference(FrameworkElement.ToolTipProperty,"L.CloseVideo"+side);
            AutomationProperties.SetAutomationId(remove,"Remove"+side);remove.SetResourceReference(AutomationProperties.NameProperty,"L.RemoveVideo"+side);
            DockPanel.SetDock(remove,Dock.Right);panel.Children.Add(remove);
            remove.Click+=async(s,e)=>await RemoveSide(side);
            button.Content=Ui.Text("OpenVideoPrefix")+side+"…";button.Height=30;button.MinWidth=142;button.FontSize=12;button.IsEnabled=false;
            AutomationProperties.SetAutomationId(button,"Open"+side);DockPanel.SetDock(button,Dock.Right);panel.Children.Add(button);
            button.Click+=async(s,e)=>await OpenSide(side);
            var playlist=side=="A"?playlistA:playlistB;
            playlist.Height=30;playlist.FontSize=12;playlist.Margin=new Thickness(0,0,6,0);playlist.Visibility=Visibility.Collapsed;
            playlist.SetResourceReference(ContentControl.ContentProperty,"L.ChangePlaylist");
            playlist.SetResourceReference(AutomationProperties.NameProperty,"L.ChangePlaylist");
            AutomationProperties.SetAutomationId(playlist,"ChangePlaylist"+side);
            DockPanel.SetDock(playlist,Dock.Right);panel.Children.Add(playlist);
            playlist.Click+=async(s,e)=>await ChangePlaylist(side);
            var badge=new Border{Width=22,Height=22,CornerRadius=new CornerRadius(4),Background=Paint("#e3e6e9"),Margin=new Thickness(0,0,9,0),VerticalAlignment=VerticalAlignment.Center};
            badge.Child=new TextBlock{Text=side,Foreground=Paint("#171a1e"),FontFamily=(FontFamily)Window.FindResource("NumberFont"),FontWeight=FontWeights.Bold,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
            DockPanel.SetDock(badge,Dock.Left);panel.Children.Add(badge);
            var labels=new StackPanel{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,10,0)};
            panel.Children.Add(labels);
            title.Text=Ui.Text("StartingPreview");title.TextTrimming=TextTrimming.CharacterEllipsis;
            AutomationProperties.SetAutomationId(title,"MediaTitle"+side);
            title.FontSize=12;title.FontWeight=FontWeights.SemiBold;title.Foreground=Paint("#e3e6e9");labels.Children.Add(title);
            playlistName.FontFamily=(FontFamily)Window.FindResource("NumberFont");playlistName.FontSize=11;
            playlistName.Foreground=Paint("#a8b0b8");playlistName.TextTrimming=TextTrimming.CharacterEllipsis;
            playlistName.Visibility=Visibility.Collapsed;AutomationProperties.SetAutomationId(playlistName,"PlaylistName"+side);
            labels.Children.Add(playlistName);
        }
        private void AddEmpty(string side,int column,Border frame,Button button,TextBlock heading) {
            frame.Background=Paint("#0f1215");frame.Visibility=Visibility.Collapsed;Grid.SetRow(frame,1);Grid.SetColumn(frame,column);previews.Children.Add(frame);
            var content=new StackPanel{HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};frame.Child=content;
            var badge=new Border{Width=38,Height=38,CornerRadius=new CornerRadius(8),BorderBrush=Paint("#343a41"),BorderThickness=new Thickness(1),Background=Paint("#171a1e"),HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,0,0,16)};
            badge.Child=new TextBlock{Text=side,FontFamily=(FontFamily)Window.FindResource("NumberFont"),FontSize=20,Foreground=Paint("#a8b0b8"),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};content.Children.Add(badge);
            heading.Text=Ui.Text("VideoPrefix")+side+Ui.Text("NotOpenSuffix");heading.FontSize=18;heading.FontWeight=FontWeights.SemiBold;heading.Foreground=Paint("#e3e6e9");heading.HorizontalAlignment=HorizontalAlignment.Center;content.Children.Add(heading);
            button.Content=Ui.Text("OpenVideoPrefix")+side+"…";button.Width=190;button.Height=36;button.Margin=new Thickness(0,18,0,12);button.IsEnabled=false;AutomationProperties.SetAutomationId(button,"EmptyOpen"+side);button.Click+=async(s,e)=>await OpenSide(side);content.Children.Add(button);
            var hint=new TextBlock{FontSize=12,Foreground=Paint("#a8b0b8"),HorizontalAlignment=HorizontalAlignment.Center};
            hint.SetResourceReference(TextBlock.TextProperty,"L.DropVideoHere");content.Children.Add(hint);
        }
        private bool Available(string side) {
            var view=side=="A"?a:b;
            var state=side=="A"?Controller.Session.HostA:Controller.Session.HostB;
            return view.Running&&state!=null&&!state.Busy;
        }
        private static string DropName(string path) {
            var leaf=Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar));
            return String.IsNullOrEmpty(leaf)?path:leaf;
        }
        private static string ResolveDropPath(string path) {
            if(File.Exists(path))return videoExtensions.Contains(Path.GetExtension(path))?path:null;
            if(!Directory.Exists(path))return null;
            if(String.Equals(DropName(path),"BDMV",StringComparison.OrdinalIgnoreCase)) {
                var direct=DiscIndex(path);
                if(direct!=null)return direct;
            }
            return DiscIndex(Path.Combine(path,"BDMV"));
        }
        private static string DiscIndex(string bdmv) {
            var index=Path.Combine(bdmv,"index.bdmv");
            return File.Exists(index)&&Directory.Exists(Path.Combine(bdmv,"PLAYLIST"))?index:null;
        }
        private static string[] ResolveDropFiles(string[] sources) {
            var files=new string[sources.Length];
            for(int i=0;i<sources.Length;i++) {
                files[i]=ResolveDropPath(sources[i]);
                if(files[i]==null)return null;
            }
            return files;
        }
        private void ClearDropPreview() {
            dragSources=null;dragAccepted=false;ShowDropNames(null,null);
        }
        private string DropSide(DragEventArgs e,out string[] files) {
            files=e.Data.GetData(DataFormats.FileDrop,false) as string[];
            if(files==null||files.Length<1||files.Length>2||starting||closing||openingSide!=null||Controller.Working||
               (e.AllowedEffects&DragDropEffects.Copy)==0)return null;
            var point=e.GetPosition(previews);
            double left=previews.ColumnDefinitions[0].ActualWidth,right=left+previews.ColumnDefinitions[1].ActualWidth;
            if(point.X<0||point.X>=previews.ActualWidth||point.Y<0||point.Y>=previews.ActualHeight||(point.X>=left&&point.X<right))return null;
            string side=point.X<left?"A":"B";
            return Available(side)&&(files.Length==1||Available(side=="A"?"B":"A"))?side:null;
        }
        private void FileDrag(object sender,DragEventArgs e) {
            string[] sources;
            string side=DropSide(e,out sources);
            if(side!=null) {
                // Explorer sends repeated DragOver events; probe the disc once per set of paths.
                if(dragSources==null||!dragSources.SequenceEqual(sources,StringComparer.Ordinal)) {
                    dragSources=(string[])sources.Clone();
                    dragAccepted=ResolveDropFiles(sources)!=null;
                }
                if(!dragAccepted)side=null;
            }
            string first=side==null?null:DropName(sources[0]);
            string second=side!=null&&sources.Length==2?DropName(sources[1]):null;
            ShowDropNames(side=="A"?first:second,side=="B"?first:second);
            e.Effects=side==null?DragDropEffects.None:DragDropEffects.Copy;e.Handled=true;
        }
        private void ShowDropNames(string nameA,string nameB) {
            if(dropNameA==nameA&&dropNameB==nameB)return;
            dropNameA=nameA;dropNameB=nameB;RenderHeaders();
        }
        private async void FileDrop(object sender,DragEventArgs e) {
            ClearDropPreview();
            string[] sources;string side=DropSide(e,out sources);
            e.Handled=true;e.Effects=DragDropEffects.None;
            if(side==null)return;
            var files=ResolveDropFiles(sources);
            if(files==null){Controller.Session.Say(Ui.Text("DropExistingFile"),true);return;}
            e.Effects=DragDropEffects.Copy;
            string other=side=="A"?"B":"A";
            await Controller.Run(Ui.Text("OpeningVideo"),async()=>{
                if(!Available(side)||(files.Length==2&&!Available(other)))return;
                openingSide=files.Length==2?"AB":side;
                try {
                    RenderHeaders();
                    var first=side=="A"?a:b;first.Fit();await first.OpenDroppedFileAsync(files[0]);
                    Controller.Session.ResetSourceNavigation();
                    if(files.Length==2) {
                        var second=side=="A"?b:a;second.Fit();await second.OpenDroppedFileAsync(files[1]);
                    }
                    await Controller.Session.PollAsync(lifetime.Token);
                } finally {openingSide=null;RenderHeaders();}
            });
        }
        private Task ChangePlaylist(string side) {
            return Controller.Run(Ui.Text("ChoosingPlaylist"),async()=>{
                var view=side=="A"?a:b;
                var host=side=="A"?Controller.Session.HostA:Controller.Session.HostB;
                if(starting||closing||!Available(side)||host.PlaylistPath==null)return;
                bool changed=await view.ChangePlaylistAsync(lifetime.Token);
                await Controller.Session.PollAsync(lifetime.Token);
                if(!changed)Controller.Session.Say(Ui.Text("PlaylistKept"));
            });
        }
        private async Task RemoveSide(string side) {
            if(starting||closing||openingSide!=null||!Available(side))return;
            await Controller.Run(Ui.Text("ClosingVideoPrefix")+side+"…",async()=>{
                (side=="A"?a:b).CloseMedia();
                Controller.Session.ResetSourceNavigation();
                for(int attempt=0;attempt<10;attempt++) {
                    await Controller.Session.PollAsync(lifetime.Token);
                    var state=side=="A"?Controller.Session.HostA:Controller.Session.HostB;
                    if(state==null)break;
                    if(!state.Busy&&!state.HasMedia){Controller.Session.Say(Ui.Text("VideoPrefix")+side+Ui.Text("ClosedSuffix"));return;}
                    await Task.Delay(100,lifetime.Token);
                }
                Controller.Session.Say(Ui.Text("PreviewPrefix")+side+Ui.Text("CloseNotConfirmed"),true);
            });
        }
        private async Task OpenSide(string side) {
            if(closing||starting||openingSide!=null||Controller.Working)return;
            try {
                var view=side=="A"?a:b;
                if(!view.Running) {
                    starting=true;RenderHeaders();
                    await view.StartAsync(executable,engines,lifetime.Token);
                    await Controller.Run(Ui.Text("PreviewRestored"),()=>Controller.Session.SelectAsync(a.Pid,b.Pid,false,lifetime.Token));
                    starting=false;RenderHeaders();
                }
                openingSide=side;RenderHeaders();view.Fit();view.OpenDialog();
                await Task.Delay(400,lifetime.Token);
            }catch(OperationCanceledException){}
            catch(Exception error){Controller.Session.Say(error.Message,true,true);}
            finally{openingSide=null;starting=false;RenderHeaders();}
        }
        private async Task StartAsync() {
            await Controller.StartAsync(0,0,false);
            try {
                await a.StartAsync(executable,engines,lifetime.Token);
                await b.StartAsync(executable,engines,lifetime.Token);
                await Controller.Run(Ui.Text("PreviewsReady"),()=>Controller.Session.SelectAsync(a.Pid,b.Pid,false,lifetime.Token));
                Controller.Session.Say(Ui.Text("StartupHint"));
            } catch(OperationCanceledException){}
            catch(Exception error) {
                if(!closing&&!closed)await Controller.Run(Ui.Text("CheckingPreviews"),()=>Controller.Session.SelectAsync(a.Running?a.Pid:0,b.Running?b.Pid:0,false,lifetime.Token));
                Controller.Session.Say(Ui.Text("StartPreviewsFailed")+error.Message,true,true);
            }
            finally{starting=false;if(!closed)RenderHeaders();}
        }
        private void RenderHeaders() {
            if(closed)return;
            foreach(var pair in new[]{Tuple.Create(a,Controller.Session.HostA,openA,titleA,"A"),Tuple.Create(b,Controller.Session.HostB,openB,titleB,"B")}) {
                var view=pair.Item1;var state=pair.Item2;var button=pair.Item3;var title=pair.Item4;
                button.IsEnabled=!starting&&!closing&&openingSide==null&&!Controller.Working&&(!view.Running||state==null||!state.Busy);
                bool loaded=view.Running&&state!=null&&state.HasMedia;
                var dropName=pair.Item5=="A"?dropNameA:dropNameB;
                button.Visibility=loaded?Visibility.Visible:Visibility.Collapsed;
                button.Content=Ui.Text("Replace");button.ToolTip=Ui.Text("OpenInPane")+pair.Item5;
                string sourcePath=state==null?null:state.File;
                var playlist=pair.Item5=="A"?playlistA:playlistB;
                string activePlaylist=loaded?state.PlaylistPath:null;
                playlist.Visibility=activePlaylist!=null?Visibility.Visible:Visibility.Collapsed;
                playlist.IsEnabled=button.IsEnabled;
                var playlistName=pair.Item5=="A"?playlistNameA:playlistNameB;
                string selected=dropName==null?activePlaylist:null;
                playlistName.Text=Path.GetFileName(selected);
                playlistName.Visibility=selected==null?Visibility.Collapsed:Visibility.Visible;
                playlistName.ToolTip=selected;
                if(dropName!=null)title.Text=pair.Item5+" ← "+dropName;
                else if(selected!=null)title.Text=Controller.SourceTitle(state,pair.Item5);
                else title.Text=loaded?Path.GetFileName(sourcePath):"";
                title.ToolTip=dropName??selected??sourcePath;
                var remove=pair.Item5=="A"?removeA:removeB;
                remove.Visibility=view.Running&&state!=null&&state.HasMedia?Visibility.Visible:Visibility.Collapsed;
                remove.IsEnabled=button.IsEnabled;
                bool show=(openingSide!=null&&openingSide.Contains(pair.Item5))||(state!=null&&(state.HasMedia||state.Busy));
                var empty=pair.Item5=="A"?emptyA:emptyB;var emptyOpen=pair.Item5=="A"?emptyOpenA:emptyOpenB;var emptyTitle=pair.Item5=="A"?emptyTitleA:emptyTitleB;
                view.Visibility=show?Visibility.Visible:Visibility.Hidden;empty.Visibility=show?Visibility.Collapsed:Visibility.Visible;
                emptyOpen.IsEnabled=button.IsEnabled;emptyOpen.Content=view.Running?Ui.Text("OpenVideoPrefix")+pair.Item5+"…":Ui.Text("RestartPrefix")+pair.Item5;
                emptyTitle.Visibility=starting||!view.Running?Visibility.Visible:Visibility.Collapsed;
                emptyTitle.Text=starting?Ui.Text("StartingPreviewPrefix")+pair.Item5+"…":!view.Running?Ui.Text("PreviewPrefix")+pair.Item5+Ui.Text("UnavailableSuffix"):Ui.Text("VideoPrefix")+pair.Item5+Ui.Text("NotOpenSuffix");
            }
        }
        private void EngineExited(EmbeddedView view) {
            if(closing||closed)return;
            Controller.Session.State.Linked=false;
            Controller.Session.Say(Ui.Text("EngineStopped"),true,true);
            RenderHeaders();
        }
        private async void OnClosing(object sender,CancelEventArgs e) {
            if(closed)return;
            e.Cancel=true;
            if(closing)return;
            if((a.Running&&Controller.Session.HostA!=null&&Controller.Session.HostA.Busy)||(b.Running&&Controller.Session.HostB!=null&&Controller.Session.HostB.Busy)) {
                Controller.Session.Say(Ui.Text("StopBeforeExit"),true);
                return;
            }
            closing=true;starting=false;lifetime.Cancel();RenderHeaders();
            // Exit the first Closing event even when no engine was started.
            await Task.Yield();
            a.RequestClose();b.RequestClose();
            // Do not block WPF while cross-process child windows are exiting.
            for(int i=0;i<40&&(a.Running||b.Running);i++)await Task.Delay(50);
            engines.Dispose();
            for(int i=0;i<20&&(a.Running||b.Running);i++)await Task.Delay(25);
            closed=true;Window.Close();
        }
    }

    public sealed partial class AppController {
        private void AddEmbeddedSettings(StackPanel panel) {
            panel.Children.Add(Section(Ui.Text("KeysHeading")));
            var global=new CheckBox{Content=Ui.Text("KeysInVideo"),IsChecked=GlobalKeys};panel.Children.Add(global);
            global.Click+=(s,e)=>{GlobalKeys=global.IsChecked==true;UpdateKeys();SaveData();};
            var hints=new CheckBox{Content=Ui.Text("ShowKeyHints"),IsChecked=Hints};panel.Children.Add(hints);
            hints.Click+=(s,e)=>{Hints=hints.IsChecked==true;Render();SaveData();};
            panel.Children.Add(Caption(Ui.Text("EmbeddedKeyGuide")));
        }
    }
}
