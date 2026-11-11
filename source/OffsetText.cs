// SPDX-License-Identifier: MIT
using System;
using System.Globalization;

namespace SyncSyncAV {
    public static class OffsetText {
        public const string DefaultTemplate="({frames}frame)({ms}ms)";
        public static decimal Frames(long canonical,bool reverse){return reverse?-(decimal)canonical:canonical;}
        public static string Signed(decimal value,bool display=false){return (value>0?"+":value<0?(display?"−":"-"):(display?"±":""))+Math.Abs(value).ToString("0",CultureInfo.InvariantCulture);}
        public static string ValidateTemplate(string template) {
            if(String.IsNullOrWhiteSpace(template)||template.Length>256)return Ui.Text("TemplateLength");
            string rest=template.Replace("{frames}","").Replace("{ms}","");
            if(rest.IndexOfAny(new[]{'{','}'})>=0)return Ui.Text("TemplateTokens");
            if(rest==template)return Ui.Text("TemplateNeedsValue");
            return null;
        }
        public static bool TryFormat(string template,decimal frames,decimal? milliseconds,out string text,out string error) {
            text=null;error=ValidateTemplate(template);if(error!=null)return false;
            if(template.Contains("{ms}")&&!milliseconds.HasValue){error=Ui.Text("TemplateNeedsFps");return false;}
            text=template.Replace("{frames}",Signed(frames)).Replace("{ms}",milliseconds.HasValue?Signed(milliseconds.Value):"");
            return true;
        }
    }
}
