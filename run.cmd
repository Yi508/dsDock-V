@echo off
rem Build + deploy OUTSIDE the workspace + start.
rem Running the copy inside the workspace forces Low integrity (no tray icon), so this
rem delegates to tools\deploy.cmd. To start the app later, just double click the deployed
rem exe (default: %~dp0..\..\dsDock\DsDock.exe  ->  D:\dsDock\DsDock.exe).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\deploy.ps1" %*
pause
