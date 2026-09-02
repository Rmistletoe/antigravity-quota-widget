@echo off
chcp 65001 >nul
echo ========================================================
echo   Antigravity Quota Widget 一键编译构建
echo ========================================================
echo [1/2] 正在检测并关闭旧进程...
taskkill /f /im AntigravityQuota.exe >nul 2>&1

echo [2/2] 正在使用 .NET 编译 Release 版本...
dotnet build "%~dp0..\AntigravityQuota.sln" -c Release -o "%~dp0..\bin"
if %ERRORLEVEL% equ 0 (
    echo.
    echo ========================================================
    echo   编译成功！可执行文件已生成至 bin\AntigravityQuota.exe
    echo ========================================================
) else (
    echo.
    echo [错误] 编译失败，请检查上方日志输出。
)
pause
