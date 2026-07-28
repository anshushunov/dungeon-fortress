@echo off
rem Starts the project-owned domain MCP server for an MCP client (Issue #38).
rem
rem The server must not execute
rem   tools\DungeonFortress.DomainMcp\bin\Release\net8.0\DungeonFortress.DomainMcp.exe
rem directly: that file is the target of "dotnet build DungeonFortress.sln", and
rem a live client session keeps it open, which fails the build with MSB3027 and
rem stops scripts\verify.ps1 before the first test. This launcher runs every
rem session from its own copy under .artifacts\domain-mcp-sessions\<id>, so the
rem build target and the executing copy are always different files. The copy is
rem made at start-up, so a session still runs the latest Release build.
rem
rem This is a .cmd and not a .ps1 on purpose. The client speaks JSON-RPC over
rem this process' stdin/stdout, and cmd hands those handles to the server
rem unchanged. PowerShell cannot: Process.Start passes bInheritHandles=false
rem unless a stream is redirected, so a child started from PowerShell gets a
rem fresh console instead of the client's pipes and the protocol never connects.
rem Nothing here may write to stdout; diagnostics go to stderr.
setlocal EnableExtensions DisableDelayedExpansion

for %%i in ("%~dp0..") do set "REPO_ROOT=%%~fi"
set "HOST_NAME=DungeonFortress.DomainMcp.exe"
set "BUILD_OUTPUT=%REPO_ROOT%\tools\DungeonFortress.DomainMcp\bin\Release\net8.0"
set "SESSIONS_ROOT=%REPO_ROOT%\.artifacts\domain-mcp-sessions"

if not exist "%BUILD_OUTPUT%\%HOST_NAME%" (
    >&2 echo domain-mcp-server: missing "%BUILD_OUTPUT%\%HOST_NAME%".
    >&2 echo domain-mcp-server: run "dotnet build DungeonFortress.sln -c Release" before starting a client session.
    exit /b 1
)

if not exist "%SESSIONS_ROOT%" md "%SESSIONS_ROOT%" 2>nul
if not exist "%SESSIONS_ROOT%" (
    >&2 echo domain-mcp-server: could not create "%SESSIONS_ROOT%".
    exit /b 1
)

rem Drop copies left behind by sessions that were killed instead of shut down.
for /d %%d in ("%SESSIONS_ROOT%\*") do call :remove_if_dead "%%~fd"

rem md fails when the directory already exists, so the retry loop gives every
rem concurrent client its own copy even if two of them draw the same %RANDOM%.
set /a ATTEMPT=0
:pick_session
set /a ATTEMPT+=1
if %ATTEMPT% gtr 64 (
    >&2 echo domain-mcp-server: could not create a private session copy under "%SESSIONS_ROOT%".
    exit /b 1
)
set "SESSION_ROOT=%SESSIONS_ROOT%\%RANDOM%%RANDOM%"
md "%SESSION_ROOT%" 2>nul || goto :pick_session

robocopy "%BUILD_OUTPUT%" "%SESSION_ROOT%" /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP >nul 2>&1
if errorlevel 8 (
    >&2 echo domain-mcp-server: could not copy the server into "%SESSION_ROOT%".
    rd /s /q "%SESSION_ROOT%" 2>nul
    exit /b 1
)

"%SESSION_ROOT%\%HOST_NAME%" --root "%REPO_ROOT%"
set "SERVER_EXIT=%ERRORLEVEL%"
rd /s /q "%SESSION_ROOT%" 2>nul
exit /b %SERVER_EXIT%

rem Opening the executable for append succeeds only when no process is running
rem it, so a live session is never touched. Deleting the whole directory at once
rem is what makes this safe: a partial delete could break a running server that
rem has not loaded all of its assemblies yet.
:remove_if_dead
2>nul ( >>"%~1\%HOST_NAME%" (call ) ) && rd /s /q "%~1" 2>nul
exit /b 0
