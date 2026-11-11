// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Markup;
[assembly:AssemblyProduct("SyncSyncAV")]
[assembly:AssemblyTitle("SyncSyncAV")]
[assembly:AssemblyVersion("0.1.0.0")]
[assembly:AssemblyFileVersion("0.1.0.0")]
namespace SyncSyncAV {
 public static class Program {
  [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
  public static string Option(string[] args,string key,string fallback=null){int i=Array.IndexOf(args,key);return i>=0&&i+1<args.Length?args[i+1]:fallback;}
  [STAThread] public static void Main(string[] args) {
   SetProcessDPIAware();
   try {
    var app=new Application();app.ShutdownMode=ShutdownMode.OnMainWindowClose;
    Window window;using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))window=(Window)XamlReader.Load(stream);
    string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SyncSyncAV");
    string data=Option(args,"--data",Path.Combine(folder,"settings.json"));
    string engine=Option(args,"--engine",Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Engine","VirtualDub2.exe"));
    var shell=new IntegratedShell(window,engine,data,Option(args,"--log",Path.Combine(folder,"session.jsonl")));
    app.Run(window);GC.KeepAlive(shell);
   }catch(Exception error){MessageBox.Show(error.ToString(),Ui.Text("StartupError"),MessageBoxButton.OK,MessageBoxImage.Error);}
  }
 }
}
