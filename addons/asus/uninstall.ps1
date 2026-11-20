# Удаление дополнения ASUS для BeautyDesk: задание планировщика, программа и её файлы.
param([switch]$NoPause)

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
  $extra = if ($NoPause) { ' -NoPause' } else { '' }
  Start-Process powershell -Verb RunAs -Wait -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`"$extra"
  exit
}

Unregister-ScheduledTask -TaskName 'AsusFans' -TaskPath '\BeautyDesk\' -Confirm:$false -ErrorAction SilentlyContinue
Get-Process AsusFans -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
Remove-Item (Join-Path $env:ProgramFiles 'BeautyDesk-AsusFans') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'BeautyDesk\bridge\fans.*') -Force -ErrorAction SilentlyContinue

Write-Host 'Дополнение ASUS удалено. Виджет «Питание» снова показывает только режим питания Windows.' -ForegroundColor Green
if (-not $NoPause) { Read-Host 'Enter — закрыть' | Out-Null }
