param(
    [ValidateRange(1, 525600)]
    [int]$IntervalMinutes = 5,
    [string]$TaskName = 'PiloSync ログ同期',
    [switch]$Unregister
)

# Uses the Task Scheduler COM API: the ScheduledTasks module needs an elevated PowerShell, this does not.
$ErrorActionPreference = 'Stop'
$service = New-Object -ComObject Schedule.Service
$service.Connect()
$folder = $service.GetFolder('\')
function Find-Task { try { $folder.GetTask($TaskName) } catch { $null } }

if ($Unregister) {
    if (Find-Task) {
        $folder.DeleteTask($TaskName, 0)
        Write-Output "削除: $TaskName"
    } else {
        Write-Output "未登録: $TaskName"
    }
    return
}

$user = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$sync = Join-Path $PSScriptRoot 'sync-logs.ps1'
$wrapper = Join-Path $PSScriptRoot 'run-hidden.vbs'
$destination = Join-Path (Split-Path $PSScriptRoot -Parent) 'logs'
# Resolve the source here so the task does not depend on its own environment.
$source = Join-Path $env:LOCALAPPDATA 'PiloSync'
$previous = Join-Path $env:LOCALAPPDATA 'PalmSync'
if (-not (Test-Path -LiteralPath $source -PathType Container) -and (Test-Path -LiteralPath $previous -PathType Container)) { $source = $previous }

$arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$sync`" -Source `"$source`" -Destination `"$destination`""
$wscript = Join-Path $env:WINDIR 'System32\wscript.exe'

$definition = $service.NewTask(0)
$definition.RegistrationInfo.Description = "PiloSync のログを $destination へコピー"
$definition.Principal.LogonType = 3   # TASK_LOGON_INTERACTIVE_TOKEN
$definition.Principal.RunLevel = 0    # TASK_RUNLEVEL_LUA
$definition.Settings.MultipleInstances = 2   # TASK_INSTANCES_IGNORE_NEW
$definition.Settings.DisallowStartIfOnBatteries = $false
$definition.Settings.StopIfGoingOnBatteries = $false
$definition.Settings.StartWhenAvailable = $true
$definition.Settings.ExecutionTimeLimit = 'PT5M'

# The time trigger starts repeating now; the logon trigger keeps repeating after each sign-in.
$interval = "PT${IntervalMinutes}M"
$time = $definition.Triggers.Create(1)   # TASK_TRIGGER_TIME
$time.StartBoundary = (Get-Date).ToString('yyyy-MM-dd\THH:mm:ss')
$time.Repetition.Interval = $interval
$logon = $definition.Triggers.Create(9)  # TASK_TRIGGER_LOGON
$logon.UserId = $user
$logon.Repetition.Interval = $interval

$action = $definition.Actions.Create(0)  # TASK_ACTION_EXEC
if (Test-Path -LiteralPath $wscript -PathType Leaf) {
    # wscript starts PowerShell without a console window; see run-hidden.vbs.
    $action.Path = $wscript
    $action.Arguments = "`"$wrapper`" powershell.exe $arguments"
    $console = '非表示'
} else {
    $action.Path = 'powershell.exe'
    $action.Arguments = $arguments
    $console = '一瞬表示'
}

$null = $folder.RegisterTaskDefinition($TaskName, $definition, 6, $null, $null, 3)   # TASK_CREATE_OR_UPDATE, TASK_LOGON_INTERACTIVE_TOKEN
$task = $folder.GetTask($TaskName)
$null = $task.Run($null)
Start-Sleep -Seconds 2
$result = '実行中'
for ($i = 0; $i -lt 30; $i++) {
    if ($task.State -ne 4) {   # TASK_STATE_RUNNING
        $result = $task.LastTaskResult
        break
    }
    Start-Sleep -Seconds 1
}
$copied = if (Test-Path -LiteralPath $destination -PathType Container) { (Get-ChildItem -LiteralPath $destination -Recurse -File).Count } else { 0 }

Write-Output "タスク: $TaskName"
Write-Output "間隔: $IntervalMinutes 分"
Write-Output "コンソール: $console"
Write-Output "元: $source"
Write-Output "コピー先: $destination"
Write-Output "初回の終了コード: $result"
Write-Output "コピー先のファイル: $copied 件"
