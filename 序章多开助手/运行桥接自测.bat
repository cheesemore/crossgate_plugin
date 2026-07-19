@echo off
setlocal EnableExtensions
set PYTHONIOENCODING=utf-8
set PYTHONUTF8=1
cd /d "%~dp0scripts"
python run_bridge_smoke_test.py %*
set EXITCODE=%ERRORLEVEL%
echo.
echo [桥接自测] 退出码 %EXITCODE%  （0=通过 1=闪退 2=黑屏 3=错误 4=注入 5=无凭据 6=登录失败 7=进游戏失败 8=多控失败 9=组队失败）
exit /b %EXITCODE%
