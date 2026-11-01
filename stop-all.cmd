@echo off
setlocal EnableExtensions
chcp 65001 >nul

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
cd /d "%ROOT%" || exit /b 1

echo === A2Core: stopping the local stack ===
python "%ROOT%\tools\start_all_windows.py" stop-servers
if errorlevel 1 (
  echo [ERROR] stop-servers failed.
  exit /b 1
)

rem Close the cmd windows we opened with A2Core * titles
powershell -NoProfile -Command ^
  "Get-Process cmd -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like 'A2Core*' } | Stop-Process -Force -ErrorAction SilentlyContinue"

echo Done.
exit /b 0
