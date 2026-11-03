@echo off
rem Starts Command Commander. The first start takes a few seconds while it is built.
rem Windows will ask for Administrator permission (UAC).
start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0CommandCommander.ps1"
