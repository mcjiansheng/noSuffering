param([string]$GameDir=$env:STS2_INSTALL_DIR,[string]$AssemblyDir=$env:STS2_ASSEMBLY_DIR,[ValidateSet('windows','macos')][string]$Platform,[string]$Branch='public-beta',[string]$Dotnet='dotnet',[string]$Python)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if (!$GameDir) {throw 'Provide -GameDir or STS2_INSTALL_DIR.'}
if (!$Python) {
    if(Test-Path "$root/.tools/python/python.exe") {$Python="$root/.tools/python/python.exe"}
    elseif(Get-Command python -ErrorAction SilentlyContinue) {$Python='python'}
    elseif(Get-Command python3 -ErrorAction SilentlyContinue) {$Python='python3'}
    elseif(Get-Command py -ErrorAction SilentlyContinue) {$Python='py'}
    else {throw 'Provide -Python to Python 3 or install the project-local .tools/python/python.exe runtime.'}
}
$arguments=@("$PSScriptRoot/build.py",'inspect','--game-dir',$GameDir,'--branch',$Branch,'--dotnet',$Dotnet)
if($AssemblyDir) {$arguments+=@('--assembly-dir',$AssemblyDir)}
if($Platform) {$arguments+=@('--platform',$Platform)}
& $Python @arguments
if($LASTEXITCODE -ne 0) {throw "NoSuffering inspect failed: $LASTEXITCODE"}
