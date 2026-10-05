@echo off
rem Build, deploy outside the workspace (so the tray icon works) and start.
rem Optional first argument: target directory (default %USERPROFILE%\dsDock).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy.ps1" %*
pause
