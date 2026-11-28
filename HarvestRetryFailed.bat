@echo off
rem Double-click after running the story scripts listed in harvest_blocked.txt (or when an event opens): clears the
rem failed stops so the harvester tries them again. A dated copy of the progress is kept. Stop the harvester first.
cd /d "%~dp0"
dotnet run --project src\SkuaSim.Cli -- harvest-plan --retry-failed %*
echo.
pause
