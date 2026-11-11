// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace SyncSyncAV {
    public sealed partial class AppController {
        private string popupKey,lastClosedKey;private DateTime popupClosedAt;private bool closedByActivator;
        private List<HostState> knownHosts=new List<HostState>();private Task<List<HostState>> discoveryTask;
        private readonly Dictionary<Border,SyncPoint> pointRows=new Dictionary<Border,SyncPoint>();
        private Action refreshPointRows;private string renderedPointsKey;private Button savePointButton;
        private readonly List<ComboBox> popupCombos=new List<ComboBox>();
        private TextBlock scanNote,missingNote;private Button refreshButton;private ComboBox pickerA,pickerB;
        private bool TogglePopup(string key) {
            if(popup!=null&&popupKey==key){popup.Close();return true;}
            bool suppress=closedByActivator&&lastClosedKey==key&&(DateTime.UtcNow-popupClosedAt).TotalMilliseconds<350;closedByActivator=false;return suppress;
        }
        private Window Popup(string title,double width,string key) {
            if(popup!=null)popup.Close();popupCombos.Clear();pointRows.Clear();
            var w=new Window{Title=title,Owner=Window,Icon=Window.Icon,Width=width,SizeToContent=SizeToContent.Height,MaxHeight=360,Background=Brush("Transparent"),Foreground=Brush("#e3e6e9"),FontSize=12,ResizeMode=ResizeMode.NoResize,WindowStyle=WindowStyle.None,AllowsTransparency=true,ShowInTaskbar=false,Topmost=Window.Topmost};
            w.Resources.MergedDictionaries.Add(Window.Resources);w.SetResourceReference(Window.FontFamilyProperty,"UiFont");
            popup=w;popupKey=key;WatchNativeMessages(w);
            w.Loaded+=(s,e)=>PositionPopup();w.SizeChanged+=(s,e)=>PositionPopup();
            w.PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape&&!popupCombos.Any(c=>c.IsDropDownOpen)){e.Handled=true;w.Close();return;}OnKey(s,e);};
            w.Deactivated+=(s,e)=>w.Dispatcher.BeginInvoke(new Action(()=>{if(popup==w&&!w.IsActive&&!popupCombos.Any(c=>c.IsDropDownOpen)){var button=B(key);Point cursor=Mouse.GetPosition(button);closedByActivator=cursor.X>=0&&cursor.Y>=0&&cursor.X<=button.ActualWidth&&cursor.Y<=button.ActualHeight;w.Close();}}),DispatcherPriority.Background);
            w.Closed+=(s,e)=>{if(popup==w){lastClosedKey=key;popupClosedAt=DateTime.UtcNow;popup=null;popupKey=null;popupCombos.Clear();pointRows.Clear();Render();}};
            return w;
        }
        private void PositionPopup() {
            if(popup==null||!popup.IsLoaded||Window.ActualWidth==0)return;
            var source=PresentationSource.FromVisual(Window);if(source==null)return;
            var area=System.Windows.Forms.Screen.FromHandle(Hwnd).WorkingArea;var transform=source.CompositionTarget.TransformFromDevice;
            var tl=transform.Transform(new Point(area.Left,area.Top));var br=transform.Transform(new Point(area.Right,area.Bottom));
            var button=B(popupKey);var anchor=transform.Transform(button.PointToScreen(new Point(button.ActualWidth,0)));
            double anchorTop=Embedded?anchor.Y:Window.Top;
            popup.MaxHeight=Math.Max(120,Math.Min(360,anchorTop-tl.Y-16));
            double left=popupKey=="Points"?anchor.X-button.ActualWidth/2-popup.ActualWidth/2:anchor.X-popup.ActualWidth;
            double scale=VisualTreeHelper.GetDpi(Window).DpiScaleY;double height=Math.Ceiling(popup.ActualHeight*scale)/scale;
            popup.Left=Math.Max(tl.X,Math.Min(left,br.X-popup.ActualWidth));popup.Top=Math.Max(tl.Y+8,Math.Floor((anchorTop-height-8)*scale)/scale);
        }
        private StackPanel PanelBody(Window w,string title) {
            var frame=new Border{Background=Brush("#1c2024"),BorderBrush=Brush("#343a41"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8),Margin=new Thickness(5),Padding=new Thickness(14,9,14,10),Effect=new DropShadowEffect{Color=Colors.Black,BlurRadius=10,ShadowDepth=2,Opacity=.3}};
            var layout=new DockPanel{LastChildFill=true};frame.Child=layout;w.Content=frame;
            var header=new DockPanel{Margin=new Thickness(0,0,0,6)};DockPanel.SetDock(header,Dock.Top);layout.Children.Add(header);
            var close=ActionButton("✕",()=>w.Close());close.Width=26;close.Height=24;close.Margin=new Thickness(0);close.Padding=new Thickness(0);close.Style=(Style)Window.Resources["ToolButton"];AutomationProperties.SetName(close,Ui.Text("ClosePrefix")+title);DockPanel.SetDock(close,Dock.Right);header.Children.Add(close);
            header.Children.Add(new TextBlock{Text=title,FontSize=15,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center});
            var body=new StackPanel();layout.Children.Add(new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});return body;
        }
        private TextBlock Caption(string text){return new TextBlock{Text=text,Foreground=Brush("#79828b"),FontSize=11,Margin=new Thickness(0,3,0,3),TextWrapping=TextWrapping.Wrap};}
        private TextBlock Section(string text){return new TextBlock{Text=text,Foreground=Brush("#79828b"),FontSize=10.5,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,3,0,2)};}
        private Button ActionButton(string text,Action action){var b=new Button{Content=text,Height=26,FontSize=11.5,Margin=new Thickness(0,2,6,2),Padding=new Thickness(9,0,9,0)};b.Click+=(s,e)=>action();return b;}
        private ComboBox Picker(string name){var picker=new ComboBox{Height=32,Margin=new Thickness(0,0,0,2)};AutomationProperties.SetAutomationId(picker,name);popupCombos.Add(picker);return picker;}
        private void PickerRow(StackPanel parent,string label,ComboBox picker) {
            var row=new Grid();row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(80)});row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock{Text=label,FontSize=12,Foreground=Brush("#a8b0b8"),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,7,4)});Grid.SetColumn(picker,1);row.Children.Add(picker);parent.Children.Add(row);
        }
        public sealed class HostChoice {public HostState Host{get;set;}public string Name{get;set;}public string Detail{get;set;}public string Full{get;set;}}
        private HostChoice HostContent(HostState host,string side) {
            string name=String.IsNullOrEmpty(host.File)?Ui.Text("NoVideo"):Path.GetFileName(host.File);
            string mark=(side=="A"?Session.PidB:Session.PidA)==host.Pid?" · "+(side=="A"?"B":"A"):"";
            string directory=String.IsNullOrEmpty(host.File)?"":Path.GetDirectoryName(host.File);
            return new HostChoice{Host=host,Name=name+mark,Detail=directory+" · PID "+host.Pid,Full=host.File+" · PID "+host.Pid};
        }
        private void RefreshPickers() {
            if(popupKey!="Connections"||pickerA==null||pickerB==null)return;
            if(pickerA.IsDropDownOpen||pickerB.IsDropDownOpen)return;
            changingPickers=true;
            try {
                var hosts=knownHosts.Concat(new[]{Session.HostA,Session.HostB}.Where(h=>h!=null)).GroupBy(h=>h.Pid).Select(g=>g.First()).OrderBy(h=>h.Pid).ToList();
                foreach(var pair in new[]{Tuple.Create(pickerA,"A",Session.PidA),Tuple.Create(pickerB,"B",Session.PidB)}) {
                    pair.Item1.Items.Clear();foreach(var host in hosts){var item=HostContent(host,pair.Item2);pair.Item1.Items.Add(item);if(host.Pid==pair.Item3)pair.Item1.SelectedItem=item;}
                }
            }finally{changingPickers=false;}
        }
        private void GuardPicker(ComboBox picker) {
            bool accepted=false;
            picker.PreviewKeyDown+=(s,e)=>{
                if(!picker.IsDropDownOpen&&(e.Key==Key.Up||e.Key==Key.Down||e.Key==Key.Left||e.Key==Key.Right||e.Key==Key.Home||e.Key==Key.End||e.Key==Key.PageUp||e.Key==Key.PageDown)){e.Handled=true;return;}
                if(picker.IsDropDownOpen&&e.Key==Key.Enter)accepted=true;
                if(picker.IsDropDownOpen&&e.Key==Key.Escape)accepted=false;
            };
            picker.IsTextSearchEnabled=false;
            picker.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,new MouseButtonEventHandler((s,e)=>{if(FindParent<ComboBoxItem>(e.OriginalSource as DependencyObject)!=null)accepted=true;}),true);
            picker.DropDownOpened+=(s,e)=>accepted=false;
            picker.DropDownClosed+=(s,e)=>{if(changingPickers)return;if(accepted)CommitPickers();RefreshPickers();};
            picker.SelectionChanged+=(s,e)=>{if(!changingPickers&&!picker.IsDropDownOpen)CommitPickers();};
        }
        private int Picked(ComboBox picker){var item=picker.SelectedItem as HostChoice;return item==null?0:item.Host.Pid;}
        private void CommitPickers() {
            if(changingPickers||Working)return;int a=Picked(pickerA),b=Picked(pickerB);if(a==Session.PidA&&b==Session.PidB)return;
            LastAction=Run(Ui.Text("ConnectingWindows"),async()=>{await Session.SelectAsync(a,b,false,lifetime.Token);Session.Say(a==b&&a>0?Ui.Text("DuplicateSelection"):Ui.Text("WindowsSelected"),true);RefreshPickers();});
        }
        private async Task RefreshHosts(Window owner) {
            refreshButton.IsEnabled=false;scanNote.Text=Ui.Text("Scanning");
            try {
                if(discoveryTask==null||discoveryTask.IsCompleted)discoveryTask=Discover();
                var hosts=await discoveryTask;var bridgePids=await Task.Run(()=>BridgeClient.EnumeratePids());
                int missing=await Task.Run(()=>{int count=0;foreach(var process in Process.GetProcesses()){using(process){try{if((process.ProcessName.StartsWith("VirtualDub",StringComparison.OrdinalIgnoreCase)||process.ProcessName.Equals("vdub2",StringComparison.OrdinalIgnoreCase))&&process.MainWindowHandle!=IntPtr.Zero&&!bridgePids.Contains(process.Id))count++;}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}}}return count;});
                if(popup!=owner)return;RefreshPickers();scanNote.Text=Ui.Text("WindowsFound")+hosts.Count;
                missingNote.Text=missing==0?"":missing+" "+(missing==1?Ui.Text("WindowWithoutBridge"):Ui.Text("WindowsWithoutBridge"))+Ui.Text("SdkOpenHint");missingNote.Visibility=missing==0?Visibility.Collapsed:Visibility.Visible;
            }catch(OperationCanceledException){}catch(Exception error){if(popup==owner){scanNote.Text=Ui.Text("ScanIncomplete");scanNote.ToolTip=error.Message;}}finally{if(popup==owner)refreshButton.IsEnabled=true;}
        }
        public void OpenConnections() {
            if(TogglePopup("Connections"))return;
            var w=Popup(Ui.Text("Settings"),Embedded?410:530,"Connections");
            FillSettings(w,null);
            w.Show();Render();
        }
        private void FillSettings(Window w,string templateDraft) {
            popupCombos.Clear();
            w.Title=Ui.Text("Settings");
            var panel=PanelBody(w,w.Title);
            var language=Picker("Language");
            AutomationProperties.SetName(language,Ui.Text("Language"));
            language.Items.Add(new ComboBoxItem{Content="Русский",Tag="ru"});
            language.Items.Add(new ComboBoxItem{Content="English",Tag="en"});
            language.SelectedIndex=Ui.Code=="ru"?0:1;
            PickerRow(panel,Ui.Text("Language"),language);
            if(!Embedded)AddConnectionSettings(w,panel);
            var template=AddOffsetSettings(panel,templateDraft);
            if(Embedded)AddEmbeddedSettings(panel);
            language.SelectionChanged+=(s,e)=>{
                var selected=language.SelectedItem as ComboBoxItem;
                if(selected==null||(string)selected.Tag==Ui.Code)return;
                Ui.Select((string)selected.Tag);
                ApplyLanguageResources();
                if(LanguageChanged!=null)LanguageChanged();
                Session.Say(Ui.Text("LanguageChanged"));
                SaveData();
                FillSettings(w,template.Text);
            };
        }
        private void AddConnectionSettings(Window w,StackPanel panel) {
            panel.Children.Add(Section(Ui.Text("WindowsHeading")));pickerA=Picker("SelectA");pickerB=Picker("SelectB");
            var hostTemplate=(DataTemplate)XamlReader.Parse("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid ToolTip='{Binding Full}'><Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions><TextBlock Text='{Binding Name}' FontSize='12' MaxWidth='160' Foreground='#e3e6e9' VerticalAlignment='Center'/><TextBlock Grid.Column='1' Text='{Binding Detail}' FontFamily='{DynamicResource UiFont}' FontSize='10' Foreground='#79828b' Margin='9,0,0,0' TextTrimming='CharacterEllipsis' VerticalAlignment='Center'/></Grid></DataTemplate>");pickerA.ItemTemplate=hostTemplate;pickerB.ItemTemplate=hostTemplate;
            PickerRow(panel,Ui.Text("LeftWindow"),pickerA);PickerRow(panel,Ui.Text("RightWindow"),pickerB);
            var note=Caption(Ui.Text("WindowChangeUnlinks"));note.Margin=new Thickness(80,0,0,2);panel.Children.Add(note);
            var actions=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(80,0,0,0)};panel.Children.Add(actions);
            var swap=ActionButton(Ui.Text("SwapAction"),()=>{LastAction=Run(Ui.Text("Swapping"),async()=>{await Session.SwapAsync(lifetime.Token);if(Layout.CanRestore)await Layout.TileAsync(Hwnd,Session.HostA,Session.HostB,lifetime.Token);RefreshPickers();});});swap.ToolTip=Ui.Text("SwapLayoutHint");actions.Children.Add(swap);
            refreshButton=ActionButton(Ui.Text("RefreshList"),()=>{LastAction=RefreshHosts(w);});AutomationProperties.SetAutomationId(refreshButton,"RefreshHosts");actions.Children.Add(refreshButton);scanNote=Caption(Ui.Text("WindowsFound")+knownHosts.Count);scanNote.VerticalAlignment=VerticalAlignment.Center;scanNote.FontSize=10;actions.Children.Add(scanNote);
            missingNote=Caption("");missingNote.Foreground=Brush("#d4a14e");missingNote.Visibility=Visibility.Collapsed;panel.Children.Add(missingNote);
            panel.Children.Add(Section(Ui.Text("KeysHeading")));
            var global=new CheckBox{Content=Ui.Text("KeysInWindows"),IsChecked=GlobalKeys};AutomationProperties.SetAutomationId(global,"GlobalKeys");panel.Children.Add(global);global.Click+=(s,e)=>{GlobalKeys=global.IsChecked==true;UpdateKeys();SaveData();};
            var hints=new CheckBox{Content=Ui.Text("ShowButtonKeys"),IsChecked=Hints};panel.Children.Add(hints);hints.Click+=(s,e)=>{Hints=hints.IsChecked==true;Render();SaveData();};
            var stepRow=new DockPanel{Margin=new Thickness(0,3,0,0)};var step=Picker("BigStep");step.Height=28;step.Width=120;step.Margin=new Thickness(0);DockPanel.SetDock(step,Dock.Right);stepRow.Children.Add(step);stepRow.Children.Add(new TextBlock{Text=Ui.Text("ShiftStep"),VerticalAlignment=VerticalAlignment.Center});panel.Children.Add(stepRow);
            foreach(int value in new[]{5,10,24,100}){var item=new ComboBoxItem{Content=value+(value==24?Ui.Text("FramesFew"):Ui.Text("FramesUnit")),Tag=value};step.Items.Add(item);if(value==BigStep)step.SelectedItem=item;}step.SelectionChanged+=(s,e)=>{if(step.SelectedItem!=null){BigStep=(int)((ComboBoxItem)step.SelectedItem).Tag;SaveData();Render();}};
            panel.Children.Add(Section(Ui.Text("ControllerHeading")));var top=new CheckBox{Content=Ui.Text("AlwaysOnTop"),IsChecked=Window.Topmost};panel.Children.Add(top);top.Click+=(s,e)=>{Window.Topmost=top.IsChecked==true;w.Topmost=Window.Topmost;SaveData();};
            var help=ActionButton(Ui.Text("AllShortcuts"),()=>{});help.Style=(Style)Window.Resources["ToolButton"];help.HorizontalAlignment=HorizontalAlignment.Left;help.ToolTip=Ui.Text("WindowKeyGuide");help.Click+=(s,e)=>{ToolTipService.SetInitialShowDelay(help,0);var tooltip=new ToolTip{Content=help.ToolTip,PlacementTarget=help,IsOpen=true,StaysOpen=false};};panel.Children.Add(help);
            RefreshPickers();GuardPicker(pickerA);GuardPicker(pickerB);LastAction=RefreshHosts(w);
        }
        public void OpenPoints() {
            if(TogglePopup("Points"))return;var w=Popup(Ui.Text("SyncPoints"),460,"Points");var panel=PanelBody(w,Ui.Text("SyncPoints"));
            panel.Children.Add(Caption(Ui.Text("PointsHint")));
            savePointButton=ActionButton(Ui.Text("SavePoint"),()=>{LastAction=Apply("anchor");});AutomationProperties.SetAutomationId(savePointButton,"SavePoint");savePointButton.HorizontalAlignment=HorizontalAlignment.Left;panel.Children.Add(savePointButton);
            var table=new StackPanel{Margin=new Thickness(0,7,0,0)};panel.Children.Add(table);
            Action populate=null;populate=()=>{table.Children.Clear();pointRows.Clear();var grid=new Grid();foreach(double width in new[]{22,62,62,72,-1,64,26})grid.ColumnDefinitions.Add(new ColumnDefinition{Width=width<0?new GridLength(1,GridUnitType.Star):new GridLength(width)});
                grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});string[] headers={"№","A","B",ReverseOffset?"A − B":"B − A",Ui.Text("LabelHeading"),"",""};for(int col=0;col<headers.Length;col++){var text=new TextBlock{Text=headers[col],FontSize=10,Foreground=Brush("#79828b"),FontWeight=FontWeights.SemiBold,Margin=new Thickness(3,0,3,6)};Grid.SetColumn(text,col);grid.Children.Add(text);}
                int index=0;foreach(var point in Session.Points.ToArray()){index++;int row=index;grid.RowDefinitions.Add(new RowDefinition{Height=new GridLength(34)});var background=new Border{BorderBrush=Brush("#262b31"),BorderThickness=new Thickness(0,0,0,1),Background=Brush(Session.State.Linked&&point.A==Session.State.A&&point.B==Session.State.B?"#243237":"Transparent")};Grid.SetRow(background,row);Grid.SetColumnSpan(background,7);grid.Children.Add(background);pointRows[background]=point;
                    string[] values={index.ToString(),point.A.ToString(),point.B.ToString(),DisplayOffset(point.Offset),point.Label};for(int col=0;col<values.Length;col++){var text=new TextBlock{Text=values[col],ToolTip=values[col],FontFamily=(FontFamily)Window.Resources[col==4?"UiFont":"NumberFont"],FontSize=11.5,Foreground=Brush(col==3&&Session.State.Linked&&point.Offset==Session.State.Offset?"#86ccd4":"#a8b0b8"),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(3,0,3,0)};Grid.SetRow(text,row);Grid.SetColumn(text,col);grid.Children.Add(text);}
                    var recall=ActionButton(Ui.Text("Recall"),()=>{w.Close();LastAction=Run(Ui.Text("RecallingPoint"),()=>Session.RecallAsync(point,lifetime.Token));});AutomationProperties.SetAutomationId(recall,"RecallPoint"+index);recall.ToolTip=Ui.Text("GoToAPrefix")+point.A+Ui.Text("AndB")+point.B+Ui.Text("ThenLink")+DisplayOffset(point.Offset);recall.Padding=new Thickness(4,0,4,0);recall.Margin=new Thickness(0,4,3,4);Grid.SetRow(recall,row);Grid.SetColumn(recall,5);grid.Children.Add(recall);
                    var delete=ActionButton("✕",()=>{Session.DeletePoint(point);SaveData();populate();});delete.ToolTip=Ui.Text("DeletePoint")+index;delete.Padding=new Thickness(0);delete.Margin=new Thickness(0,4,0,4);Grid.SetRow(delete,row);Grid.SetColumn(delete,6);grid.Children.Add(delete);
                }table.Children.Add(grid);if(index==0)table.Children.Add(Caption(Ui.Text("NoPoints")));table.Children.Add(Caption(Ui.Text("PointsPersist")));};refreshPointRows=populate;renderedPointsKey=null;w.Closed+=(s,e)=>{refreshPointRows=null;savePointButton=null;};w.Show();Render();
        }
    }
}
