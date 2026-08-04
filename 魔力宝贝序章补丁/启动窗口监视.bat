@echo off
chcp 65001 >nul
cd /d "%~dp0"
start "" pythonw "%~dp0scripts\window_monitor_gui.py"
if errorlevel 1 (
  echo 启动失败，尝试 python …
  python "%~dp0scripts\window_monitor_gui.py"
  pause
)
