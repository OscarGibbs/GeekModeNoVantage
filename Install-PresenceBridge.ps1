$ErrorActionPreference = 'Stop'
$serviceName = 'GeekModeNoVantagePresence'
$buildDirectory = Join-Path $PSScriptRoot 'PresenceBridge\bin\Release\net8.0-windows'
$builtBridge = Join-Path $buildDirectory 'GeekModeNoVantage.PresenceBridge.exe'
$installDirectory = Join-Path $env:ProgramData 'GeekModeNoVantage\PresenceBridge'
$bridge = Join-Path $installDirectory 'GeekModeNoVantage.PresenceBridge.exe'

if (-not (Test-Path $builtBridge)) {
    throw "Presence bridge has not been built: $builtBridge"
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $serviceName | Out-Null
    Start-Sleep -Milliseconds 500
}

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
Copy-Item (Join-Path $buildDirectory '*') $installDirectory -Recurse -Force

& sc.exe create $serviceName binPath= "`"$bridge`"" start= auto obj= LocalSystem DisplayName= "Geek Mode No Vantage Presence Bridge" | Out-Null
& sc.exe description $serviceName "Read-only bridge for Lenovo SmartSense present/absent decision events." | Out-Null
Start-Service -Name $serviceName
Get-Service -Name $serviceName
