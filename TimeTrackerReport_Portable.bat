@echo off
chcp 65001 >nul
cd /d "%~dp0"
if exist "%~dp0TimeTrackerReport.exe" (
  start "" "%~dp0TimeTrackerReport.exe"
  exit /b
)
if exist "%~dp0artifacts\TimeTrackerReport-win-x64\TimeTrackerReport.exe" (
  start "" "%~dp0artifacts\TimeTrackerReport-win-x64\TimeTrackerReport.exe"
  exit /b
)
where dotnet >nul 2>&1
if errorlevel 1 (
  echo ネイティブ版のexe、または.NET 10 SDKが必要です。
  echo GitHub ActionsのWindows nativeから配布ZIPを取得するか、SDKをインストールしてください。
  echo https://dotnet.microsoft.com/download/dotnet/10.0
  pause
  exit /b 1
)
dotnet run --project "%~dp0desktop\TimeTracker.Windows\TimeTracker.Windows.csproj"
if errorlevel 1 pause
