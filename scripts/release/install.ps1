<#
.SYNOPSIS
  Installs Recall Search for the current user from the release archive. No administrator rights, no .NET SDK.
.DESCRIPTION
  1. Copies the agent to %LOCALAPPDATA%\Programs\RecallSearch (the extension trusts only this exact path).
  2. Copies the Command Palette extension to %LOCALAPPDATA%\Programs\RecallSearch\CmdPal and registers it for this user.
  3. Starts the agent and adds it to the per-user autostart (HKCU Run key). Skip this with -NoAutostart.
  Needs Windows Developer Mode, because the extension package is not signed (loose registration).
  Run it again to update: it stops the old agent first and keeps your config and index.
#>
[CmdletBinding()]
param([switch]$NoAutostart)

$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA 'Programs\RecallSearch'
$cmdpal = Join-Path $target 'CmdPal'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

foreach ($part in 'agent', 'cmdpal') {
    if (-not (Test-Path (Join-Path $source $part))) { throw "Folder '$part' is missing next to install.ps1. Extract the whole archive first." }
}

$devMode = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Name AllowDevelopmentWithoutDevLicense -ErrorAction SilentlyContinue
if (-not $devMode -or $devMode.AllowDevelopmentWithoutDevLicense -ne 1) {
    throw 'Turn on Developer Mode first: Settings > System > For developers > Developer Mode. Then run this script again.'
}

Write-Host 'Stopping the running agent (if any)...'
Get-Process -Name 'Recall.Agent' -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$target*" } | Stop-Process -Force
Get-AppxPackage -Name 'RecallSearch.CmdPal' -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

Write-Host "Copying files to $target ..."
New-Item -ItemType Directory -Force $target | Out-Null
Remove-Item -Recurse -Force $cmdpal -ErrorAction SilentlyContinue
Copy-Item (Join-Path $source 'agent\*') $target -Recurse -Force
Copy-Item (Join-Path $source 'cmdpal') $cmdpal -Recurse -Force

Write-Host 'Registering the Command Palette extension...'
Add-AppxPackage -Register (Join-Path $cmdpal 'AppxManifest.xml')

if (-not $NoAutostart) {
    Set-ItemProperty -Path $runKey -Name 'RecallSearch' -Value ('"{0}"' -f (Join-Path $target 'Recall.Agent.exe'))
    Write-Host 'Autostart on sign-in: on (undo with uninstall.ps1).'
}

Start-Process -FilePath (Join-Path $target 'Recall.Agent.exe') -WindowStyle Hidden
Write-Host ''
Write-Host 'Done. Open Command Palette, run "Reload", then type "Recall Search".'
Write-Host 'The first scan starts now. Progress is in the extension settings.'
