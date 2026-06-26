@echo off
title build dnspy-mcp (FAST - skip dnSpy subset)

REM ===========================================================
REM  FAST build (-SkipDnSpy): reuses ..\lib\ and rebuilds only the
REM  agents (x64 + x86) and the host -> ..\dist\. Use for normal
REM  iteration after one FULL build.
REM
REM  IMPORTANT: stop any running agent (Ctrl+C) AND close Claude
REM  Code first - otherwise the running ..\dist\*.exe are locked
REM  and the build fails with "file in use".
REM ===========================================================

echo Building dnspy-mcp (FAST, -SkipDnSpy)...
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\builder.ps1" -SkipDnSpy
echo.
echo Done (exit %errorlevel%).
pause
