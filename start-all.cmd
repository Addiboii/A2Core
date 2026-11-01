@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul

rem A2Core: one double-click to start PostgreSQL, server hosts, and the launcher.
rem Usage: start-all.cmd [nobuild] [nolauncher] [skipncguard]

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
set "NOBUILD=0"
set "NOLAUNCHER=0"
set "SKIPNCGUARD=0"
for %%A in (%*) do (
  if /I "%%~A"=="nobuild" set "NOBUILD=1"
  if /I "%%~A"=="nolauncher" set "NOLAUNCHER=1"
  if /I "%%~A"=="skipncguard" set "SKIPNCGUARD=1"
)

echo.
echo === A2Core: starting the local stack (Windows) ===
echo Repository root: "%ROOT%"
echo.

cd /d "%ROOT%" || (
  echo [ERROR] Could not change to the repository folder.
  exit /b 1
)

python "%ROOT%\tools\start_all_windows.py" check
if errorlevel 1 exit /b 1

python "%ROOT%\tools\start_all_windows.py" postgres-up
if errorlevel 1 exit /b 2

if "%NOBUILD%"=="0" (
  echo.
  echo Building Aion.Host ^(Debug, same as tools/run_account_server.py^)...
  dotnet build "%ROOT%\server\Aion.Host\Aion.Host.csproj" -c Debug --nologo -v q
  if errorlevel 1 (
    echo [ERROR] Build failed.
    exit /b 3
  )
) else (
  echo Skipping build ^(nobuild^).
)

if not exist "%ROOT%\server\Aion.Host\bin\Debug\net10.0\Aion.Host.dll" (
  echo [ERROR] Aion.Host.dll not found. Drop nobuild or run: dotnet build server/Aion.Host
  exit /b 3
)

python "%ROOT%\tools\start_all_windows.py" prepare-launch
if errorlevel 1 exit /b 4

set "RUNTIME=%ROOT%\.start-all\runtime"
set "LOGDIR=%ROOT%\.start-all\logs"
if not exist "%LOGDIR%" mkdir "%LOGDIR%"

echo.
echo Starting processes in separate windows...

rem 1) Account site + API (8780)
start "A2Core Account 8780" cmd /k "chcp 65001>nul && cd /d \"%ROOT%\" && echo [A2Core] Account + API http://127.0.0.1:8780 && python tools\run_account_server.py"

rem 2) Login + Game + Platform bootstrap + Chat (3700, 13328, 8087, 8091) - one Aion.Host process
start "A2Core Login Game Platform Chat" cmd /k "\"%RUNTIME%\start-native.cmd\""

rem 3) NCGuard TLS sink (443), optional — launcher redirects NC hostnames here
if "%SKIPNCGUARD%"=="0" if exist "%RUNTIME%\start-ncguard.cmd" (
  start "A2Core NCGuard 443" cmd /k "\"%RUNTIME%\start-ncguard.cmd\""
) else if "%SKIPNCGUARD%"=="0" (
  echo [WARNING] NCGuard fixture not found; the launcher may not reach the game without skipncguard and your own fixture.
)

echo Waiting for ports...
python "%ROOT%\tools\start_all_windows.py" wait-ports 8780 3700 13328 8087 8091 --timeout 120
if errorlevel 1 (
  echo [ERROR] Services did not start in time. Check the server windows and %LOGDIR%
  exit /b 5
)

if "%SKIPNCGUARD%"=="0" if exist "%RUNTIME%\ncguard-host.env" (
  python "%ROOT%\tools\start_all_windows.py" wait-ports 443 --timeout 90
  if errorlevel 1 (
    echo [WARNING] Port 443 is not responding - accept the UAC prompt for NCGuard or run with skipncguard.
  )
)

if "%NOLAUNCHER%"=="1" (
  echo Done ^(nolauncher^). Account: http://127.0.0.1:8780
  exit /b 0
)

set "LAUNCHER="
if exist "%ROOT%\research\local\launcher-publish\A2Core Launcher.exe" set "LAUNCHER=%ROOT%\research\local\launcher-publish\A2Core Launcher.exe"
if not defined LAUNCHER if exist "%ROOT%\out\launcher\A2Core Launcher.exe" set "LAUNCHER=%ROOT%\out\launcher\A2Core Launcher.exe"

if not defined LAUNCHER (
  echo.
  echo [WARNING] Launcher not found. Build it:
  echo   dotnet publish launcher/AionLauncher -c Release -r win-x64 -o research/local/launcher-publish
  echo Servers are running; open http://127.0.0.1:8780
  exit /b 0
)

echo Starting A2Core Launcher...
start "" "%LAUNCHER%"

echo.
echo Done. Order: PostgreSQL -^> Account 8780 -^> Native 3700/13328 + Bootstrap 8087 + Chat 8091 -^> NCGuard 443 -^> Launcher
echo To stop: stop-all.cmd
exit /b 0
