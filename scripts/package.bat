@echo off
chcp 65001 >nul
cd /d "%~dp0.."

rem 版本号自动从 csproj 读取（避免忘记同步）；读不到时回退到下面的默认值
set VERSION=2.0.3
for /f "tokens=3 delims=<>" %%v in ('findstr /r /c:"<Version>[0-9]" "src\AntigravityQuota\AntigravityQuota.csproj"') do set VERSION=%%v
if not defined VERSION set VERSION=2.0.3

echo ========================================================
echo   Antigravity 用量面板 一键发布打包 (v%VERSION%)
echo ========================================================

echo [1/3] 关闭运行中的服务...
taskkill /f /im AntigravityQuota.exe >nul 2>&1
rem 等端口释放
ping -n 2 127.0.0.1 >nul

echo [2/3] 编译 Release 版本...
dotnet build "src\AntigravityQuota\AntigravityQuota.csproj" -c Release -o "bin"
if %ERRORLEVEL% neq 0 (
    echo.
    echo [错误] 编译失败，请检查上方输出。
    pause
    exit /b %ERRORLEVEL%
)

echo [3/3] 打包 zip...
if not exist "releases" mkdir "releases"
set ZIP=releases\antigravity-quota-widget-v%VERSION%.zip
if exist "%ZIP%" del "%ZIP%"

powershell -NoProfile -Command "Compress-Archive -Path 'bin','scripts','start.bat','README.md','LICENSE' -DestinationPath '%ZIP%' -Force"

if not exist "%ZIP%" (
    echo.
    echo [错误] 打包失败。
    pause
    exit /b 1
)

echo.
echo ========================================================
echo   打包完成: %ZIP%
echo.
echo   下一步（见 README 的「发布流程」）：
echo     1. git push origin main --tags
echo     2. 到 GitHub 网页新建 Release，把这个 zip 拖进附件区
echo ========================================================
pause
