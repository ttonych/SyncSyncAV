param(
    [Parameter(Mandatory)][string]$WorkDirectory,
    [Parameter(Mandatory)][string]$RuntimeDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$YasmDirectory,
    [string]$HostMirror,
    [string]$DecoderMirror
)
$ErrorActionPreference='Stop'
$work=(New-Item -ItemType Directory -Force -Path $WorkDirectory).FullName
$pins=Get-Content -Raw (Join-Path $PSScriptRoot 'engine/upstream.json') | ConvertFrom-Json
$trees=@{}
foreach ($component in 'host','decoder') {
    $patch=Join-Path $PSScriptRoot "engine/patches/$component.patch"
    $patchId=(Get-FileHash $patch).Hash.Substring(0,12)
    $pin=$pins.$component.revision.Substring(0,12)
    $tree=Join-Path $work "$component-$pin-$patchId"
    if (!(Test-Path -LiteralPath $tree)) {
        $mirror=if ($component -eq 'host') {$HostMirror} else {$DecoderMirror}
        $extra=if ($mirror) {@('--mirror',$mirror)}
        & python (Join-Path $PSScriptRoot 'engine/prepare.py') $component $tree @extra
        if ($LASTEXITCODE) {throw "Could not prepare $component"}
    }
    $trees[$component]=$tree
}
& (Join-Path $PSScriptRoot 'engine/build-host.ps1') -Checkout $trees.host -YasmDirectory $YasmDirectory
& (Join-Path $PSScriptRoot 'engine/build-decoder.ps1') -Checkout $trees.decoder -RuntimeDirectory $RuntimeDirectory
$app=Join-Path $work 'app'
$bridge=Join-Path $work 'bridge-build'
& (Join-Path $PSScriptRoot 'engine/bridge/build.ps1') -OutputDirectory $bridge
& (Join-Path $PSScriptRoot 'source/build.ps1') -OutputDirectory $app
& (Join-Path $PSScriptRoot 'tests/test.ps1') -OutputDirectory (Join-Path $work 'unit')
& python (Join-Path $PSScriptRoot 'tools/release.py') --app $app --runtime $RuntimeDirectory --bridge $bridge --host $trees.host --decoder $trees.decoder --output $OutputDirectory
if ($LASTEXITCODE) {throw 'Release verification failed'}
