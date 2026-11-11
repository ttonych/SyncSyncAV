// SPDX-License-Identifier: MIT
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SyncSyncAV {
    public sealed partial class AppController {
        public bool ReverseOffset=true;
        public string OffsetCopyTemplate=OffsetText.DefaultTemplate;
        public string OffsetFormula {get{return ReverseOffset?"A = B":"B = A";}}
        public decimal DisplayOffsetValue {get{return OffsetText.Frames(Session.State.Offset,ReverseOffset);}}
        public string DisplayOffset(long canonical){return OffsetText.Signed(OffsetText.Frames(canonical,ReverseOffset),true);}
        private decimal? OffsetMilliseconds(long canonical) {
            var a=Meta(Session.HostA);var b=Meta(Session.HostB);
            if(a==null||b==null||a.Fps<=0||b.Fps<=0||a.FpsNumerator<=0||a.FpsDenominator<=0||Math.Abs(a.Fps-b.Fps)>0.0001)return null;
            decimal ms=a.RoundedMilliseconds(canonical);return ReverseOffset?-ms:ms;
        }
        public void ToggleOffsetDirection() {
            if(Working)return;
            FocusController();ReverseOffset=!ReverseOffset;renderedPointsKey=null;
            SaveData();Session.Say(Ui.Text("OffsetMode")+OffsetFormula+" "+DisplayOffset(Session.State.Offset)+Ui.Text("FramesRetained"));Render();
        }
        public bool TryGetOffsetCopyText(out string text,out string error) {
            text=null;
            if(!Session.PairReady){error=Ui.Text("ChooseOffset");return false;}
            return OffsetText.TryFormat(OffsetCopyTemplate,DisplayOffsetValue,OffsetMilliseconds(Session.State.Offset),out text,out error);
        }
        public async Task CopyOffsetAsync() {
            if(Working)return;
            string text,error;if(!TryGetOffsetCopyText(out text,out error)){Session.Say(error,true);return;}
            // The clipboard can be briefly held by another desktop application.
            for(int attempt=0;attempt<4;attempt++) {
                try{Clipboard.SetText(text);Session.Say(Ui.Text("Copied")+text);return;}
                catch(ExternalException){if(attempt==3){Session.Say(Ui.Text("ClipboardBusy"),true);return;}await Task.Delay(60);}
            }
        }
        public bool SetOffsetTemplate(string template,out string error) {
            error=OffsetText.ValidateTemplate(template);if(error!=null)return false;
            OffsetCopyTemplate=template;SaveData();Render();return true;
        }
        private TextBox AddOffsetSettings(StackPanel panel,string draft) {
            panel.Children.Add(Section(Ui.Text("CopySettingsHeading")));
            panel.Children.Add(Caption(Ui.Text("CopyTemplateHint")));
            var edit=new TextBox{Text=draft??OffsetCopyTemplate,Height=32,MaxLength=256,FontSize=12,FontFamily=(FontFamily)Window.Resources["NumberFont"],TextAlignment=TextAlignment.Left,VerticalContentAlignment=VerticalAlignment.Center,Padding=new Thickness(7,3,7,3),BorderThickness=new Thickness(1),BorderBrush=Brush("#343a41"),Background=Brush("#0f1215"),Foreground=Brush("#e3e6e9")};
            AutomationProperties.SetAutomationId(edit,"OffsetCopyTemplate");AutomationProperties.SetName(edit,Ui.Text("CopyTemplateName"));panel.Children.Add(edit);
            panel.Children.Add(Caption(Ui.Text("CopyTokensHint")));
            var preview=Caption("");preview.FontFamily=(FontFamily)Window.Resources["NumberFont"];AutomationProperties.SetAutomationId(preview,"OffsetCopyPreview");panel.Children.Add(preview);
            var actions=new StackPanel{Orientation=Orientation.Horizontal};panel.Children.Add(actions);
            Button save=null;
            Action refresh=()=>{
                string error=OffsetText.ValidateTemplate(edit.Text);save.IsEnabled=error==null&&edit.Text!=OffsetCopyTemplate;
                string example,formatError;
                decimal frames=Session.PairReady?DisplayOffsetValue:OffsetText.Frames(361,ReverseOffset);
                decimal? ms=Session.PairReady?OffsetMilliseconds(Session.State.Offset):(ReverseOffset?-15057m:15057m);
                bool valid=OffsetText.TryFormat(edit.Text,frames,ms,out example,out formatError);
                preview.Text=valid?Ui.Text("Example")+example:formatError;preview.ToolTip=preview.Text;preview.Foreground=Brush(valid?"#a8b0b8":"#d4a14e");
            };
            Action commit=()=>{string error;if(SetOffsetTemplate(edit.Text,out error)){refresh();Session.Say(Ui.Text("TemplateSaved"));}else{preview.Text=error;preview.Foreground=Brush("#d4a14e");}};
            save=ActionButton(Ui.Text("Save"),commit);AutomationProperties.SetAutomationId(save,"SaveOffsetTemplate");actions.Children.Add(save);
            var reset=ActionButton(Ui.Text("UseDefault"),()=>{edit.Text=OffsetText.DefaultTemplate;commit();});AutomationProperties.SetAutomationId(reset,"ResetOffsetTemplate");actions.Children.Add(reset);
            edit.TextChanged+=(s,e)=>refresh();edit.PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Enter){commit();e.Handled=true;}};refresh();
            return edit;
        }
    }
}
