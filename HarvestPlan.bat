@echo off
rem Double-click: writes %LOCALAPPDATA%\SkuaSim Harvest\harvest_plan.json for tools\skua\SkuaSim_Harvester.cs
rem (the maps, shops and quests the Skua scripts use that the collected data still lacks).
cd /d "%~dp0"
echo Building the SkuaSim harvest plan (about 20 seconds)...
echo.
dotnet run --project src\SkuaSim.Cli -- harvest-plan %*
echo.
pause
