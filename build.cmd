@echo off
rem Build dsDock (offline, zero third party dependencies).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
echo.
echo Exit code: %ERRORLEVEL%
pause
