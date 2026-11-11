param(
    [Parameter(Mandatory=$true)][string]$Checkout,
    [Parameter(Mandatory=$true)][string]$RuntimeDirectory,
    [string]$VisualStudioDirectory
)
$ErrorActionPreference = 'Stop'
$sourceRoot = (Resolve-Path -LiteralPath $Checkout).Path
if (!$VisualStudioDirectory) {
    $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $VisualStudioDirectory=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
$env:_LINK_=($env:_LINK_+' /PDBALTPATH:%_PDB%').Trim()
if (Test-Path -LiteralPath (Join-Path $sourceRoot '.git')) { & (Join-Path $sourceRoot 'update_revision.cmd') }
if (!(Test-Path -LiteralPath (Join-Path $sourceRoot 'revision.h'))) {throw 'Missing decoder revision metadata'}
$msvcRoot = Join-Path $VisualStudioDirectory 'VC\Tools\MSVC'
$msvcVersion = Get-ChildItem -LiteralPath $msvcRoot -Directory | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1
if (-not $msvcVersion) { throw 'MSVC tools not found' }
$toolDirectory = Join-Path $msvcVersion.FullName 'bin\Hostx64\x64'
$libraryDirectory = Join-Path $sourceRoot 'ffmpeg\lib_x64'
New-Item -ItemType Directory -Path $libraryDirectory -Force | Out-Null
$libraries = @{'avcodec'=62; 'avformat'=62; 'avutil'=60; 'swscale'=9; 'swresample'=6}
foreach ($libraryName in $libraries.Keys) {
    $dllName = "$libraryName-$($libraries[$libraryName]).dll"
    $exports = & (Join-Path $toolDirectory 'dumpbin.exe') /nologo /exports (Join-Path $RuntimeDirectory $dllName)
    if ($LASTEXITCODE -ne 0) { throw "dumpbin failed: $dllName" }
    $names = @($exports | ForEach-Object {
        if ($_ -match '^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+(\w+)') { $Matches[1] }
    })
    if ($names.Count -eq 0) { throw "No exports: $dllName" }
    $definition = Join-Path $libraryDirectory "$libraryName.def"
    @("LIBRARY $dllName", 'EXPORTS') + $names | Set-Content -LiteralPath $definition -Encoding ascii
    & (Join-Path $toolDirectory 'lib.exe') /nologo /machine:x64 "/def:$definition" "/out:$(Join-Path $libraryDirectory "$libraryName.lib")"
    if ($LASTEXITCODE -ne 0) { throw "lib failed: $dllName" }
}
$msbuild = Join-Path $VisualStudioDirectory 'MSBuild\Current\Bin\MSBuild.exe'
& $msbuild (Join-Path $sourceRoot 'avlib_vdplugin.sln') /m:2 /p:Configuration=Release /p:Platform=x64 /p:PreBuildEventUseInBuild=false /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Output (Join-Path $sourceRoot '_bin\Release_x64\avlib-1.vdplugin')
