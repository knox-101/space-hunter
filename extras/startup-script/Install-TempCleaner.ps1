<#
  Install-TempCleaner.ps1 - run ONCE from an *Administrator* PowerShell.

  - Copies Clean-Temp.ps1 to %LOCALAPPDATA%\TempCleaner\
  - Registers a scheduled task "TempCleaner" that runs it hidden, elevated,
    2 minutes after you log on (elevation is needed for Windows\Temp + Prefetch)
  - Puts a "Clean Temp" shortcut on your Desktop that triggers the same task
    on demand (runs elevated with no UAC prompt)

  Uninstall:  .\Install-TempCleaner.ps1 -Uninstall
#>
param([switch]$Uninstall)

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
           [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Error 'Run this from PowerShell opened with "Run as administrator".'; exit 1 }

$taskName = 'TempCleaner'
$dir      = Join-Path $env:LOCALAPPDATA 'TempCleaner'
$script   = Join-Path $dir 'Clean-Temp.ps1'
$shortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Clean Temp.lnk'

if ($Uninstall) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item $shortcut -Force -ErrorAction SilentlyContinue
    Write-Output "Removed task and shortcut. Log/script kept in $dir (delete it manually if you like)."
    exit 0
}

New-Item -ItemType Directory -Force -Path $dir | Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot 'Clean-Temp.ps1') -Destination $script -Force

$user      = "$env:USERDOMAIN\$env:USERNAME"
$action    = New-ScheduledTaskAction -Execute 'powershell.exe' `
             -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`""
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User $user
$trigger.Delay = 'PT2M'
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
             -ExecutionTimeLimit (New-TimeSpan -Minutes 30) -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal `
    -Settings $settings -Description 'Empties Temp, Windows\Temp, Prefetch and Run history' -Force | Out-Null

$ws = New-Object -ComObject WScript.Shell
$lnk = $ws.CreateShortcut($shortcut)
$lnk.TargetPath  = "$env:SystemRoot\System32\schtasks.exe"
$lnk.Arguments   = "/run /tn $taskName"
$lnk.WindowStyle = 7   # minimized
$lnk.IconLocation = "$env:SystemRoot\System32\shell32.dll,31"
$lnk.Save()

Write-Output "Installed. Runs 2 min after each logon; double-click 'Clean Temp' on the Desktop to run now."
Write-Output "Log: $dir\log.txt"
