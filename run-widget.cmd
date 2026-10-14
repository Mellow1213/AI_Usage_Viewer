@echo off
setlocal
set "widget_exe=%~dp0Release\AIUsageWidget.exe"
if not exist "%widget_exe%" (
  echo Release\AIUsageWidget.exe is missing.
  echo Run build-widget.ps1 first.
  pause
  exit /b 1
)
start "" /D "%~dp0Release" "%widget_exe%"
endlocal