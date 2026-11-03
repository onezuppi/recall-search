<#
.SYNOPSIS
  Publishes the Recall Search agent to %LOCALAPPDATA%\Programs\RecallSearch and starts it.
.NOTES
  The Command Palette extension only trusts an agent running from exactly this path.
#>
[CmdletBinding()]
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA 'Programs\RecallSearch'

Get-Process -Name 'Recall.Agent' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $target 'Recall.Agent.exe') } |
    Stop-Process -Force

dotnet publish (Join-Path $repo 'src\Recall.Agent') -c $Configuration -o $target
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

Start-Process -FilePath (Join-Path $target 'Recall.Agent.exe') -WindowStyle Hidden
Write-Host "Agent started from $target"
