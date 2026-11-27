@echo off
setlocal

cd /d "%~dp0"

dotnet publish AndroidLogViewer.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=false ^
  -o "%~dp0publish\win-x64"

if errorlevel 1 (
  echo.
  echo Publish failed.
  pause
  exit /b 1
)

echo.
echo Publish complete:
echo %~dp0publish\win-x64
pause
