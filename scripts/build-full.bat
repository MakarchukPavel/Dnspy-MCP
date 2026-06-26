@echo off
title build dnspy-mcp (FULL - rebuilds dnSpy subset)

REM ===========================================================
REM  FULL build: compiles the dnSpy subset -> ..\lib\, then both
REM  agents (x64 + x86) and the host -> ..\dist\. Slow (several
REM  minutes). Use after the first clone, a submodule update, or
REM  if ..\lib\ was cleaned.
REM
REM  IMPORTANT: stop any running agent (Ctrl+C) AND close Claude
REM  Code first - otherwise the running ..\dist\*.exe are locked
REM  and the build fails with "file in use".
REM ===========================================================

echo Building dnspy-mcp (FULL)...
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\builder.ps1"
echo.
echo Done (exit %errorlevel%).
pause
