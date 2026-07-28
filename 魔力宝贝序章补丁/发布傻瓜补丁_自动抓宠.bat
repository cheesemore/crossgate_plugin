@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo [INFO] 已并入「融合版 / 九动版」四选一。请用：
echo   发布傻瓜补丁_融合版.bat
echo   发布傻瓜补丁_九动版.bat
echo.
echo 正在转发到融合版…
call "%~dp0发布傻瓜补丁_融合版.bat"
