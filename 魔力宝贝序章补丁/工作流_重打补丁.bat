@echo off
chcp 65001 >nul
cd /d "%~dp0.."
echo 将执行：python tools\workflow.py repatch（默认组合，需关游戏）
pause
python tools\workflow.py repatch
if errorlevel 1 (
  echo [FAIL]
  pause
  exit /b 1
)
pause
