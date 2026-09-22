@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo ========================================================
echo   Antigravity 用量面板 启动中...
echo ========================================================

if not exist "bin\AntigravityQuota.exe" (
    echo [错误] 未找到 bin\AntigravityQuota.exe
    echo        请先双击运行 scripts\build.bat 完成编译。
    echo.
    pause
    exit /b 1
)

echo [1/2] 关闭已有实例...
taskkill /f /im AntigravityQuota.exe >nul 2>&1
rem 等端口释放
ping -n 2 127.0.0.1 >nul

echo [2/2] 启动采集服务，浏览器将自动打开面板...
start "" "%~dp0bin\AntigravityQuota.exe"

echo.
echo   停止服务：双击 scripts\stop.bat
echo.
ping -n 3 127.0.0.1 >nul
exit
