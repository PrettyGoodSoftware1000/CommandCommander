@echo off
rem Starts Command Commander. Windows will ask for Administrator permission (UAC).
start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0CommandCommander.ps1"
