@echo off
rem One command to make RhapsodyServer-Setup.exe:   installer.cmd
rem   1. bundles PHP / Apache / nginx / MariaDB + tools (skipped if .\bundle is already complete)
rem   2. builds the app into .\dist with the bundle beside it
rem   3. compiles installer\rhapsody-server.iss with Inno Setup  ->  installer-out\RhapsodyServer-Setup.exe
rem Needs: .NET 8 SDK, and Inno Setup 6 (https://jrsoftware.org/isdl.php). If Apache Lounge blocks the download,
rem run  bundle.cmd --apache-zip "C:\path\httpd-...-win64-VS17.zip"  once first, then run this.
setlocal

dotnet run --project tools\RhapsodyServer.Bundler -c Release -- --verify bundle >nul 2>&1
if errorlevel 1 (
  echo Bundle is missing or incomplete: downloading components...
  call bundle.cmd %*
  if errorlevel 1 exit /b 1
)

call publish.cmd
if errorlevel 1 exit /b 1

set "ISCC="
for %%P in ("%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" "%ProgramFiles%\Inno Setup 6\ISCC.exe" "%LocalAppData%\Programs\Inno Setup 6\ISCC.exe") do if exist %%P set "ISCC=%%~P"
if not defined ISCC for /f "delims=" %%I in ('where ISCC.exe 2^>nul') do set "ISCC=%%I"
if not defined ISCC (
  echo.
  echo Inno Setup 6 was not found. Install it from https://jrsoftware.org/isdl.php and run installer.cmd again.
  echo The app itself is already built in .\dist.
  exit /b 1
)

"%ISCC%" installer\rhapsody-server.iss
if errorlevel 1 exit /b 1
echo.
echo Done: installer-out\RhapsodyServer-Setup.exe
