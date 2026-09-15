@echo off
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist "%CSC%" (
    echo ERROR: The .NET Framework C# compiler was not found.
    echo Install the Microsoft .NET Framework 4.8 Developer Pack, then run this file again.
    pause
    exit /b 1
)

echo Building ShakeCursor.exe...
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /win32manifest:"app.manifest" /win32icon:"app.ico" /out:"ShakeCursor.exe" /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll "ShakeCursor.cs"

if errorlevel 1 (
    echo.
    echo Build failed.
    pause
    exit /b 1
)

echo Build complete: %CD%\ShakeCursor.exe
exit /b 0
