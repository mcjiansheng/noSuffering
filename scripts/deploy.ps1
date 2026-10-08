param([Parameter(Mandatory=$true)][string]$GameDir)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if (Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue) {throw 'Close the game before deploying NoSuffering.'}
$stage="$root\artifacts\NoSuffering"
if (!(Test-Path "$stage\NoSuffering.dll")) {throw 'Run build.ps1 first.'}
$dest="$GameDir\mods\NoSuffering"
if(Test-Path $dest){$backup="$root\artifacts\deploy-backup-$(Get-Date -Format yyyyMMdd-HHmmss)";Copy-Item $dest $backup -Recurse}
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item "$stage\*" $dest -Force
Write-Output "Installed: $dest"
