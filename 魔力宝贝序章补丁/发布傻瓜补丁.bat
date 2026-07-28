@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo [INFO] 默认已改为「融合版」。正在转发…
call "%~dp0发布傻瓜补丁_融合版.bat"
