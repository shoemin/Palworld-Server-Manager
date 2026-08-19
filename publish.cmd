@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\publish.ps1"
if errorlevel 1 (
  echo.
  echo PUBLISH FAILED. Review the output above.
  pause
  exit /b 1
)
echo.
echo PUBLISH COMPLETE.
pause
