@echo off
setlocal
cd /d "%~dp0"
set "VER=V1.0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ERROR] .NET Framework 4.x compiler not found.
  pause
  exit /b 1
)
"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /out:CaptureTool_%VER%.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll CaptureTool_%VER%.cs
if errorlevel 1 (
  echo.
  echo [ERROR] Build failed.
  pause
  exit /b 1
)
echo.
echo [OK] CaptureTool_%VER%.exe created.
start "" "CaptureTool_%VER%.exe"
