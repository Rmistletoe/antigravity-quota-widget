@echo off
chcp 65001 >nul
set VERSION=1.0.0

echo ========================================================
echo   Antigravity Quota Widget 一键发布打包 (v%VERSION%)
echo ========================================================

echo [1/3] 正在清理并使用 Release 模式编译...
taskkill /f /im AntigravityQuota.exe >nul 2>&1
dotnet build "%~dp0..\src\AntigravityQuota\AntigravityQuota.csproj" -c Release -o "%~dp0..\bin"
if %ERRORLEVEL% neq 0 (
    echo [错误] 编译失败！
    pause
    exit /b %ERRORLEVEL%
)

echo [2/3] 正在创建 releases 目录...
if not exist "%~dp0..\releases" mkdir "%~dp0..\releases"

echo [3/3] 正在打包生成发布压缩包...
set ZIP_NAME=antigravity-quota-widget-v%VERSION%.zip
powershell -Command "Compress-Archive -Path '%~dp0..\bin', '%~dp0..\scripts', '%~dp0..\start.bat', '%~dp0..\README.md', '%~dp0..\LICENSE' -DestinationPath '%~dp0..\releases\%ZIP_NAME%' -Force"

echo.
echo ========================================================
echo   打包完成！输出文件: releases\%ZIP_NAME%
echo ========================================================
pause
