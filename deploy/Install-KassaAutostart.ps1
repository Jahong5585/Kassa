[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$manage = Join-Path (Split-Path -Parent $PSCommandPath) 'Manage-Kassa.ps1'
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument (
    '-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "{0}" -Action start' -f $manage)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 1) `
    -RestartCount 6 -RestartInterval (New-TimeSpan -Minutes 1)
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName 'KassaServer' -Action $action -Trigger $trigger -Settings $settings `
    -Principal $principal -Description 'Starts Kassa API, PostgreSQL, Keycloak, ClamAV and local gateway after sign-in.' `
    -Force | Out-Null
Write-Output 'Scheduled task KassaServer installed for the current user.'
