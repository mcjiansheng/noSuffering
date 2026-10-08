param([Parameter(Mandatory=$true)][string]$GameDir)
$ErrorActionPreference='Stop'
$assembly="$GameDir/data_sts2_windows_x86_64"
$result=[ordered]@{release=(Get-Content "$GameDir/release_info.json" -Raw -Encoding UTF8 | ConvertFrom-Json); runtime=(Get-Content "$assembly/sts2.runtimeconfig.json" -Raw -Encoding UTF8 | ConvertFrom-Json); assemblies=@()}
foreach($name in 'sts2.dll','GodotSharp.dll','0Harmony.dll'){
    $path="$assembly/$name"
    $result.assemblies+=@{name=$name;version=[Reflection.AssemblyName]::GetAssemblyName($path).Version.ToString();sha256=(Get-FileHash $path -Algorithm SHA256).Hash}
}
$result | ConvertTo-Json -Depth 8
