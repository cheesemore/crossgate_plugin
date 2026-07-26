@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo [BUILD] 傻瓜补丁·烧卡/抓宠（兼容：旧「烧封印」入口） ...
python scripts\publish_foolproof.py --seal-catch
if errorlevel 1 (
  echo [FAIL]
  pause
  exit /b 1
)
echo.
echo 完成。发布物在 发布\ 目录（同系列旧包已自动删除）。
pause
