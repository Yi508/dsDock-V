@echo off
rem Run the automated self test (isolated data root, never touches your settings.json).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0validate.ps1"
pause
