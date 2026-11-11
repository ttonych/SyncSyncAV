param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\build'))
$ErrorActionPreference = 'Stop'
$taskVs = & (Join-Path $PSScriptRoot '../../tools/find-vs.ps1') -Requires 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64'
$taskMsvc = Get-ChildItem -LiteralPath (Join-Path $taskVs 'VC\Tools\MSVC') -Directory | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1
$taskSdk = 'C:\Program Files (x86)\Windows Kits\10'
$taskSdkVersion = Get-ChildItem -LiteralPath (Join-Path $taskSdk 'Include') -Directory |
    Where-Object {
        ($_.Name -as [version]) -and
        (Test-Path -LiteralPath (Join-Path $taskSdk "Lib\$($_.Name)\ucrt\x64\ucrt.lib")) -and
        (Test-Path -LiteralPath (Join-Path $taskSdk "Lib\$($_.Name)\um\x64\user32.lib"))
    } | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1
if (-not $taskSdkVersion) { throw 'Windows SDK with x64 UCRT and UM libraries not found.' }
$env:INCLUDE = (@((Join-Path $taskMsvc.FullName 'include')) + @('ucrt','shared','um','winrt' | ForEach-Object { Join-Path $taskSdk "Include\$($taskSdkVersion.Name)\$_" })) -join ';'
$env:LIB = (@((Join-Path $taskMsvc.FullName 'lib\x64')) + @('ucrt','um' | ForEach-Object { Join-Path $taskSdk "Lib\$($taskSdkVersion.Name)\$_\x64" })) -join ';'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$taskOut = (Resolve-Path -LiteralPath $OutputDirectory).Path
$taskCl = Join-Path $taskMsvc.FullName 'bin\Hostx64\x64\cl.exe'
& $taskCl /nologo /std:c++17 /EHsc /W4 /WX /O2 /MT /LD /utf-8 /DUNICODE /D_UNICODE (Join-Path $PSScriptRoot 'control_bridge.cpp') "/Fo:$taskOut\control_bridge.obj" /link user32.lib advapi32.lib comctl32.lib "/OUT:$taskOut\control-bridge.vdplugin" "/IMPLIB:$taskOut\control-bridge.lib" "/PDB:$taskOut\control-bridge.pdb"
if ($LASTEXITCODE -ne 0) { throw 'Control bridge build failed' }
Write-Output (Join-Path $taskOut 'control-bridge.vdplugin')
