param([string]$Requires = 'Microsoft.Component.MSBuild')
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products '*' -version '[17.0,18.0)' -requires $Requires -property installationPath
if (!$installation -or $LASTEXITCODE) { throw "Visual Studio 2022 component missing: $Requires" }
$installation
