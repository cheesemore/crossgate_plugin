@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo === 发布傻瓜换装补丁 ===
python "%~dp0scripts\publish_foolproof_skin.py"
if errorlevel 1 (
  echo.
  echo [失败] 发布未完成
  pause
  exit /b 1
)
echo.
pause
exit /b 0
