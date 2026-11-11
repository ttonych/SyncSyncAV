param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\build'))
$ErrorActionPreference = 'Stop'
$taskCsc = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
$taskFramework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$taskOut = (Resolve-Path -LiteralPath $OutputDirectory).Path
$taskSources = @('Ui.cs','BridgeClient.cs','SyncSession.cs','DiscMetadata.cs','Desktop.cs','AppController.cs','OffsetText.cs','OffsetControls.cs','Presentation.cs','Panels.cs','EmbeddedView.cs','IntegratedShell.cs','AppIcon.cs','Program.cs') | ForEach-Object {Join-Path $PSScriptRoot $_}
$taskRefs = @('WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll') | ForEach-Object { "/reference:$taskFramework\WPF\$_" }
& $taskCsc /nologo /target:winexe /platform:x64 /optimize+ /warn:4 /warnaserror+ /utf8output "/out:$taskOut\SyncSyncAV.exe" "/win32manifest:$PSScriptRoot\app.manifest" "/win32icon:$PSScriptRoot\Assets\SyncSyncAV.ico" "/resource:$PSScriptRoot\Assets\SyncSyncAV.ico,SyncSyncAV.ico" "/resource:$PSScriptRoot\MainWindow.xaml,MainWindow.xaml" "/resource:$PSScriptRoot\Strings.xml,Strings.xml" /reference:System.Xml.dll /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.Xaml.dll $taskRefs $taskSources
if ($LASTEXITCODE -ne 0) { throw 'Native controller build failed' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SyncSyncAV.exe.config') -Destination $taskOut -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Fonts') -Destination $taskOut -Recurse -Force
Write-Output (Join-Path $taskOut 'SyncSyncAV.exe')
