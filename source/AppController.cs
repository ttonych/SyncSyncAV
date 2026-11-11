// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SyncSyncAV {
    public sealed partial class AppController {
        public readonly Window Window;
        public bool Embedded {get;private set;}
        public Func<Task> SwapEmbedded;
        public readonly SyncSession Session=new SyncSession(new BridgeClient());
        public readonly LayoutController Layout=new LayoutController();
        private readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        private readonly DispatcherTimer timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(180)};
        private readonly SemaphoreSlim gate=new SemaphoreSlim(1,1);
        private readonly Dictionary<string,DiscMetadata> metadata=new Dictionary<string,DiscMetadata>(StringComparer.OrdinalIgnoreCase);
        private readonly string dataPath,logPath;private bool saveEnabled=true;
        private HostKeys keys;private Window popup;private bool closing,changingPickers;
        public bool Working;public int BigStep=10;public bool Hints=true,GlobalKeys=true;
        public event Action Rendered;
        public Task LastAction=Task.FromResult(0);
        public IntPtr Hwnd {get{return new WindowInteropHelper(Window).Handle;}}
        public bool Restoring {get;private set;}
        public AppController(Window window,string storage,string log,bool embedded=false) {
            Embedded=embedded;
            Window=window;dataPath=storage;logPath=log;AppIcon.Apply(Window);
            Window.Resources["UiFont"]=new FontFamily(new Uri(AppDomain.CurrentDomain.BaseDirectory),"./Fonts/#Golos Text");
            Window.Resources["NumberFont"]=new FontFamily(new Uri(AppDomain.CurrentDomain.BaseDirectory),"./Fonts/#JetBrains Mono");
            System.Windows.Shell.WindowChrome.SetWindowChrome(Window,new System.Windows.Shell.WindowChrome{CaptionHeight=0,ResizeBorderThickness=new Thickness(4),GlassFrameThickness=new Thickness(0),CornerRadius=new CornerRadius(0),UseAeroCaptionButtons=false});
            Window.Topmost=true;
            Ui.Select(null);
            LoadData();
            ApplyLanguageResources();
            if(!Session.Warning)Session.Message=Ui.Text("FindingWindows");
            Session.FormatOffset=DisplayOffset;
            Session.Changed+=Render;
            Bind("Back",()=>Apply("step",-Step));Bind("Forward",()=>Apply("step",Step));
            Bind("NudgeAMinus",()=>Apply("nudgeA",-Step));Bind("NudgeAPlus",()=>Apply("nudgeA",Step));
            Bind("NudgeBMinus",()=>Apply("nudgeB",-Step));Bind("NudgeBPlus",()=>Apply("nudgeB",Step));
            Bind("Link",()=>Apply("link"));Bind("Anchor",()=>Apply("anchor"));
            Bind("ResetOffset",()=>{FocusController();return Apply("offset",0);});
            B("OffsetDirection").Click+=(s,e)=>ToggleOffsetDirection();Bind("CopyOffset",CopyOffsetAsync);
            Bind("Swap",()=>SwapEmbedded!=null?SwapEmbedded():Run(Ui.Text("Swapping"),()=>Session.SwapAsync(lifetime.Token)));
            foreach(int value in new[]{10,24,48}) {int step=value;B("Step"+step).Click+=(s,e)=>{BigStep=step;SaveData();Render();};}
            Bind("Tile",()=>Run(Ui.Text("Tiling"),async()=>{await Session.PollAsync(lifetime.Token);await Layout.TileAsync(Hwnd,Session.HostA,Session.HostB,lifetime.Token);Session.Say(Ui.Text("Tiled"));}));
            Bind("RestoreLayout",()=>Run(Ui.Text("RestoringLayout"),async()=>{Restoring=true;try{Session.Say(await Layout.RestoreAsync(lifetime.Token));}finally{Restoring=false;}}));
            B("Connections").Click+=(s,e)=>OpenConnections();B("Points").Click+=(s,e)=>OpenPoints();
            B("CloseButton").Click+=(s,e)=>Window.Close();B("Minimize").Click+=(s,e)=>Window.WindowState=WindowState.Minimized;
            T("Status").MouseLeftButtonUp+=(s,e)=>MessageBox.Show(Window,Session.Message,Ui.Text("LastAction"),MessageBoxButton.OK,MessageBoxImage.Information);
            C<Border>("TitleBar").MouseLeftButtonDown+=(s,e)=>{if(e.ClickCount==1&&e.OriginalSource==s)Window.DragMove();};
            // Drag on non-interactive title text/background, while preserving all buttons.
            C<Border>("TitleBar").PreviewMouseLeftButtonDown+=(s,e)=>{DependencyObject n=e.OriginalSource as DependencyObject;while(n!=null&&n!=s){if(n is Button||n==T("Status"))return;n=VisualTreeHelper.GetParent(n);}if(e.ClickCount==1){Window.DragMove();e.Handled=true;}};
            foreach(string name in new[]{"FrameA","FrameB","Offset"}) {
                var input=C<TextBox>(name);
                input.GotKeyboardFocus+=(s,e)=>{input.SelectAll();Render();};
                input.PreviewMouseLeftButtonDown+=(s,e)=>{if(!input.IsKeyboardFocusWithin){input.Focus();e.Handled=true;}};
                input.LostKeyboardFocus+=(s,e)=>Render();
                input.PreviewKeyDown+=(s,e)=>{
                    if(e.Key==Key.Escape){FocusController();Render();e.Handled=true;}
                    else if(e.Key==Key.Enter){string text=input.Text;LastAction=EnterField(name,text);e.Handled=true;}
                };
            }
            Window.PreviewKeyDown+=OnKey;WatchNativeMessages(Window);
            // A background click must return keyboard input from a native preview
            // or numeric editor to a real WPF target, not leave focus empty.
            Window.PreviewMouseLeftButtonDown+=(s,e)=>{
                var target=e.OriginalSource as DependencyObject;
                if(FindParent<System.Windows.Controls.Primitives.ButtonBase>(target)!=null||FindParent<TextBox>(target)!=null||FindParent<ComboBox>(target)!=null||FindParent<EmbeddedView>(target)!=null)return;
                FocusController();
            };
            Window.SizeChanged+=(s,e)=>{Render();PositionPopup();};Window.LocationChanged+=(s,e)=>PositionPopup();
            Window.Closing+=(s,e)=>{if(e.Cancel)return;closing=true;timer.Stop();lifetime.Cancel();statusTimer.Stop();if(keys!=null)keys.Dispose();if(popup!=null)popup.Close();};
            timer.Tick+=async(s,e)=>{
                if(Working||closing||!gate.Wait(0))return;
                try {await Session.PollAsync(lifetime.Token);}
                catch(OperationCanceledException){}catch(Exception error){Session.State.Linked=false;Session.Say(error.Message,true,true);Log("poll_error",error.Message);}
                finally{gate.Release();}
            };
            Render();
        }
        public TControl C<TControl>(string name) where TControl:class {return (TControl)Window.FindName(name);}
        public Button B(string name){return C<Button>(name);}private TextBlock T(string name){return C<TextBlock>(name);}
        private int Step {get{return ResolveStep(Desktop.Shift,Desktop.Control);}}
        public int ResolveStep(bool shift,bool ctrl){return shift?BigStep*(ctrl?2:1):1;}
        private void Bind(string name,Func<Task> action){B(name).Click+=(s,e)=>{LastAction=action();};}
        public async Task StartAsync(int a,int b,bool anchorInitial=true) {
            await Run(Ui.Text("FindingWindows"),async()=>{
                var hosts=Embedded?new List<HostState>():await Discover();
                if(a==0&&b==0&&hosts.Count==2){a=hosts[0].Pid;b=hosts[1].Pid;}
                await Session.SelectAsync(a,b,anchorInitial,lifetime.Token);
                if(!Embedded&&hosts.Count==0)Session.Say(Ui.Text("NoSdkWindows"),true);
            });
            try{keys=new HostKeys();keys.OnKey=HostKey;UpdateKeys();}catch(Exception e){GlobalKeys=false;Session.Say(Ui.Text("HostKeysUnavailable")+e.Message+Ui.Text("ShellKeysAvailable"),true);}
            timer.Start();if(!Embedded&&!Session.PairReady)OpenConnections();
        }
        public async Task Run(string message,Func<Task> action) {
            if(Working||closing)return;Working=true;Render();bool locked=false;var clock=Stopwatch.StartNew();
            try{await gate.WaitAsync(lifetime.Token);locked=true;Session.Say(message);await action();SaveData();Log("action",Session.Message,clock.Elapsed.TotalMilliseconds);}
            catch(OperationCanceledException){}catch(Exception e){bool ordinary=e is InvalidOperationException&&e.Message.StartsWith(Ui.Text("SharedStepPrefix"),StringComparison.Ordinal);Session.Say(e.Message,true,!ordinary);Log(ordinary?"warning":"error",e.Message,clock.Elapsed.TotalMilliseconds);}
            finally{if(locked)gate.Release();Working=false;if(!closing)Render();}
        }
        private Task Apply(string kind,long value=0){return Run(Ui.Text("CheckingFrames"),()=>Session.ApplyAsync(SyncAction.Create(kind,value),lifetime.Token));}
        public Task EnterField(string name,string text) {
            long value;bool relative;if(!SyncPlanner.Parse(text,out value,out relative)){Session.Say(Ui.Text("EnterFrame"),true,true);return Task.FromResult(0);}
            if(name!="Offset"&&!relative&&value<0){Session.Say(Ui.Text("NegativeFrame"),true,true);return Task.FromResult(0);}
            string kind=name=="Offset"?"offset":(relative?"relative":"goto")+(name=="FrameA"?"A":"B");
            if(name=="Offset"&&ReverseOffset){try{value=checked(-value);}catch(OverflowException){Session.Say(Ui.Text("OffsetOverflow"),true,true);return Task.FromResult(0);}}
            FocusController();return Apply(kind,value);
        }
        private void OnKey(object sender,KeyEventArgs e) {
            if(e.Handled||FindParent<ComboBox>(Keyboard.FocusedElement as DependencyObject)!=null)return;
            if((Keyboard.Modifiers&(ModifierKeys.Alt|ModifierKeys.Windows))!=0)return;
            Key key=e.Key==Key.System?e.SystemKey:e.Key;
            if(key==Key.NumPad4||key==Key.NumPad6||((key==Key.Left||key==Key.Right)&&!nativeExtended))return;
            uint scan=Desktop.MapVirtualKeyW((uint)KeyInterop.VirtualKeyFromKey(key),0);
            bool ctrl=(Keyboard.Modifiers&ModifierKeys.Control)!=0;
            var editor=Keyboard.FocusedElement as TextBox;
            if(editor!=null) {
                // G/H/O switch our numeric fields; ordinary text editors and
                // standard editing shortcuts keep their normal input behavior.
                bool own=editor==C<TextBox>("FrameA")||editor==C<TextBox>("FrameB")||editor==C<TextBox>("Offset");
                if(!own||ctrl||(scan!=0x22&&scan!=0x23&&scan!=0x18))return;
            }
            if(DispatchKey(scan,ctrl,(Keyboard.Modifiers&ModifierKeys.Shift)!=0))e.Handled=true;
        }
        private bool HostKey(uint scan,bool ctrl,bool shift,bool extended) {
            if((ctrl&&(!shift||scan==0x2c||scan==0x15))||((scan==0x4b||scan==0x4d)&&(!extended||!Session.State.Linked))||!Recognized(scan,ctrl,shift))return false;
            if(!Working)Window.Dispatcher.BeginInvoke(new Action(()=>DispatchKey(scan,ctrl,shift)),DispatcherPriority.Input);
            return true;
        }
        private static bool IsDoubleStepKey(uint scan){return new uint[]{0x4b,0x4d,0x1e,0x1f,0x20,0x21}.Contains(scan);}
        private static bool Recognized(uint scan,bool ctrl,bool shift) {return ctrl&&shift&&IsDoubleStepKey(scan)|| (ctrl?(scan==0x2c||scan==0x15):new uint[]{0x4b,0x4d,0x1e,0x1f,0x20,0x21,0x26,0x25,0x22,0x23,0x18}.Contains(scan));}
        public bool DispatchKey(uint scan,bool ctrl,bool shift) {
            if(!Recognized(scan,ctrl,shift))return false;if(Working)return true;
            if(ctrl&&!(shift&&IsDoubleStepKey(scan))){bool redo=shift||scan==0x15;LastAction=Run(redo?Ui.Text("Redoing"):Ui.Text("Undoing"),()=>Session.UndoAsync(redo,lifetime.Token));return true;}
            FlashKey(scan);
            int step=ResolveStep(shift,ctrl);
            switch(scan){case 0x4b:LastAction=Apply("step",-step);break;case 0x4d:LastAction=Apply("step",step);break;
                case 0x1e:LastAction=Apply("nudgeA",-step);break;case 0x1f:LastAction=Apply("nudgeA",step);break;
                case 0x20:LastAction=Apply("nudgeB",-step);break;case 0x21:LastAction=Apply("nudgeB",step);break;
                case 0x26:LastAction=Apply("link");break;case 0x25:LastAction=Apply("anchor");break;
                case 0x22:FocusField("FrameA");break;case 0x23:FocusField("FrameB");break;case 0x18:FocusField("Offset");break;}
            return true;
        }
        private void FocusController(){Window.Focusable=true;FocusManager.SetFocusedElement(Window,Window);Keyboard.Focus(Window);}
        private void FocusField(string name){if(Window.WindowState==WindowState.Minimized)Window.WindowState=WindowState.Normal;Window.Activate();var input=C<TextBox>(name);if(input.IsEnabled){Keyboard.Focus(input);input.SelectAll();}}
        private void UpdateKeys(){if(keys!=null){keys.Enabled=GlobalKeys;keys.A=Session.HostA;keys.B=Session.HostB;}}
        private Brush Brush(string color){return (Brush)new BrushConverter().ConvertFromString(color);}
        private DiscMetadata Meta(HostState h){if(h==null||String.IsNullOrEmpty(h.File))return null;if(h.Video!=null)return h.Video;DiscMetadata m;if(!metadata.TryGetValue(h.File,out m)){m=DiscMetadata.TryRead(h.File);metadata[h.File]=m;}return m;}
        private static string Timecode(long frame,DiscMetadata m){if(m==null||m.Fps<=0)return Ui.Text("ZeroBasedFrames");try{var t=TimeSpan.FromMilliseconds((double)m.RoundedMilliseconds(frame));return ((int)t.TotalHours)+":"+t.Minutes.ToString("00")+":"+t.Seconds.ToString("00")+"."+t.Milliseconds.ToString("000");}catch{return "—";}}
        private void RenderHost(string side,HostState h,int pid) {
            DiscMetadata m=Meta(h);bool ready=SyncSession.Usable(h);
            string file=h==null||String.IsNullOrEmpty(h.File)?(Embedded?Ui.Text("OpenVideo"):Ui.Text("ChooseWindow")):System.IO.Path.GetFileName(h.File);
            T("Title"+side).Text=SourceTitle(h,side);T("Title"+side).ToolTip=T("Title"+side).Text;
            T("Title"+side).MaxWidth=Math.Max(20,(Window.ActualWidth-1066)/2-51);
            T("File"+side).Text=file;T("File"+side).ToolTip=h==null?null:h.File;
            T("Spec"+side).Text=m==null?Ui.Text("MissingMetadata"):(m.Codec??Ui.Text("Video"))+" · "+m.Fps.ToString("0.###",Ui.Culture)+Ui.Text("FpsUnit")+m.Width+"×"+m.Height;
            T("Spec"+side).ToolTip=m==null?Ui.Text("MissingMetadataHint"):m.Origin=="input_driver"?Ui.Text("StreamMetadata")+m.ResolvedFile:Ui.Text("DiscMetadata");
            T("Length"+side).Text=h==null||!h.HasMedia?Ui.Text("NoVideoLoaded"):h.Count.ToString("N0",Ui.Culture)+Ui.Text("FramesUnit")+(m==null?"":" · "+Timecode(h.Count,m).Split('.')[0]);
            T("Connection"+side).Text=Embedded?(h==null?Ui.Text("PreviewUnavailable"):h.Busy?Ui.Text("PreviewBusy"):h.HasMedia?Ui.Text("EmbeddedPreview"):Ui.Text("ReadyToOpen")):pid==0?Ui.Text("Disconnected"):h==null?Ui.Text("UnavailablePid")+pid:h.Busy?Ui.Text("BusyPid")+pid:Ui.Text("HostPid")+pid;
            bool empty=Embedded&&h!=null&&!h.HasMedia&&!h.Busy;
            if(empty)T("Connection"+side).Text=Ui.Text("VideoNotOpen");
            T("Spec"+side).Visibility=T("Length"+side).Visibility=empty?Visibility.Collapsed:Visibility.Visible;
            C<Ellipse>("ConnectionDot"+side).Fill=Brush(ready?"#73bf8f":"#d4a14e");
            var input=C<TextBox>("Frame"+side);
            if(!input.IsKeyboardFocusWithin)input.Text=h==null||!h.Frame.HasValue?"—":h.Frame.Value.ToString(CultureInfo.InvariantCulture);
            T("Time"+side).Text=h!=null&&h.Frame==h.Count?Ui.Text("TimelineEnd"):h==null||!h.Frame.HasValue?"—":Timecode(h.Frame.Value,m);
            bool enabled=!Working&&ready&&Session.PidA!=Session.PidB;
            input.IsEnabled=enabled;B("Nudge"+side+"Minus").IsEnabled=enabled&&(!Session.State.Linked||Session.PairReady);B("Nudge"+side+"Plus").IsEnabled=enabled&&(!Session.State.Linked||Session.PairReady);
        }
        public void Render() {
            if(closing)return;
            RenderHost("A",Session.HostA,Session.PidA);RenderHost("B",Session.HostB,Session.PidB);
            bool linked=Session.State.Linked,ready=Session.PairReady&&!Working;
            C<Line>("Bridge").Stroke=Brush(linked?"#5db6c0":"#4b535c");C<Line>("Bridge").StrokeDashArray=linked?null:new DoubleCollection(new[]{3.0,3.0});
            C<Border>("CounterA").BorderBrush=C<Border>("CounterB").BorderBrush=Brush(linked?"#8c5db6c0":"#343a41");
            foreach(string side in new[]{"A","B"})if(Session.Boundary.Contains(side))C<Border>("Counter"+side).BorderBrush=Brush("#d4a14e");
            B("Link").IsEnabled=ready;B("Link").Background=Brush(linked?"#243237":"#252a30");B("Link").BorderBrush=Brush(linked?"#8c5db6c0":"#343a41");
            T("LinkState").Text=linked?Ui.Text("Linked"):Ui.Text("Unlinked");T("LinkState").Foreground=Brush(linked?"#86ccd4":"#e3e6e9");
            C<Border>("SwitchTrack").Background=Brush(linked?"#5db6c0":"#4b535c");C<Ellipse>("SwitchKnob").HorizontalAlignment=linked?HorizontalAlignment.Right:HorizontalAlignment.Left;
            C<Ellipse>("SwitchKnob").Fill=Brush(linked?"#071d21":"#a8b0b8");
            T("OffsetCaption").Text=linked?Ui.Text("OffsetHeading"):Ui.Text("OffsetInactive");T("OffsetCaption").Foreground=Brush(linked?"#79828b":"#d4a14e");
            var off=C<TextBox>("Offset");if(!off.IsKeyboardFocusWithin)off.Text=DisplayOffset(Session.State.Offset);off.IsEnabled=ready;off.Foreground=Brush(linked?"#86ccd4":"#79828b");
            B("OffsetDirection").Content=OffsetFormula;B("OffsetDirection").IsEnabled=!Working;
            B("OffsetDirection").ToolTip=OffsetFormula+Ui.Text("SwitchFormulaIntro")+(ReverseOffset?"B = A":"A = B")+Ui.Text("SwitchFormulaEnd");
            AutomationProperties.SetName(B("OffsetDirection"),Ui.Text("OffsetDirection")+OffsetFormula+Ui.Text("SwitchDirectionAction"));
            AutomationProperties.SetName(off,ReverseOffset?Ui.Text("OffsetRelativeA"):Ui.Text("OffsetRelativeB"));
            off.ToolTip=OffsetFormula+Ui.Text("ApplyOffsetHint");
            string copyText,copyError;bool canCopy=TryGetOffsetCopyText(out copyText,out copyError);
            B("CopyOffset").IsEnabled=ready&&canCopy;B("CopyOffset").ToolTip=canCopy?Ui.Text("CopyValue")+copyText+(linked?"":Ui.Text("StoredOffsetHint")):copyError;
            ToolTipService.SetShowOnDisabled(B("CopyOffset"),true);
            B("Swap").IsEnabled=ready;
            B("ResetOffset").IsEnabled=ready&&Session.State.Offset!=0;
            B("ResetOffset").ToolTip=linked?Ui.Text("ResetAndAlign"):Ui.Text("ResetAndKeep");
            foreach(int value in new[]{10,24,48}) {
                var button=B("Step"+value);bool selected=value==BigStep;
                button.Background=Brush(selected?"#2f353c":"Transparent");button.BorderBrush=Brush(selected?"#4b535c":"#343a41");button.Foreground=Brush(selected?"#e3e6e9":"#a8b0b8");
                button.ToolTip="Shift: "+value+Ui.Text("StepUnitsWithDouble")+(value*2);AutomationProperties.SetItemStatus(button,selected?Ui.Text("Selected"):"");
            }
            B("Anchor").IsEnabled=ready;T("AnchorPreview").Text=Session.PairReady?Session.State.A+" ↔ "+Session.State.B+" → "+DisplayOffset(Session.State.B-Session.State.A):(Embedded?Ui.Text("OpenBothVideos"):Ui.Text("SelectTwoWindows"));
            B("Back").IsEnabled=B("Forward").IsEnabled=ready&&linked;
            B("Tile").IsEnabled=!Working&&Session.HostA!=null&&Session.HostB!=null&&!Session.HostA.Busy&&!Session.HostB.Busy&&Session.PidA!=Session.PidB;
            B("RestoreLayout").IsEnabled=!Working&&Layout.CanRestore;B("Connections").IsEnabled=!Working;
            RenderPointCount();B("Points").IsEnabled=!Working;
            RenderStatus();
            var ma=Meta(Session.HostA);var mb=Meta(Session.HostB);T("FpsWarning").Text=ma!=null&&mb!=null&&Math.Abs(ma.Fps-mb.Fps)>0.0001?Ui.Text("DifferentRates"):"";
            bool commonFps=ma!=null&&mb!=null&&ma.Fps>0&&mb.Fps>0&&Math.Abs(ma.Fps-mb.Fps)<=0.0001;
            var milliseconds=T("OffsetMs");
            decimal offsetMs=commonFps?OffsetMilliseconds(Session.State.Offset).GetValueOrDefault():0;
            milliseconds.Text=commonFps?(offsetMs>0?"+":offsetMs<0?"−":"±")+Math.Abs(offsetMs).ToString("N0",Ui.Culture)+Ui.Text("MillisecondsUnit"):Ui.Text("NoMilliseconds");
            milliseconds.ToolTip=commonFps?Ui.Text("MsCalculation")+ma.Fps.ToString("0.######",Ui.Culture)+Ui.Text("MsRounding"):Ui.Text("NeedSameRate");
            T("FpsWarning").ToolTip=Ui.Text("DifferentRatesHint");
            foreach(string name in new[]{"HintL","HintK","HintA","HintS","HintD","HintF","HintLeft","HintRight"})((Border)T(name).Parent).Visibility=Hints?Visibility.Visible:Visibility.Hidden;
            RenderDetails(linked);
            UpdateKeys();
            if(Rendered!=null)Rendered();
        }
        private async Task<List<HostState>> Discover(){var pids=await Task.Run(()=>BridgeClient.EnumeratePids());var result=new List<HostState>();foreach(int pid in pids){try{result.Add(await new BridgeClient(2500).StatusAsync(pid,lifetime.Token));}catch(OperationCanceledException){throw;}catch{}}knownHosts=result;return result;}
        public event Action LanguageChanged;

        private void ApplyLanguageResources() {
            Window.Language=System.Windows.Markup.XmlLanguage.GetLanguage(Ui.Culture.Name);
            foreach(var entry in Ui.Strings)Window.Resources["L."+entry.Key]=entry.Value;
        }

        private void LoadData() {
            if(!File.Exists(dataPath))return;
            try {var serializer=new JavaScriptSerializer{MaxJsonLength=4194304};var settings=serializer.Deserialize<SavedSettings>(File.ReadAllText(dataPath));if(settings==null||settings.Version!=1)throw new InvalidDataException();
                Ui.Select(settings.Language);
                Hints=settings.Hints;GlobalKeys=settings.GlobalKeys;Window.Topmost=settings.Topmost;if(new[]{10,24,48}.Contains(settings.BigStep))BigStep=settings.BigStep;
                ReverseOffset=settings.ReverseOffset;OffsetCopyTemplate=OffsetText.ValidateTemplate(settings.OffsetCopyTemplate)==null?settings.OffsetCopyTemplate:OffsetText.DefaultTemplate;
                if(settings.PointSets!=null)foreach(var kv in settings.PointSets){if(kv.Key.Length>8192||kv.Value==null||kv.Value.Count>10000)throw new InvalidDataException();Session.PointSets[kv.Key]=kv.Value.Where(p=>p.A>=0&&p.B>=0&&p.Offset==p.B-p.A).ToList();}
            }catch{saveEnabled=false;Session.Say(Ui.Text("SettingsReadFailure"),true);}
        }
        private void SaveData() {
            if(!saveEnabled)return;
            try {Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dataPath));string tmp=dataPath+".tmp";var data=new SavedSettings{Version=1,Language=Ui.Code,Hints=Hints,GlobalKeys=GlobalKeys,Topmost=Window.Topmost,BigStep=BigStep,ReverseOffset=ReverseOffset,OffsetCopyTemplate=OffsetCopyTemplate,PointSets=Session.PointSets};File.WriteAllText(tmp,new JavaScriptSerializer{MaxJsonLength=4194304}.Serialize(data));if(File.Exists(dataPath))File.Replace(tmp,dataPath,dataPath+".bak");else File.Move(tmp,dataPath);}
            catch(Exception e){Session.Say(Ui.Text("SettingsWriteFailure")+e.Message,true);}
        }
        private void Log(string kind,string message,double ms=0){if(logPath==null)return;try{Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath));File.AppendAllText(logPath,new JavaScriptSerializer().Serialize(new{utc=DateTime.UtcNow.ToString("o"),kind=kind,message=message,ms=ms,a=Session.State.A,b=Session.State.B,offset=Session.State.Offset,linked=Session.State.Linked})+Environment.NewLine);}catch(IOException){}catch(UnauthorizedAccessException){}}
        public sealed class SavedSettings{public int Version,BigStep;public string Language;public bool Hints=true,GlobalKeys=true,Topmost=true,ReverseOffset=true;public string OffsetCopyTemplate=OffsetText.DefaultTemplate;public Dictionary<string,List<SyncPoint>> PointSets;}
    }
}
