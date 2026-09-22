@echo off
chcp 65001 >nul
echo ========================================================
echo   Antigravity 用量面板 一键编译构建
echo ========================================================
echo [1/2] 正在关闭运行中的服务...
taskkill /f /im AntigravityQuota.exe >nul 2>&1

echo [2/2] 正在使用 .NET 编译 Release 版本...
dotnet build "%~dp0..\src\AntigravityQuota\AntigravityQuota.csproj" -c Release -o "%~dp0..\bin"
if %ERRORLEVEL% equ 0 (
    echo.
    echo ========================================================
    echo   编译成功！可执行文件已生成至 bin\AntigravityQuota.exe
    echo   双击根目录 start.bat 即可启动并打开面板
    echo ========================================================
) else (
    echo.
    echo [错误] 编译失败，请检查上方日志输出。
)
pause
