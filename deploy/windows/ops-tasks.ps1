<#
.SYNOPSIS
  Installs the A2Core backup scripts and their scheduled tasks (run as administrator; install-package.ps1 calls it; safe to run again).

.DESCRIPTION
  Copies backup.ps1 and restore-test.ps1 from this folder to <Root> and registers, as SYSTEM:
  - "A2Core nightly backup": every day at 05:00. "Run as soon as possible after a missed start" is on, so a PC that is switched
    off at night makes the backup at the next start-up instead of skipping it (the first version of the task never ran on the
    Dell for that reason). Allowed on battery; stops after 2 hours.
  - "A2Core monthly restore test": the 1st of every month at 13:00 (also catches up after a missed start): restores the newest
    dump into the scratch database aion_restore_test and compares row counts (restore-test.ps1).
  Neither task stops or restarts the game server.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File deploy\windows\ops-tasks.ps1 -Root G:\A2Core
#>
param(
    [Parameter(Mandatory)] [string] $Root,
    [string] $BackupTime = "05:00",
    [string] $RestoreTestTime = "13:00"
)
$ErrorActionPreference = "Stop"
foreach ($script in "backup.ps1", "restore-test.ps1") {
    $from = Join-Path $PSScriptRoot $script
    $to = Join-Path $Root $script
    if ((Resolve-Path $from).Path -ne [IO.Path]::GetFullPath($to)) { Copy-Item $from $to -Force }
}

function TaskXml($description, $trigger, $script, $limit) {
    $command = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $Root $script)`""
    return @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>$description</Description></RegistrationInfo>
  <Triggers>$trigger</Triggers>
  <Principals><Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>$limit</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author"><Exec><Command>powershell.exe</Command><Arguments>$([Security.SecurityElement]::Escape($command))</Arguments></Exec></Actions>
</Task>
"@
}

$today = (Get-Date).ToString("yyyy-MM-dd")
$daily = "<CalendarTrigger><StartBoundary>$($today)T$($BackupTime):00</StartBoundary><Enabled>true</Enabled><ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay></CalendarTrigger>"
$months = "<January/><February/><March/><April/><May/><June/><July/><August/><September/><October/><November/><December/>"
$monthly = "<CalendarTrigger><StartBoundary>$($today)T$($RestoreTestTime):00</StartBoundary><Enabled>true</Enabled><ScheduleByMonth><DaysOfMonth><Day>1</Day></DaysOfMonth><Months>$months</Months></ScheduleByMonth></CalendarTrigger>"

Register-ScheduledTask -TaskName "A2Core nightly backup" -Force -Xml (TaskXml "A2Core database dump into $Root\backups, about 14 days kept (deploy\windows\backup.ps1)." $daily "backup.ps1" "PT2H") | Out-Null
Register-ScheduledTask -TaskName "A2Core monthly restore test" -Force -Xml (TaskXml "Restores the newest A2Core dump into the scratch database aion_restore_test and compares row counts (deploy\windows\restore-test.ps1)." $monthly "restore-test.ps1" "PT2H") | Out-Null
foreach ($name in "A2Core nightly backup", "A2Core monthly restore test") {
    $info = Get-ScheduledTaskInfo -TaskName $name
    Write-Host "$name - next run $($info.NextRunTime)"
}
