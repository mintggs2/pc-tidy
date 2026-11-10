# Made-up leftovers of "FPS guides" for PC Tidy's tests: each line writes something the bad-tweak finder resets.
reg add "HKLM\SYSTEM\CurrentControlSet\Control\Lsa" /v RunAsPPL /t REG_DWORD /d 0 /f
Set-MpPreference -DisableBehaviorMonitoring $true -DisableScriptScanning $true
Set-MpPreference -MAPSReporting 0
reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate" /v PauseFeatureUpdatesStartTime /t REG_SZ /d "2026-11-10T00:00:00Z" /f
reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate" /v DeferFeatureUpdatesPeriodInDays /t REG_DWORD /d 365 /f
bcdedit /set {current} safeboot minimal
reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\examplegame.exe\PerfOptions" /v CpuPriorityClass /t REG_DWORD /d 4 /f
reg add "HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers" /v "C:\Games\Example\examplegame.exe" /t REG_SZ /d "~ RUNASADMIN" /f
attrib +r "%LOCALAPPDATA%\ExampleGame\Saved\Config\WindowsClient\GameUserSettings.ini"
powercfg -import "C:\Packs\example-plan.pow"
pnputil /disable-device "ACPI\PNP0103\0"
Get-PnpDevice -FriendlyName 'High precision event timer' | Disable-PnpDevice -Confirm:$false
Set-Content -Path '{MARKER}' -Value 'this file proves the script ran'
