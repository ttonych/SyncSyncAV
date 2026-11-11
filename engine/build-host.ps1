param([Parameter(Mandatory)][string]$Checkout, [string]$YasmDirectory)
$ErrorActionPreference='Stop'
$tree=(Resolve-Path -LiteralPath $Checkout).Path
if ($YasmDirectory) { $env:PATH=$YasmDirectory+';'+$env:PATH }
Get-Command yasm.exe -ErrorAction Stop | Out-Null
$env:DXSDK_DIR=Join-Path $tree 'directx\'
$env:_LINK_=($env:_LINK_+' /PDBALTPATH:%_PDB%').Trim()
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild=& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\MSBuild.exe' | Select-Object -First 1
if (!$msbuild) {throw 'Install Visual Studio Build Tools with the C++ workload.'}
# Asuka includes revision.h before the VirtualDub project runs its pre-build event.
if (Test-Path -LiteralPath (Join-Path $tree '.git')) { & (Join-Path $tree 'update_revision.cmd') }
if (!(Test-Path -LiteralPath (Join-Path $tree 'revision.h'))) {throw 'Missing upstream revision metadata'}
& $msbuild (Join-Path $tree 'VirtualDub2.sln') /t:VirtualDub2 /p:Configuration=Release /p:Platform=x64 /m:4 /verbosity:minimal /nologo
if ($LASTEXITCODE) {throw 'VirtualDub2 build failed.'}
Write-Output (Join-Path $tree '_out\Release_x64\VirtualDub2.exe')
