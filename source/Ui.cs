// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using System.Globalization;
using System.Xml;

namespace SyncSyncAV {
    internal static class Ui {
        private static readonly Dictionary<string,string> russian = new Dictionary<string,string>();
        private static readonly Dictionary<string,string> english = new Dictionary<string,string>();

        private static readonly Dictionary<string,string> bridgeRussian = new Dictionary<string,string>();

        public static string Code { get; private set; }
        public static CultureInfo Culture {
            get { return CultureInfo.GetCultureInfo(Code == "ru" ? "ru-RU" : "en-US"); }
        }
        public static IReadOnlyDictionary<string,string> Strings {
            get { return Code == "ru" ? russian : english; }
        }

        static Ui() {
            var document = new XmlDocument { XmlResolver = null };
            using (var stream = typeof(Ui).Assembly.GetManifestResourceStream("Strings.xml")) {
                document.Load(stream);
            }
            foreach (XmlElement entry in document.DocumentElement.ChildNodes) {
                if (entry.Name == "bridge") {
                    bridgeRussian.Add(entry.GetAttribute("en"), entry.GetAttribute("ru"));
                    continue;
                }
                string key = entry.GetAttribute("key");
                russian.Add(key, entry.GetAttribute("ru"));
                english.Add(key, entry.GetAttribute("en"));
            }
            Select(null);
        }

        public static void Select(string code) {
            if (code != "ru" && code != "en") {
                code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";
            }
            Code = code;
        }

        public static string BridgeDetail(string message) {
            string translated;
            return Code == "ru" && bridgeRussian.TryGetValue(message, out translated) ? translated : message;
        }

        public static string Text(string key) { return Strings[key]; }
    }
}
