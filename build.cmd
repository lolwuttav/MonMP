@echo off
setlocal
rem PowerShell 7 module paths cannot be inherited by Windows PowerShell 5.1.
set "PSModulePath=%SystemRoot%\System32\WindowsPowerShell\v1.0\Modules;%ProgramFiles%\WindowsPowerShell\Modules"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build.ps1" %*
set "releaseExit=%errorlevel%"
endlocal & exit /b %releaseExit%
