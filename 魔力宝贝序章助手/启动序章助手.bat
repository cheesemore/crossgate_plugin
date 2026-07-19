@echo off
setlocal EnableExtensions
set PYTHONIOENCODING=utf-8
set PYTHONUTF8=1
cd /d "%~dp0scripts"
python main_gui.py %*
if errorlevel 1 pause
