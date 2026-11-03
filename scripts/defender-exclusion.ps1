<#
.SYNOPSIS
  Adds the installed Recall Search agent to the Microsoft Defender process exclusions, or removes it with -Remove.
.NOTES
  Run from an elevated PowerShell. Defender then skips real-time scanning of files that this process opens (the agent
  opens every new or changed file to hash it). Only the process is excluded, no folders. The exclusion is bound to the
  exact path, so run it again after moving the agent. Nothing here runs unless you start it.
#>
[CmdletBinding()]
param(
    [string]$AgentPath = (Join-Path $env:LOCALAPPDATA 'Programs\RecallSearch\Recall.Agent.exe'),
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$admin = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $admin.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell (Run as administrator)."
}

if ($Remove) {
    Remove-MpPreference -ExclusionProcess $AgentPath
    Write-Host "Removed the Defender exclusion for $AgentPath"
}
else {
    if (-not (Test-Path $AgentPath)) { throw "Agent not found at $AgentPath. Run install-agent.ps1 first or pass -AgentPath." }
    Add-MpPreference -ExclusionProcess $AgentPath
    Write-Host "Added the Defender exclusion for $AgentPath"
}
