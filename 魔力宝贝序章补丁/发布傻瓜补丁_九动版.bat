@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo.
echo [已停发] 九动版已无限期取消发布。
echo 当前只发布：融合版（发布傻瓜补丁.bat）+ 傻瓜换装补丁（发布傻瓜换装补丁.bat）。
echo.
echo 请运行「发布傻瓜补丁.bat」发布默认包（融合版 + 换装）。
echo.
pause
exit /b 1
