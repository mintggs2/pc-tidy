# Made-up "latency pack" for PC Tidy's tests: it changes Windows first and only then makes a restore point.
Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\PriorityControl' -Name Win32PrioritySeparation -Value 38 -Type DWord
bcdedit /set disabledynamictick yes
Checkpoint-Computer -Description 'Latency pack' -RestorePointType MODIFY_SETTINGS
Set-Content -Path '{MARKER}' -Value 'this file proves the script ran'
