@echo off
rem ПРОТОТИП: сборка в короткий путь (в папке проекта путь слишком длинный для Windows) и запуск.
set OUT=%TEMP%\BeautyDeskProto
"C:\Program Files\dotnet\dotnet.exe" build "%~dp0ShellRisksPrototype.csproj" -nologo -v q -o "%OUT%" || exit /b 1
start "" "%OUT%\ShellRisksPrototype.exe" %*
