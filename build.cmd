@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build.ps1"
if errorlevel 1 (
  echo.
  echo BUILD FAILED.
  echo A timestamped transcript is stored under:
  echo   %~dp0build-logs\
  echo Send that .log file when reporting a build or self-test failure.
  pause
  exit /b 1
)
echo.
echo BUILD AND SELF-TESTS PASSED.
echo Build transcripts are stored under:
echo   %~dp0build-logs\
pause
