@echo off
setlocal
title dnspy-mcp agent (x64)

REM ===========================================================
REM  Starts the x64 (64-bit) dnspy-mcp DEBUG AGENT on port 5555.
REM  Use for x64 .NET targets (most Creatio NetFramework w3wp and
REM  NetCore). For 32-bit targets use start-agent-x86.bat (:5556).
REM  The debugger bitness MUST match the target's.
REM  Connect the MCP host: debug_session_connect(host="127.0.0.1", port=5555)
REM ===========================================================

REM --- Attaching to IIS w3wp needs admin -> self-elevate ---
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator rights...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

REM ===================== SETTINGS =====================
set "PORT=5555"
REM Empty TOKEN = no auth (fine for localhost-only pilot). Set a value to require a token.
set "TOKEN="
REM ===================================================

set "TOKENARG="
if not "%TOKEN%"=="" set "TOKENARG=--token %TOKEN%"

REM Resolve the agent exe relative to this script (canonical absolute path).
for %%I in ("%~dp0..\dist\dnspymcpagent\dnspymcpagent.exe") do set "AGENT=%%~fI"

if not exist "%AGENT%" (
    echo ERROR: x64 agent exe not found:
    echo   %AGENT%
    echo Build it first: run build-fast.bat (or build-full.bat).
    pause
    exit /b 1
)

echo Starting dnspy-mcp agent (x64)
echo   Bind:  127.0.0.1:%PORT%   (localhost only)
if "%TOKEN%"=="" (echo   Token: none) else (echo   Token: %TOKEN%)
echo   Exe:   %AGENT%
echo.
echo Leave this window open. Press Ctrl+C to stop.
echo.
"%AGENT%" --host 127.0.0.1 --port %PORT% %TOKENARG%

echo.
echo Agent stopped.
pause
