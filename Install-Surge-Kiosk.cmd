@echo off
setlocal
title Surge Mobile Event Kiosk Installer
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Surge-Kiosk.ps1"
if errorlevel 1 (
  echo.
  echo Installation did not finish. Review the message above.
  pause
)
endlocal
