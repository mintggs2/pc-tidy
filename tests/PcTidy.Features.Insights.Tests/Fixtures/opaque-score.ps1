# Made-up "PC score" booster for PC Tidy's tests. Nothing here is real.
$score = Get-Random -Minimum 870 -Maximum 990
Set-ItemProperty -Path 'HKCU:\Control Panel\Desktop' -Name MenuShowDelay -Value 0
Write-Host "Optimizing your PC..."
Write-Host "Your PC Score: $score/1000" -ForegroundColor Green
Set-Content -Path '{MARKER}' -Value 'this file proves the script ran'
