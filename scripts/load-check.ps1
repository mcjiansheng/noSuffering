param([Parameter(Mandatory=$true)][string]$GameDir,[switch]$InteractiveSession)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue){throw 'Close the game before the startup check.'}
$log="$root/artifacts/load-beta-interactive.log"
if($InteractiveSession){
    # Run under the already logged-in Windows account so Steam IPC is available.
    $task='NoSuffering-StartupCheck'
    $action=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -GameDir `"$GameDir`""
    $principal=New-ScheduledTaskPrincipal -UserId ([System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value) -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Force | Out-Null
    Start-ScheduledTask -TaskName $task
    Write-Output "Started $task in the existing interactive session. Inspect $log and unregister the completed task."
    return
}
$process=Start-Process -FilePath "$GameDir/SlayTheSpire2.exe" -WorkingDirectory $GameDir -ArgumentList @('--headless','--max-fps','30','--quit-after','600','--log-file',$log) -PassThru
$process.WaitForExit()
"exit=$($process.ExitCode)" | Set-Content "$root/artifacts/load-check-exit.txt"
