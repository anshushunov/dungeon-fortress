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
rem made when the session starts, so a session runs the Release build that was
rem current at that moment and keeps running it until the session is restarted.
rem
rem This is a .cmd and not a .ps1 because cmd starts the server with the very
rem stdin/stdout handles the client handed to this process, with nothing in
rem between. PowerShell's native command processor does not pass a child's
rem streams through: it turns their output into pipeline objects and rewrites it
rem through the host, so a PowerShell launcher would have to shuttle the
rem JSON-RPC bytes between two processes itself. That is an extra copy, extra
rem buffering and an encoding risk on the protocol path for no gain.
rem (scripts\verify-domain-mcp.ps1 does drive the same server from PowerShell,
rem but it is the client there, not a transparent pass-through.)
rem
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

rem Drop copies and locks left behind by sessions that were killed instead of
rem shut down. Live sessions are skipped, see :remove_if_dead.
for /d %%d in ("%SESSIONS_ROOT%\*") do call :remove_if_dead "%%~fd"
for %%f in ("%SESSIONS_ROOT%\*.lock") do call :remove_orphan_lock "%%~ff"

set /a ATTEMPT=0
:pick_session
set /a ATTEMPT+=1
if %ATTEMPT% gtr 64 (
    >&2 echo domain-mcp-server: could not create a private session copy under "%SESSIONS_ROOT%".
    exit /b 1
)
set "SESSION_ID=%RANDOM%%RANDOM%"
set "SESSION_ROOT=%SESSIONS_ROOT%\%SESSION_ID%"
set "SESSION_STAGE=%SESSION_ROOT%.partial"
set "SESSION_LOCK=%SESSIONS_ROOT%\%SESSION_ID%.lock"

rem The lock is opened before the session directory exists and stays open until
rem the server exits, so a concurrent sweep can never mistake a session that is
rem still copying for a dead one. If the lock or the id is already taken,
rem :run_session does not reach "set CLAIMED", and another id is drawn.
set "CLAIMED="
9>>"%SESSION_LOCK%" call :run_session
set "SESSION_EXIT=%ERRORLEVEL%"
if not defined CLAIMED goto :pick_session

rd /s /q "%SESSION_STAGE%" 2>nul
rd /s /q "%SESSION_ROOT%" 2>nul
del "%SESSION_LOCK%" 2>nul
exit /b %SESSION_EXIT%

rem Runs one session while the caller holds the session lock on handle 9.
:run_session
md "%SESSION_STAGE%" 2>nul || exit /b 1
set "CLAIMED=1"
robocopy "%BUILD_OUTPUT%" "%SESSION_STAGE%" /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP >nul 2>&1
if errorlevel 8 (
    >&2 echo domain-mcp-server: could not copy the server into "%SESSION_STAGE%".
    exit /b 1
)
rem The runnable directory only ever appears complete: the copy is staged next
rem to it and renamed into place in one step.
ren "%SESSION_STAGE%" "%SESSION_ID%" 2>nul
if not exist "%SESSION_ROOT%\%HOST_NAME%" (
    >&2 echo domain-mcp-server: could not stage the session copy in "%SESSION_ROOT%".
    exit /b 1
)
"%SESSION_ROOT%\%HOST_NAME%" --root "%REPO_ROOT%"
exit /b %ERRORLEVEL%

rem Removes a session copy only when nothing is using it. A live session holds
rem its lock on handle 9 from before the copy until the server exits, and an
rem orphaned server keeps its own executable open even after its launcher is
rem gone. The executable is probed only when it exists, so the probe never
rem creates one. Deleting the whole directory at once is what makes this safe:
rem a partial delete could break a running server that has not loaded all of its
rem assemblies yet.
:remove_if_dead
set "SWEEP_NAME=%~nx1"
set "SWEEP_ID=%SWEEP_NAME%"
if /i "%SWEEP_NAME:~-8%"==".partial" set "SWEEP_ID=%SWEEP_NAME:~0,-8%"
2>nul ( 9>>"%SESSIONS_ROOT%\%SWEEP_ID%.lock" (call ) ) || exit /b 0
if exist "%~1\%HOST_NAME%" (
    2>nul ( >>"%~1\%HOST_NAME%" (call ) ) || exit /b 0
)
rd /s /q "%~1" 2>nul
del "%SESSIONS_ROOT%\%SWEEP_ID%.lock" 2>nul
exit /b 0

rem A lock without a session directory is left over from an id that was drawn
rem but never used, or from a copy that has already been removed.
:remove_orphan_lock
if exist "%~dpn1" exit /b 0
if exist "%~dpn1.partial" exit /b 0
2>nul ( 9>>"%~1" (call ) ) || exit /b 0
del "%~1" 2>nul
exit /b 0
