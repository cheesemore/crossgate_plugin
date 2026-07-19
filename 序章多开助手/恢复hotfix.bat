@echo off
chcp 65001 >nul
set "SCRIPT=%~dp0..\序章助手共享\assistant_common\patch_bridge.py"
python -c "import sys; from pathlib import Path; sys.path.insert(0, str(Path(r'%~dp0..')/'序章助手共享')); from assistant_common.config import get_game_root; from assistant_common.patch_bridge import remove_bridge_patch; ok,msg=remove_bridge_patch(get_game_root()); print(msg); sys.exit(0 if ok else 1)"
if errorlevel 1 pause
