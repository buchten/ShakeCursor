@echo off
setlocal
cd /d "%~dp0"

call Build.cmd
if errorlevel 1 exit /b 1

set "INSTALL_DIR=%LOCALAPPDATA%\ShakeCursor"
if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"

copy /y "ShakeCursor.exe" "%INSTALL_DIR%\ShakeCursor.exe" >nul
if errorlevel 1 (
    echo ERROR: Installation failed. If Shake Cursor is already running, exit it from the notification area and try again.
    pause
    exit /b 1
)

start "" "%INSTALL_DIR%\ShakeCursor.exe"
echo Shake Cursor was installed and started.
echo Use its notification-area icon to change settings or enable Start with Windows.
timeout /t 3 >nul
exit /b 0
