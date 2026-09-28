@echo off
rem EchoRelay game server host: set up this PC's Echo VR installs and region, keep them updated, then start the game
rem servers players request from the installer for this region. Keep EchoRelay.Host.exe next to this file.
title EchoRelay Game Server Host
cd /d "%~dp0"
if not exist "EchoRelay.Host.exe" (
    echo EchoRelay.Host.exe isn't next to this .bat. Extract the whole zip into one folder and run it again.
    pause
    exit /b 1
)
"EchoRelay.Host.exe" %*
echo.
echo The host stopped. Close this window, or run the .bat again to restart it.
pause
