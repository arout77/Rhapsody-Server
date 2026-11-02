@echo off
rem Builds the app into .\dist, bundle included. Run bundle.cmd first (once, or whenever you want newer versions).
rem Self-contained: nobody has to install the .NET runtime. ReadyToRun: starts fast.
rem   publish.cmd              refuses to build unless the bundle is complete (Apache included), so the installer never has to download anything
rem   publish.cmd --no-bundle  build the app only (for development)

if /I not "%~1"=="--no-bundle" (
  dotnet run --project tools\RhapsodyServer.Bundler -c Release -- --verify bundle
  if errorlevel 1 (
    echo.
    echo Not building: the bundle is missing or incomplete. Run bundle.cmd first, or use publish.cmd --no-bundle for a dev build.
    exit /b 1
  )
)

dotnet publish src\RhapsodyServer.Tray -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o dist
if errorlevel 1 exit /b 1

if /I not "%~1"=="--no-bundle" (
  if exist dist\bundle rmdir /S /Q dist\bundle
  xcopy /E /I /Y bundle dist\bundle >nul
  echo Bundle copied into dist\bundle
) else (
  echo WARNING: built without a bundle. Rhapsody Server will download components on first run.
)
echo.
echo Done: dist\RhapsodyServer.exe   (installer: open installer\rhapsody-server.iss in Inno Setup)
