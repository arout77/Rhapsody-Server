@echo off
rem Downloads the latest PHP / Apache / nginx / MariaDB + tools into .\bundle  (needs the .NET 8 SDK and internet)
rem   bundle.cmd --dry-run           just check the vendor pages first
rem   bundle.cmd --php 8.5,8.4,8.3   bundle more than the latest PHP
rem   bundle.cmd --apache-zip "C:\Downloads\httpd-2.4.xx-...-win64-VS17.zip"
rem                                  use an Apache you downloaded in your browser (apachelounge.com often blocks scripts)
dotnet run --project tools\RhapsodyServer.Bundler -c Release -- %*
