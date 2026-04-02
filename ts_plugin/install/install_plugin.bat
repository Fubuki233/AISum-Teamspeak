@echo off
setlocal EnableExtensions

set "SCRIPT_DIR=%~dp0"
set "PLUGIN_NAME=ts_voice_forwarder"
set "DLL_SRC=%SCRIPT_DIR%%PLUGIN_NAME%.dll"
set "CFG_TEMPLATE=%SCRIPT_DIR%%PLUGIN_NAME%.cfg"
set "TS3_PLUGIN_DIR=%APPDATA%\TS3Client\plugins"
set "DLL_DST=%TS3_PLUGIN_DIR%\%PLUGIN_NAME%.dll"
set "CFG_DST=%TS3_PLUGIN_DIR%\%PLUGIN_NAME%.cfg"

if not exist "%DLL_SRC%" set "DLL_SRC=%SCRIPT_DIR%..\out\%PLUGIN_NAME%.dll"
if not exist "%CFG_TEMPLATE%" set "CFG_TEMPLATE=%SCRIPT_DIR%..\ts_voice_forwarder.cfg.example"

if not exist "%DLL_SRC%" (
    goto fail
)

if not exist "%TS3_PLUGIN_DIR%" (
    mkdir "%TS3_PLUGIN_DIR%" >nul 2>&1
)

if not exist "%TS3_PLUGIN_DIR%" (
    goto fail
)

copy /Y "%DLL_SRC%" "%DLL_DST%" >nul
if errorlevel 1 (
    goto fail
)

if exist "%CFG_DST%" (
) else (
    if exist "%CFG_TEMPLATE%" (
        copy /Y "%CFG_TEMPLATE%" "%CFG_DST%" >nul
    )
)

echo 安装成功
exit /b 0

:fail
echo 安装失败
exit /b 1
