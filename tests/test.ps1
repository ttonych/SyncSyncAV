param([string]$OutputDirectory=(Join-Path $PSScriptRoot '..\validation'))
$ErrorActionPreference='Stop'
$vs = & (Join-Path $PSScriptRoot '../tools/find-vs.ps1')
$taskCsc = Join-Path $vs 'MSBuild/Current/Bin/Roslyn/csc.exe'
$taskRoot=Split-Path $PSScriptRoot -Parent
$taskOutput=(New-Item -ItemType Directory -Force -Path $OutputDirectory).FullName
& $taskCsc /nologo /target:exe /platform:x64 /warn:4 /warnaserror+ "/resource:$taskRoot\source\Strings.xml,Strings.xml" /reference:System.Xml.dll /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll "/out:$taskOutput\SessionTests.exe" "$taskRoot\source\Ui.cs" "$taskRoot\source\BridgeClient.cs" "$taskRoot\source\DiscMetadata.cs" "$taskRoot\source\SyncSession.cs" "$PSScriptRoot\SessionTests.cs"
if($LASTEXITCODE -ne 0){throw 'Compile tests failed'}
& "$taskOutput\SessionTests.exe" "$taskOutput\session-tests.json"
if($LASTEXITCODE -ne 0){throw 'Tests failed'}
& $taskCsc /nologo /target:exe /platform:x64 /warn:4 /warnaserror+ "/resource:$taskRoot\source\Strings.xml,Strings.xml" /reference:System.Xml.dll /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll "/out:$taskOutput\TransportTests.exe" "$taskRoot\source\Ui.cs" "$taskRoot\source\BridgeClient.cs" "$taskRoot\source\DiscMetadata.cs" "$PSScriptRoot\TransportTests.cs"
if($LASTEXITCODE -ne 0){throw 'Compile transport tests failed'}
& "$taskOutput\TransportTests.exe" "$taskOutput\transport-tests.json"
if($LASTEXITCODE -ne 0){throw 'Transport tests failed'}
