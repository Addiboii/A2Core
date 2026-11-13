@echo off
rem A2Core one-step Windows server setup: double-click (it asks for administrator rights), or run from a command prompt.
rem Moving the Linux server: set A2CORE_MIGRATE_FROM and A2CORE_TOKEN first (see server-setup.txt).
net session >nul 2>&1
if %errorlevel% neq 0 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
if "%A2CORE_SOURCE%"=="" (
  echo Set A2CORE_SOURCE=^<server^>:8091 first, e.g.  set A2CORE_SOURCE=203.0.113.5:8091
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -Command "irm http://%A2CORE_SOURCE%/a2core/server-setup.txt | iex"
pause
