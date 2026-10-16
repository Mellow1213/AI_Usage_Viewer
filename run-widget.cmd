@echo off
setlocal
if exist "%LOCALAPPDATA%\Mellow.AIUsageViewer\Update.exe" (
  start "" /D "%LOCALAPPDATA%\Mellow.AIUsageViewer" "%LOCALAPPDATA%\Mellow.AIUsageViewer\Update.exe" start AIUsageWidget.exe
  exit /b 0
)
echo AI Usage Viewer is not installed in this Windows environment.
echo Install Mellow.AIUsageViewer-win-Setup.exe from GitHub Releases.
echo For development, use build-dev-widget.ps1 and run-dev-widget.cmd.
exit /b 1
endlocal
