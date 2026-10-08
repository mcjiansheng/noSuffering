param([Parameter(Mandatory=$true)][string]$BetaGameDir,[string]$StableGameDir)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$rows=@()
foreach($entry in @(@{Branch='public-beta';Path=$BetaGameDir},@{Branch='public';Path=$StableGameDir})){
    if(!$entry.Path){$rows+=@{branch=$entry.Branch;result='not_executed';reason='No installed assembly directory supplied'};continue}
    try{
        & "$PSScriptRoot/build.ps1" -GameDir $entry.Path
        $version=Get-Content "$($entry.Path)/release_info.json" -Raw -Encoding UTF8 | ConvertFrom-Json
        $target="$root/artifacts/$($entry.Branch)"
        New-Item -ItemType Directory -Force $target | Out-Null
        Copy-Item "$root/artifacts/NoSuffering" $target -Recurse -Force
        $rows+=@{branch=$entry.Branch;result='build_passed';game=$version.version;assembly_sha256=(Get-FileHash "$($entry.Path)/data_sts2_windows_x86_64/sts2.dll").Hash;gameplay='not_executed'}
    }catch{$rows+=@{branch=$entry.Branch;result='build_failed';reason=$_.Exception.Message}}
}
$rows | ConvertTo-Json -Depth 4 | Set-Content "$root/artifacts/build-matrix.json" -Encoding UTF8
$rows | ConvertTo-Json -Depth 4
if($rows.result -contains 'build_failed'){throw 'A supplied branch failed to build. Inspect build-matrix.json.'}
