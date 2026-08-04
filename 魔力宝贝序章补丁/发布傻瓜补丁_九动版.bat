@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo [DISABLED] 九动版已停发（见 publish_packs.json packs.nine.enabled=false）。
echo 默认请运行「发布傻瓜补丁.bat」（融合版 + 换装）。
echo 若确需强制打九动包：python scripts\publish_foolproof.py --nine-pack --force-nine-pack
echo.
pause
exit /b 1
