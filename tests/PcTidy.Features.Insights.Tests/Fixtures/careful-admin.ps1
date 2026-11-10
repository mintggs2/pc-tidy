<#
  Made-up admin script for PC Tidy's tests (none of the false-alarm rules should fire).
  For more info see the team wiki. The settings function is written first but called after the restore point.
#>
$ErrorActionPreference = 'Stop'

function Set-TeamSettings {
    Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name HideFileExt -Value 0 -Type DWord
    Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa' -Name RunAsPPL -Value 1 -Type DWord
    Set-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate' -Name DeferQualityUpdatesPeriodInDays -Value 0 -Type DWord
    Set-MpPreference -DisableRealtimeMonitoring $false
}

function New-SafetyPoint {
    Enable-ComputerRestore -Drive "C:\"
    Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore' -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord
    Checkpoint-Computer -Description 'Before team settings' -RestorePointType MODIFY_SETTINGS
}

New-SafetyPoint
Set-TeamSettings

$status = Invoke-RestMethod -Uri 'https://status.example.invalid/api/health'
Write-Host "Service version: $($status.version)"
Write-Host "Disk C: 85% used"
Write-Host "Backup progress: 100%"
Write-Progress -Activity 'Copying' -PercentComplete 50
Write-Host "Line one`nLine two`tdone"
Write-Host 'Install all updates from Settings > Windows Update when you are ready.'
attrib +r +s "C:\Users\Public\Team\desktop.ini"
Get-PnpDevice -FriendlyName 'Example USB Receiver' | Enable-PnpDevice -Confirm:$false
$hashes = @('3f786850e387550fdab836ed7e6dc881de23001b', '89e6c98d92887913cadf06b2adb97f26cde4849b', '2b66fd261ee5c6cfc8de7fa466bab3e6a4e9b4d0')
$all = $hashes -join ','
Set-Content -Path '{MARKER}' -Value 'this file proves the script ran'
