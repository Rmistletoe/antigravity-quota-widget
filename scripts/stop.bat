@echo off
chcp 65001 >nul
echo 正在停止 Antigravity 用量面板服务...
taskkill /f /im AntigravityQuota.exe >nul 2>&1 && echo 已停止。 || echo 服务当前未在运行。
ping -n 2 127.0.0.1 >nul
exit
