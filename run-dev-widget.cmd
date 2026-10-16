@echo off
setlocal
set "widget_exe=%~dp0artifacts\dev-build\AIUsageWidget.exe"
if not exist "%widget_exe%" (
  echo Development build is missing. Run build-dev-widget.ps1 first.
  exit /b 1
)
start "" /D "%~dp0artifacts\dev-build" "%widget_exe%" --data-dir "%~dp0artifacts\dev-profile"
endlocal
