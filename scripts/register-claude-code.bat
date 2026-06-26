@echo off
setlocal
title register dnspy-mcp in Claude Code

REM ===========================================================
REM  Registers the dnspy-mcp HOST (dnspymcp.exe) with Claude Code
REM  (the `claude mcp` CLI) as a stdio MCP server. Claude Code
REM  launches the host itself, so you do NOT run dnspymcp.exe.
REM
REM  SAFE / IDEMPOTENT: removes any existing "dnspy" entry first,
REM  then re-adds it -> running this any number of times never
REM  errors and always ends pointing at the built host exe.
REM ===========================================================

set "NAME=dnspy"
set "SCOPE=user"
REM Resolve the host exe relative to this script -> canonical absolute path,
REM so the value stored by `claude mcp add` is clean and machine-correct.
for %%I in ("%~dp0..\dist\dnspymcp\dnspymcp.exe") do set "HOST_EXE=%%~fI"

if not exist "%HOST_EXE%" (
    echo ERROR: host exe not found:
    echo   %HOST_EXE%
    echo Build the host first: run build-fast.bat or build-full.bat.
    pause
    exit /b 1
)

call claude mcp get %NAME% >nul 2>&1 && (echo "%NAME%" already registered - re-registering safely...) || (echo "%NAME%" not registered yet - registering fresh...)

call claude mcp remove %NAME% -s %SCOPE% >nul 2>&1
call claude mcp add %NAME% -s %SCOPE% -- "%HOST_EXE%"
if errorlevel 1 goto :failed

echo.
echo Verifying registration:
call claude mcp get %NAME%
echo.
echo Done. Restart Claude Code so the change is picked up.
pause
exit /b 0

:failed
echo.
echo Registration FAILED.
echo   - Is 'claude' on PATH?  (try: claude mcp list)
echo   - Is "%NAME%" registered at another scope (local/project)? Remove it there too.
pause
exit /b 1
