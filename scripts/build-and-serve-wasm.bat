@echo off
REM ###########################################################################
REM  build-and-serve-wasm.bat
REM  
REM  Bygger og hoster CSharPN.Wasm (Blazor WebAssembly app)
REM 
REM  Brug:
REM    build-and-serve-wasm.bat [options]
REM 
REM  Optioner:
REM    --host <host>       Server vært (default: localhost)
REM    --port <port>       Server port (default: 8080)
REM    --no-build          Skip build step, kun host
REM    --no-open           Åbn ikke browser automatisk
REM ###########################################################################

setlocal enabledelayedexpansion

set "SCRIPT_DIR=%~dp0"
set "PROJECT_ROOT=%SCRIPT_DIR%.."
set "WASM_CSPROJ=%PROJECT_ROOT%\src\CSharPN.Wasm\CSharPN.Wasm.csproj"
set "PUBLISH_DIR=%PROJECT_ROOT%\publish\wasm"
set "WWWROOT_DIR=%PUBLISH_DIR%\wwwroot"

set "HOST=localhost"
set "PORT=8080"
set "DO_BUILD=true"
set "DO_OPEN=true"

REM Parse argumenter
:parse_args
if "%~1"=="" goto args_done
if "%~1"=="--host" (
    set "HOST=%~2"
    shift
    shift
    goto parse_args
)
if "%~1"=="--port" (
    set "PORT=%~2"
    shift
    shift
    goto parse_args
)
if "%~1"=="--no-build" (
    set "DO_BUILD=false"
    shift
    goto parse_args
)
if "%~1"=="--no-open" (
    set "DO_OPEN=false"
    shift
    goto parse_args
)
if "%~1"=="--help" (
    echo Brug: build-and-serve-wasm.bat [--host ^<host^>] [--port ^<port^>] [--no-build] [--no-open]
    exit /b 0
)
shift
goto parse_args

:args_done

echo.
echo ================================================================================
echo CSharPN WebAssembly Builder ^& Server
echo ================================================================================

REM Build
if "%DO_BUILD%"=="true" (
    echo.
    echo Packaging Build af CSharPN.Wasm ^(Release^)...
    echo.
    
    dotnet publish "%WASM_CSPROJ%" ^
        --configuration Release ^
        --output "%PUBLISH_DIR%" ^
        --no-restore ^
        --verbosity normal
    
    if errorlevel 1 (
        echo.
        echo Fejl under build!
        exit /b 1
    )
    
    echo.
    echo Build gennemfort!
)

REM Check wwwroot folder
if not exist "%WWWROOT_DIR%" (
    echo.
    echo Fejl: wwwroot-mappe ikke fundet pa %WWWROOT_DIR%
    echo Kor først med --build eller dotnet publish manuelt.
    exit /b 1
)

REM Start server
echo.
echo ================================================================================
echo Starter server pa http://%HOST%:%PORT%
echo ================================================================================
echo.
echo Folder: %WWWROOT_DIR%
echo.
echo Tryk Ctrl+C for at stoppe serveren
echo.

REM Prøv at åbne browser
if "%DO_OPEN%"=="true" (
    start http://%HOST%:%PORT%
)

REM Start Python HTTP server
cd /d "%WWWROOT_DIR%"
python -m http.server %PORT% --bind %HOST%
