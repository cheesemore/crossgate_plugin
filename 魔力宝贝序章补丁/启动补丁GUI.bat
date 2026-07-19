@echo off
setlocal EnableExtensions
set PYTHONIOENCODING=utf-8
set PYTHONUTF8=1
cd /d "%~dp0scripts"
if errorlevel 1 (
  echo [FAIL] Cannot cd to scripts folder
  pause
  exit /b 1
)
python seqchapter_combo_gui.py
if errorlevel 1 pause
