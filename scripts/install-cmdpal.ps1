<#
.SYNOPSIS
  Builds the Recall Search Command Palette extension and registers it for the current user (loose registration).
.NOTES
  Needs Windows Developer Mode. After registering, open Command Palette and run "Reload".
  The extension talks only to the agent installed by install-agent.ps1.
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'ARM64' } else { 'x64' }),
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\Recall.CmdPal'

dotnet build $project -c $Configuration "-p:Platform=$Platform"
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$manifest = Get-ChildItem -Path (Join-Path $project "bin\$Platform\$Configuration") -Filter AppxManifest.xml -Recurse |
    Select-Object -First 1
if (-not $manifest) { throw "AppxManifest.xml was not produced by the build." }

Add-AppxPackage -Register $manifest.FullName
Write-Host "Registered $($manifest.FullName). Reload extensions in Command Palette."
