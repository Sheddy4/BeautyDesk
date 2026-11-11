@echo off
rem Сборка и запуск для разработки. Собираем в короткий путь: Windows не запускает exe по слишком длинному пути.
set OUT=%TEMP%\BeautyDeskDev
"C:\Program Files\dotnet\dotnet.exe" build "%~dp0src\BeautyDesk\BeautyDesk.csproj" -nologo -v q -o "%OUT%" || exit /b 1
start "" "%OUT%\BeautyDesk.exe" %*
