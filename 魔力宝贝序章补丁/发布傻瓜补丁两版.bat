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
echo 完成。发布物在：游戏目录上一级\发布plugin\
echo （本仓库相对路径：..\发布plugin\ ；可用环境变量 SEQCHAPTER_RELEASE_DIR 覆盖）
pause
