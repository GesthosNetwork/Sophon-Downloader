@echo off
dotnet publish -c Release
pause
taskkill /F /IM dotnet.exe
taskkill /F /IM MSBuild.exe
