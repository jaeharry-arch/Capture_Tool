@echo off
setlocal
cd /d "%~dp0"
if "%~2"=="" (
  echo Usage: new_version.bat ^<FROM^> ^<TO^>
  echo   e.g. new_version.bat V0.1 V0.2
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0new_version.ps1" -From "%~1" -To "%~2"
if errorlevel 1 pause
