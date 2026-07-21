@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo [BUILD] 傻瓜补丁 exe ...
python scripts\publish_foolproof.py
if errorlevel 1 (
  echo [FAIL]
  pause
  exit /b 1
)
echo.
echo 完成。发布物在 发布\ 目录。
pause
