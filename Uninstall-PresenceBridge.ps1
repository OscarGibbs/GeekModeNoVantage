$serviceName = 'GeekModeNoVantagePresence'
Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
& sc.exe delete $serviceName
Remove-Item 'HKLM:\SOFTWARE\GeekModeNoVantage\Presence' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:ProgramData 'GeekModeNoVantage\PresenceBridge') -Recurse -Force -ErrorAction SilentlyContinue
