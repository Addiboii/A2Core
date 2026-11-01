@echo off
setlocal EnableExtensions
set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
net session >nul 2>&1
if errorlevel 1 (
  powershell.exe -NoProfile -Command "Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File','\"%ROOT%\capture.ps1\"','-InGame',%*)"
  exit /b !ERRORLEVEL!
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\capture.ps1" -InGame %*
exit /b %ERRORLEVEL%
