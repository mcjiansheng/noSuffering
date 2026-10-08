param([Parameter(Mandatory=$true)][string]$BetaGameDir,[string]$StableGameDir,[string]$BetaAssemblyDir,[string]$StableAssemblyDir,[ValidateSet('windows','macos')][string]$Platform,[string]$Dotnet='dotnet',[string]$Python)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$rows=@()
foreach($entry in @(@{Branch='public-beta';Path=$BetaGameDir;Assembly=$BetaAssemblyDir},@{Branch='public';Path=$StableGameDir;Assembly=$StableAssemblyDir})){
    if(!$entry.Path){$rows+=@{branch=$entry.Branch;build='not_executed';load='not_executed';singleplayer='not_executed';multiplayer='not_executed';reason='No installed assembly directory supplied'};continue}
    try{
        $parameters=@{GameDir=$entry.Path;Branch=$entry.Branch;Dotnet=$Dotnet}
        if($entry.Assembly){$parameters.AssemblyDir=$entry.Assembly}
        if($Platform){$parameters.Platform=$Platform}
        if($Python){$parameters.Python=$Python}
        & "$PSScriptRoot/build.ps1" @parameters
        $rows+=@{branch=$entry.Branch;build='passed';load='not_executed';singleplayer='not_executed';multiplayer='not_executed'}
    }catch{$rows+=@{branch=$entry.Branch;build='failed';reason=$_.Exception.Message;load='not_executed';singleplayer='not_executed';multiplayer='not_executed'}}
}
$label=if($Platform){$Platform}else{'auto'}
New-Item -ItemType Directory -Force "$root/artifacts/$label" | Out-Null
$rows | ConvertTo-Json -Depth 4 | Set-Content "$root/artifacts/$label/build-matrix.json" -Encoding UTF8
$rows | ConvertTo-Json -Depth 4
if($rows.build -contains 'failed'){throw 'A supplied branch failed to build. Inspect build-matrix.json.'}
