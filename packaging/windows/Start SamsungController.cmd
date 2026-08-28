@echo off
setlocal

cd /d "%~dp0"
set "CONTROLLER_URL=http://127.0.0.1:5050"

if not exist "SamsungController.Web.exe" (
    echo SamsungController.Web.exe is missing.
    echo Extract the complete release archive, then try again.
    pause
    exit /b 1
)

start "" /B powershell.exe -NoProfile -WindowStyle Hidden -Command "$url='%CONTROLLER_URL%'; for ($i = 0; $i -lt 240; $i++) { try { Invoke-WebRequest -UseBasicParsing -Uri $url -TimeoutSec 1 | Out-Null; Start-Process $url; exit 0 } catch {}; Start-Sleep -Milliseconds 250 }"

echo Starting SamsungController...
echo Keep this window open while using the web interface.
echo Open %CONTROLLER_URL% manually if the browser does not appear.
echo.

SamsungController.Web.exe
set "EXIT_CODE=%ERRORLEVEL%"

if not "%EXIT_CODE%"=="0" (
    echo.
    echo SamsungController stopped with error code %EXIT_CODE%.
    echo Another program may already be using port 5050.
    pause
)

endlocal & exit /b %EXIT_CODE%
