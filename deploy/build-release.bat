@echo off
setlocal

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-release.ps1" %*
set EXITCODE=%ERRORLEVEL%

endlocal & exit /b %EXITCODE%
