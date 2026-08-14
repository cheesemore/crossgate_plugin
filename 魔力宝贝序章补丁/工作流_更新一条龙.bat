@echo off
chcp 65001 >nul
cd /d "%~dp0.."
echo 将执行：python tools\workflow.py update
echo 请确认已关闭游戏，且 crosscopy 已手动更新完毕。
pause
python tools\workflow.py update %*
if errorlevel 1 (
  echo [FAIL]
  pause
  exit /b 1
)
pause
