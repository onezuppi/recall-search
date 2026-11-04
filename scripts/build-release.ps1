<#
.SYNOPSIS
  Builds the release archive: the self-contained agent, the self-contained Command Palette extension and the end-user install scripts.
.EXAMPLE
  .\scripts\build-release.ps1 -Version 0.2.0
.NOTES
  Output: dist\RecallSearch-<Version>-win-<Platform>.zip and a .sha256 file next to it.
  The archive does not need the .NET SDK or runtime on the target machine.
#>
[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [ValidateSet('x64', 'ARM64')]
    [string]$Platform = 'x64'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$rid = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$dist = Join-Path $repo 'dist'
$stage = Join-Path $dist "stage-$rid"
$name = "RecallSearch-$Version-$rid"
$zip = Join-Path $dist "$name.zip"

if (-not ($Version -match '^\d+\.\d+\.\d+$')) { throw "Version must look like 1.2.3, got '$Version'." }
Remove-Item -Recurse -Force $stage, $zip -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $stage 'agent'), (Join-Path $stage 'cmdpal') | Out-Null

Write-Host "== Agent ($rid)"
dotnet publish (Join-Path $repo 'src\Recall.Agent') -c Release -r $rid --self-contained true "-p:Version=$Version" -o (Join-Path $stage 'agent')
if ($LASTEXITCODE -ne 0) { throw 'Agent publish failed.' }

Write-Host "== Command Palette extension ($rid)"
$project = Join-Path $repo 'src\Recall.CmdPal'
dotnet build $project -c Release "-p:Platform=$Platform" "-p:RuntimeIdentifier=$rid" -p:SelfContained=true "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw 'Extension build failed.' }
$manifest = Get-ChildItem (Join-Path $project "bin\$Platform\Release") -Filter AppxManifest.xml -Recurse |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $manifest) { throw 'AppxManifest.xml was not produced by the build.' }
Copy-Item (Join-Path $manifest.DirectoryName '*') (Join-Path $stage 'cmdpal') -Recurse -Force

# The package version follows the release version.
$manifestCopy = Join-Path $stage 'cmdpal\AppxManifest.xml'
(Get-Content $manifestCopy -Raw) -replace '(<Identity [^>]*Version=")[\d.]+(")', "`${1}$Version.0`${2}" | Set-Content $manifestCopy -Encoding utf8
Get-ChildItem (Join-Path $stage 'cmdpal') -Include '*.pdb', '*.appxrecipe' -Recurse | Remove-Item -Force

Write-Host '== Install scripts'
Copy-Item (Join-Path $PSScriptRoot 'release\install.ps1'), (Join-Path $PSScriptRoot 'release\uninstall.ps1') $stage
Copy-Item (Join-Path $repo 'LICENSE') $stage
Set-Content (Join-Path $stage 'VERSION.txt') $Version -Encoding ascii

Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
$sha = [Security.Cryptography.SHA256]::Create()
$stream = [IO.File]::OpenRead($zip)
try { $hash = ([BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant() } finally { $stream.Dispose() }
"$hash  $name.zip" | Set-Content (Join-Path $dist "$name.zip.sha256") -Encoding ascii
Write-Host ("Built {0} ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
Write-Host "SHA-256: $hash"
