@echo off
setlocal EnableExtensions
set PYTHONIOENCODING=utf-8
set PYTHONUTF8=1
cd /d "%~dp0..\序章助手共享"
python scripts\check_client_integrity.py "%~dp0.."
echo.
pause
