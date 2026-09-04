@echo off
rem Build the KindleChat console client (works on Windows)
rem Usage: build.cmd [Debug^|Release]
setlocal
set MODE=%1
if "%MODE%"=="" set MODE=Debug
dotnet build "%~dp0RKCore.vbproj" -c %MODE%
