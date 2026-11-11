// SPDX-License-Identifier: MIT
using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace SyncSyncAV {
    public sealed partial class AppController {
        private bool nativeExtended=true;
        private string statusKey;
        private readonly DispatcherTimer statusTimer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2.2)};
        private readonly Dictionary<string,string> volumeTitles=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Button,DispatcherTimer> flashes=new Dictionary<Button,DispatcherTimer>();
        private TextBlock pointLabel;private Run pointNumber;
        private static T FindParent<T>(DependencyObject element) where T:DependencyObject {
            while(element!=null){var target=element as T;if(target!=null)return target;element=element is Visual?VisualTreeHelper.GetParent(element):LogicalTreeHelper.GetParent(element);}return null;
        }
        private void WatchNativeMessages(Window w) {
            w.SourceInitialized+=(s,e)=>((HwndSource)PresentationSource.FromVisual(w)).AddHook((IntPtr hwnd,int msg,IntPtr wp,IntPtr lp,ref bool handled)=>{
                if(msg==0x100||msg==0x104)nativeExtended=(lp.ToInt64()&0x1000000)!=0;
                if(msg==0x0219&&w==Window)volumeTitles.Clear(); // WM_DEVICECHANGE: media may have changed under the same letter.
                return IntPtr.Zero;
            });
        }
        internal string SourceTitle(HostState host,string side) {
            if(host==null||String.IsNullOrEmpty(host.File))return Ui.Text("VideoPrefix")+side;
            if(host.File.EndsWith(".iso",StringComparison.OrdinalIgnoreCase))return Path.GetFileName(host.File);

            string playlist=host.PlaylistPath;
            string path=(playlist??host.File).Replace('/','\\');
            string root=Path.GetPathRoot(path);
            int bdmv=path.LastIndexOf(@"\BDMV\",StringComparison.OrdinalIgnoreCase);
            if(bdmv<root.TrimEnd('\\').Length)bdmv=-1; // A UNC server or share can itself be named BDMV.
            if(bdmv>=0||playlist!=null) {
                string folder=bdmv>=0?path.Substring(0,bdmv+1):Path.GetDirectoryName(path);
                if(bdmv<0&&String.Equals(Path.GetFileName(folder),"PLAYLIST",StringComparison.OrdinalIgnoreCase))
                    folder=Path.GetDirectoryName(folder);
                if(!String.IsNullOrEmpty(folder)) {
                    string trimmed=folder.TrimEnd('\\');
                    if(!String.Equals(trimmed,root.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))return Path.GetFileName(trimmed);
                    return VolumeTitle(root);
                }
            }
            try {if(new DriveInfo(root).DriveType==DriveType.CDRom)return VolumeTitle(root);}catch(ArgumentException){}catch(IOException){}
            return Path.GetFileNameWithoutExtension(path);
        }
        private string VolumeTitle(string root) {
            if(root.StartsWith(@"\\",StringComparison.Ordinal))return root.TrimEnd('\\').Split('\\').Last();
            string label;
            if(!volumeTitles.TryGetValue(root,out label)) {
                try {label=new DriveInfo(root).VolumeLabel;}catch(IOException){label=null;}catch(UnauthorizedAccessException){label=null;}
                volumeTitles[root]=label;
            }
            return String.IsNullOrWhiteSpace(label)?root:label;
        }

        private void RenderPointCount() {
            if(pointLabel==null){pointLabel=new TextBlock();var label=new Run();label.SetResourceReference(System.Windows.Documents.Run.TextProperty,"L.PointsCount");pointLabel.Inlines.Add(label);pointNumber=new Run{FontFamily=(FontFamily)Window.Resources["NumberFont"],FontWeight=FontWeights.SemiBold,Foreground=Brush("#e3e6e9")};pointLabel.Inlines.Add(pointNumber);B("Points").Content=pointLabel;B("Points").MouseEnter+=(s,e)=>pointLabel.TextDecorations=TextDecorations.Underline;B("Points").MouseLeave+=(s,e)=>pointLabel.TextDecorations=null;}
            pointNumber.Text=Session.Points.Count.ToString();
            if(popupKey=="Points"&&refreshPointRows!=null) {
                string key=ReverseOffset+"\n"+Session.PairKey+"\n"+String.Join(";",Session.Points.Select(p=>p.A+","+p.B+","+p.Label));
                if(key!=renderedPointsKey){renderedPointsKey=key;refreshPointRows();}
                if(savePointButton!=null)savePointButton.IsEnabled=Session.PairReady&&!Working;
            }
        }
        private void RenderStatus() {
            string key=Session.Message+"|"+Session.Warning+"|"+Session.Error;
            T("Status").Text=Session.Message;T("Status").ToolTip=Session.Message+Ui.Text("StatusDetail");
            if(statusKey==key)return;statusKey=key;statusTimer.Stop();
            var foreground=new SolidColorBrush((Color)ColorConverter.ConvertFromString(Session.Error?"#e0705f":Session.Warning?"#d4a14e":"#e3e6e9"));T("Status").Foreground=foreground;
            statusTimer.Tick-=FadeStatus;statusTimer.Tick+=FadeStatus;if(!Session.Warning&&!Session.Error)statusTimer.Start();
        }
        private void FadeStatus(object sender,EventArgs e){statusTimer.Stop();var brush=T("Status").Foreground as SolidColorBrush;if(brush!=null&&!brush.IsFrozen)brush.BeginAnimation(SolidColorBrush.ColorProperty,new ColorAnimation((Color)ColorConverter.ConvertFromString("#79828b"),TimeSpan.FromSeconds(.8)));}
        private void RenderDetails(bool linked) {
            bool hot=!linked&&Session.PairReady&&Session.State.B-Session.State.A!=Session.State.Offset;
            B("Anchor").BorderBrush=Brush(hot?"#4b535c":"#343a41");B("Anchor").Background=Brush(hot?"#2f353c":"#252a30");T("AnchorPreview").Foreground=Brush(hot?"#e3e6e9":"#79828b");
            B("NudgeAMinus").ToolTip=Ui.Text("NudgeAMinusHint");
            B("NudgeAPlus").ToolTip=Ui.Text("NudgeAPlusHint");
            B("NudgeBMinus").ToolTip=Ui.Text("NudgeBMinusHint");
            B("NudgeBPlus").ToolTip=Ui.Text("NudgeBPlusHint");
            B("Anchor").ToolTip=Ui.Text("AnchorHint");
            B("Link").ToolTip=linked?Ui.Text("UnlinkHint"):Ui.Text("LinkOffsetHint")+DisplayOffset(Session.State.Offset)+Ui.Text("BMovesTo")+((decimal)Session.State.A+Session.State.Offset)+" (L)";
            foreach(string name in new[]{"Back","Forward"}){var b=B(name);b.Background=Brush(linked?"#30464d":"#2f353c");b.BorderBrush=Brush(linked?"#8c5db6c0":"#4b535c");b.ToolTip=linked?Ui.Text("BothStepHint")+(name=="Back"?Ui.Text("Backward"):Ui.Text("ForwardDirection"))+Ui.Text("ShiftMoves")+BigStep+Ui.Text("DoubleMoves")+(BigStep*2):Ui.Text("LinkedStepHint");}
            foreach(string side in new[]{"A","B"}){
                var input=C<TextBox>("Frame"+side);var time=T("Time"+side);bool focus=input.IsKeyboardFocusWithin;
                Grid.SetColumn(time,focus?0:2);Grid.SetColumnSpan(time,focus?3:1);
                time.FontFamily=(FontFamily)Window.Resources[focus?"UiFont":"NumberFont"];time.Foreground=Brush(focus?"#5db6c0":"#79828b");
                time.FontSize=11;time.TextTrimming=focus?TextTrimming.None:TextTrimming.CharacterEllipsis;time.Margin=focus?new Thickness(-7,0,-10,0):new Thickness(0);time.HorizontalAlignment=focus?HorizontalAlignment.Center:HorizontalAlignment.Right;
                if(focus){time.Text=Ui.Text("FrameEditHint");var measure=new FormattedText(time.Text,Ui.Culture,FlowDirection.LeftToRight,new Typeface(time.FontFamily,FontStyles.Normal,FontWeights.Normal,FontStretches.Normal),11,Brushes.White,VisualTreeHelper.GetDpi(time).PixelsPerDip);double available=C<Border>("Counter"+side).ActualWidth-12;if(available>0)time.FontSize=Math.Min(11,11*available/measure.WidthIncludingTrailingWhitespace);}
                var counter=C<Border>("Counter"+side);counter.BorderBrush=Brush(focus?"#5db6c0":Session.Boundary.Contains(side)?"#d4a14e":linked?"#8c5db6c0":"#343a41");FocusEffect(counter,focus);
                foreach(UIElement child in ((Grid)counter.Child).Children){var badge=child as Border;if(badge!=null)Grid.SetRowSpan(badge,focus?1:2);}
            }
            var offset=C<Border>("OffsetBox");bool offFocus=C<TextBox>("Offset").IsKeyboardFocusWithin;offset.BorderBrush=Brush(offFocus?"#5db6c0":"#343a41");FocusEffect(offset,offFocus);
            foreach(string name in new[]{"Connections","Points"})B(name).Background=Brush(popup!=null&&popupKey==name?"#252a30":"Transparent");
            foreach(var row in pointRows){bool current=linked&&row.Value.A==Session.State.A&&row.Value.B==Session.State.B;row.Key.Background=Brush(current?"#243237":"Transparent");var grid=(Grid)row.Key.Parent;foreach(UIElement child in grid.Children){var text=child as TextBlock;if(text!=null&&Grid.GetRow(text)==Grid.GetRow(row.Key)&&Grid.GetColumn(text)==3)text.Foreground=Brush(current?"#86ccd4":"#a8b0b8");}}
        }
        private static void FocusEffect(Border border,bool focus){border.Effect=focus?new DropShadowEffect{Color=Color.FromRgb(93,182,192),BlurRadius=6,ShadowDepth=0,Opacity=.22}:null;}
        private void FlashKey(uint scan) {
            string name=null;switch(scan){case 0x4b:name="Back";break;case 0x4d:name="Forward";break;case 0x1e:name="NudgeAMinus";break;case 0x1f:name="NudgeAPlus";break;case 0x20:name="NudgeBMinus";break;case 0x21:name="NudgeBPlus";break;case 0x26:name="Link";break;case 0x25:name="Anchor";break;}
            if(name==null)return;var b=B(name);if(!b.IsEnabled)return;DispatcherTimer timer;if(flashes.TryGetValue(b,out timer))timer.Stop();
            b.RenderTransform=new TranslateTransform(0,1);b.Opacity=.78;timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(130)};flashes[b]=timer;timer.Tick+=(s,e)=>{timer.Stop();b.Opacity=1;b.RenderTransform=null;flashes.Remove(b);};timer.Start();
        }
    }
}
