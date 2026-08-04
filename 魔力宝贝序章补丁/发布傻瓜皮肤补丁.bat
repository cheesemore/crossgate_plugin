@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo [INFO] 已更名为「傻瓜换装补丁」，正在转发…
call "%~dp0发布傻瓜换装补丁.bat"
