@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo [INFO] 按 publish_packs.json 发布默认包（融合版 + 换装）…
python scripts\publish_default_packs.py
if errorlevel 1 (
  echo [FAIL]
  pause
  exit /b 1
)
echo.
echo 完成。发布物在 发布plugin\ 目录。
pause
