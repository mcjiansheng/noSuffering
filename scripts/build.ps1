param([string]$GameDir=$env:STS2_INSTALL_DIR,[string]$Dotnet='dotnet')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if (!$GameDir) {throw 'Provide -GameDir or STS2_INSTALL_DIR.'}
if ($Dotnet -eq 'dotnet' -and (Test-Path "$root\.tools\dotnet\dotnet.exe")) {$Dotnet="$root\.tools\dotnet\dotnet.exe"}
& $Dotnet build "$root\NoSuffering.csproj" -c Release "-p:Sts2InstallDir=$GameDir" --nologo
if ($LASTEXITCODE -ne 0) {throw "Build failed: $LASTEXITCODE"}
$stage="$root\artifacts\NoSuffering"
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item "$root\bin\Release\net9.0\NoSuffering.dll","$root\NoSuffering.json" $stage -Force
Copy-Item "$root\README.md" $stage -Force
if(Test-Path "$stage\docs"){Remove-Item "$stage\docs" -Recurse -Force}
Copy-Item "$root\docs" $stage -Recurse -Force
if(Test-Path "$root\THIRD_PARTY_NOTICES.md"){Copy-Item "$root\THIRD_PARTY_NOTICES.md" $stage -Force}
$version=(Get-Content "$root\NoSuffering.json" -Raw -Encoding UTF8 | ConvertFrom-Json).version
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stream=[IO.File]::Open("$root/artifacts/NoSuffering-$version.zip",[IO.FileMode]::Create)
$archive=New-Object IO.Compression.ZipArchive($stream,[IO.Compression.ZipArchiveMode]::Create)
try {
    foreach($file in Get-ChildItem $stage -Recurse -File){
        $entry='NoSuffering/'+$file.FullName.Substring($stage.Length+1).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.FullName,$entry) | Out-Null
    }
}finally{$archive.Dispose();$stream.Dispose()}
Get-FileHash "$stage\NoSuffering.dll" -Algorithm SHA256
