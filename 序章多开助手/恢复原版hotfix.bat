@echo off
setlocal EnableExtensions
set PYTHONIOENCODING=utf-8
set PYTHONUTF8=1
cd /d "%~dp0..\序章助手共享"
python -c "from pathlib import Path; from assistant_common.patch_bridge import remove_bridge_patch; from assistant_common.config import get_game_root; ok, msg = remove_bridge_patch(get_game_root()); print(msg); raise SystemExit(0 if ok else 1)"
echo.
pause
