@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo [BUILD] 傻瓜补丁·九动版（四选一：九动加速/抓宠/烧卡/慢速烧卡） ...
python scripts\publish_foolproof.py --nine-pack
if errorlevel 1 (
  echo [FAIL]
  pause
  exit /b 1
)
echo.
echo 完成。发布物在 发布plugin\ 目录（同系列旧包已自动删除）。
pause
