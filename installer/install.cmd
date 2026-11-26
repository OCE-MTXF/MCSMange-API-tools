@echo off
setlocal
set "LOG=%TEMP%\MCSMServerManager-install.log"
echo [%date% %time%] Setup payload started.> "%LOG%"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" >> "%LOG%" 2>&1
set "RC=%ERRORLEVEL%"
echo [%date% %time%] install.ps1 exit code %RC%>> "%LOG%"
if not "%RC%"=="0" (
  echo Installation failed with code %RC%. See %LOG%
)
exit /b %RC%