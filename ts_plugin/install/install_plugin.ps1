$ErrorActionPreference = 'Stop'

$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$PluginName  = 'ts_voice_forwarder'
$DllSrc      = Join-Path $ScriptDir "$PluginName.dll"
$CfgTemplate = Join-Path $ScriptDir "$PluginName.cfg"
$Ts3PluginDir = Join-Path $env:APPDATA 'TS3Client\plugins'
$DllDst      = Join-Path $Ts3PluginDir "$PluginName.dll"
$CfgDst      = Join-Path $Ts3PluginDir "$PluginName.cfg"

if (-not (Test-Path $DllSrc)) {
    $DllSrc = Join-Path $ScriptDir "..\out\$PluginName.dll"
}
if (-not (Test-Path $CfgTemplate)) {
    $CfgTemplate = Join-Path $ScriptDir "..\ts_voice_forwarder.cfg.example"
}

if (-not (Test-Path $DllSrc)) {
    Write-Host 'INSTALL_FAIL' -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $Ts3PluginDir)) {
    New-Item -ItemType Directory -Path $Ts3PluginDir -Force | Out-Null
}

Copy-Item -Path $DllSrc -Destination $DllDst -Force

if (-not (Test-Path $CfgDst) -and (Test-Path $CfgTemplate)) {
    Copy-Item -Path $CfgTemplate -Destination $CfgDst -Force
}

Write-Host 'INSTALL_OK' -ForegroundColor Green
exit 0
