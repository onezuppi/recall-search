<#
.SYNOPSIS
  Removes Recall Search for the current user. Your index and config are kept unless you pass -RemoveData.
#>
[CmdletBinding()]
param([switch]$RemoveData)

$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'Programs\RecallSearch'
$data = Join-Path $env:LOCALAPPDATA 'RecallSearch'

Get-Process -Name 'Recall.Agent' -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$target*" } | Stop-Process -Force
Get-Process -Name 'Recall.CmdPal' -ErrorAction SilentlyContinue | Stop-Process -Force
Get-AppxPackage -Name 'RecallSearch.CmdPal' -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'RecallSearch' -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Remove-Item -Recurse -Force $target -ErrorAction SilentlyContinue
Write-Host "Removed the program from $target."

if ($RemoveData) {
    Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
    Write-Host "Removed the index, config, models and log from $data."
} else {
    Write-Host "Kept your data in $data. Delete the folder by hand, or run again with -RemoveData."
}
Write-Host 'The optional services (change journal, Defender exclusion) are removed by their own scripts with -Remove.'
