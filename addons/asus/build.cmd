@echo off
rem Builds AsusFans.exe with the .NET Framework compiler that ships with Windows 10/11.
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /target:winexe /r:System.Management.dll /out:"%~dp0AsusFans.exe" "%~dp0AsusFans.cs"
