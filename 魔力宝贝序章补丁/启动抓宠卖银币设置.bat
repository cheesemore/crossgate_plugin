@echo off
chcp 65001 >nul
cd /d "%~dp0"
start "" pythonw "%~dp0scripts\catch_sell_config_gui.py"
if errorlevel 1 (
  python "%~dp0scripts\catch_sell_config_gui.py"
  pause
)
