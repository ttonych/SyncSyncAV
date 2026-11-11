param([Parameter(Mandatory)][string]$Checkout, [string]$YasmDirectory)
$ErrorActionPreference='Stop'
$tree=(Resolve-Path -LiteralPath $Checkout).Path
if ($YasmDirectory) { $env:PATH=$YasmDirectory+';'+$env:PATH }
Get-Command yasm.exe -ErrorAction Stop | Out-Null
$env:DXSDK_DIR=Join-Path $tree 'directx\'
$env:_LINK_=($env:_LINK_+' /PDBALTPATH:%_PDB%').Trim()
$vs = & (Join-Path $PSScriptRoot '../tools/find-vs.ps1') -Requires 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64'
$msbuild = Join-Path $vs 'MSBuild/Current/Bin/MSBuild.exe'
# Asuka includes revision.h before the VirtualDub project runs its pre-build event.
if (Test-Path -LiteralPath (Join-Path $tree '.git')) { & (Join-Path $tree 'update_revision.cmd') }
if (!(Test-Path -LiteralPath (Join-Path $tree 'revision.h'))) {throw 'Missing upstream revision metadata'}
& $msbuild (Join-Path $tree 'VirtualDub2.sln') /t:VirtualDub2 /p:Configuration=Release /p:Platform=x64 /m:4 /verbosity:minimal /nologo
if ($LASTEXITCODE) {throw 'VirtualDub2 build failed.'}
Write-Output (Join-Path $tree '_out\Release_x64\VirtualDub2.exe')
