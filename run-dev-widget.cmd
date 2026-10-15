@echo off
cd /d "%~dp0"
start "" "%~dp0Release\AIUsageWidget.exe" --data-dir "%~dp0artifacts\dev-profile"
