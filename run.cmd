@echo off
rem Run the KindleChat console client (works on Windows)
rem Usage: run.cmd [Debug^|Release] [-- args...]
setlocal
set MODE=%1
if "%MODE%"=="" set MODE=Debug
shift
dotnet run --project "%~dp0RKCore.vbproj" -c %MODE% -- %*
