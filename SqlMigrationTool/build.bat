@echo off
cd /d "%~dp0"

echo Restoring packages...
dotnet restore SqlMigrationTool\SqlMigrationTool.csproj --configfile NuGet.Config

echo.
echo Building self-contained portable release...
dotnet publish SqlMigrationTool\SqlMigrationTool.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=false ^
    -p:PublishTrimmed=false ^
    --configfile NuGet.Config ^
    -o dist\SqlMigrationTool

echo.
echo BUILD COMPLETE
echo Portable folder: dist\SqlMigrationTool\
echo.
echo To distribute: ZIP the dist\SqlMigrationTool\ folder and send it.
echo Recipient: unzip, double-click SqlMigrationTool.exe. No install needed.
pause
