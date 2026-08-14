@echo off
chcp 65001 >nul
cd /d "%~dp0.."
echo [BUILD] 傻瓜补丁两版（融合版 + 带龙族）…
python tools\workflow.py publish-foolproof
if errorlevel 1 (
  echo [FAIL]
  pause
  exit /b 1
)
echo.
echo 完成。发布物在 E:\cross\发布plugin\
pause
