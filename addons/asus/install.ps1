# Установка дополнения ASUS для BeautyDesk.
# Копирует AsusFans.exe в Program Files и регистрирует задание планировщика: при входе в Windows оно
# запускает дополнение с повышенными правами (они нужны только для доступа к драйверу ASUS).
param([string]$User = "$env:USERDOMAIN\$env:USERNAME", [switch]$NoPause)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
  # Задание должно работать от имени того, кто вошёл в Windows, даже если права даёт другая учётная запись.
  $extra = if ($NoPause) { ' -NoPause' } else { '' }
  Start-Process powershell -Verb RunAs -Wait -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -User `"$User`"$extra"
  exit
}
function Pause-End { if (-not $NoPause) { Read-Host 'Enter — закрыть' | Out-Null } }

$wmi = Get-CimInstance -Namespace root\wmi -ClassName AsusAtkWmi_WMNB -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $wmi) {
  Write-Host 'Драйвер ASUS не найден. Нужен ноутбук ASUS с «ASUS System Control Interface» (ставится вместе с MyASUS).' -ForegroundColor Red
  Pause-End; exit 1
}

$dir = Join-Path $env:ProgramFiles 'BeautyDesk-AsusFans'
New-Item -ItemType Directory -Force $dir | Out-Null
Get-Process AsusFans -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
Copy-Item (Join-Path $here 'AsusFans.exe') $dir -Force
$exe = Join-Path $dir 'AsusFans.exe'

$principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Highest
$action    = New-ScheduledTaskAction -Execute $exe
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User $User
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) `
               -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName 'AsusFans' -TaskPath '\BeautyDesk\' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName 'AsusFans' -TaskPath '\BeautyDesk\'

Write-Host ''
Write-Host 'Готово. Добавьте на рабочий стол виджет «Питание» — режимы вентиляторов и порог зарядки появятся в нём.' -ForegroundColor Green
Write-Host 'Если стоит G-Helper или Armoury Crate, переключайте режимы в одной программе: иначе они будут перебивать друг друга.'
Pause-End
