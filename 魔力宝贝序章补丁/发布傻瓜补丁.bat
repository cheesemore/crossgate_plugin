@echo off
chcp 65001 >nul
cd /d "%~dp0.."
echo [BUILD] 按 publish_packs.json 默认清单发布…
python tools\workflow.py publish-all
if errorlevel 1 (
  echo [FAIL]
  pause
  exit /b 1
)
echo.
echo 完成。发布物在：游戏目录上一级\发布plugin\
echo （本仓库相对路径：..\发布plugin\ ；可用环境变量 SEQCHAPTER_RELEASE_DIR 覆盖）
pause
