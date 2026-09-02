@echo off
chcp 65001 >nul
echo 正在退出 Antigravity 用量监控悬浮组件...
taskkill /f /im AntigravityQuota.exe >nul 2>&1
echo 已退出。
exit
