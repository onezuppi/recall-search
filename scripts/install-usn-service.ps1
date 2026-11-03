<#
.SYNOPSIS
  Installs the optional Recall Search change-journal service, or removes it with -Remove.
.NOTES
  Run from an elevated PowerShell. Without this service the agent still works: it skips unchanged folders by their
  last-write time and does a full walk once a week. With it, the agent also learns which files were overwritten in place
  while it was off, and the weekly full walk is no longer needed.

  What is installed: Recall.UsnService.exe in %ProgramFiles%\RecallSearch\Journal (writable by administrators only) and a
  scheduled task "RecallSearch.Journal" that runs it as SYSTEM at startup. The service reads the NTFS change journal and
  answers over the named pipe RecallSearch.Journal. It keeps no data. A signed-in user gets only paths under folders that
  user can list. The agent accepts answers only from the exe at the path above.
#>
[CmdletBinding()]
param([switch]$Remove, [string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$admin = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $admin.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell (Run as administrator)."
}

$taskName = 'RecallSearch.Journal'
$target = Join-Path $env:ProgramFiles 'RecallSearch\Journal'
$exe = Join-Path $target 'Recall.UsnService.exe'

function Stop-Service-Process {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Get-Process -Name 'Recall.UsnService' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
}

if ($Remove) {
    Stop-Service-Process
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $target -ErrorAction SilentlyContinue
    Write-Host "Removed the change-journal service."
    return
}

$repo = Split-Path -Parent $PSScriptRoot
Stop-Service-Process
dotnet publish (Join-Path $repo 'src\Recall.UsnService') -c $Configuration -o $target
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

$action = New-ScheduledTaskAction -Execute $exe
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 5 `
    -RestartInterval (New-TimeSpan -Minutes 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $taskName
Write-Host "Installed and started. Restart the Recall agent so it picks the service up."
