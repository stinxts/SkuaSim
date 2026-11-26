@echo off
rem Double-click: starts the harvest over. The current progress is archived (harvest_progress.<date>.json, nothing is
rem deleted) and the new plan lists everything the scripts use, also what is already collected (the data only adds up).
rem Stop the harvester on every client first.
cd /d "%~dp0"
echo This archives the harvest progress and makes a plan that revisits everything.
echo Stop the harvester on every client before going on.
echo.
choice /c YN /m "Start the harvest over"
if errorlevel 2 exit /b
echo.
dotnet run --project src\SkuaSim.Cli -- harvest-plan --all --reset %*
echo.
pause
