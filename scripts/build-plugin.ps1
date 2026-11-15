$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dalamud = Join-Path $env:APPDATA 'XIVLauncher\addon\Hooks\dev\Dalamud.dll'
if (-not (Test-Path -LiteralPath $dalamud)) {
    Write-Error "Dalamud.dll not found at $dalamud. Install XIVLauncher/Dalamud before building the plugin."
    exit 1
}

dotnet build (Join-Path $repo 'SlidecastExtender.Plugin.slnx') -c Release
exit $LASTEXITCODE
