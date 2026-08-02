@echo off
chcp 65001 >nul
cd /d "%~dp0"
REM 工作目录=E:\cross\魔力宝贝：序章 ；配置从 crosscopy 提取到本目录 _config_extract
REM 启动游戏请用本目录上级的 cg37.exe，不是 crossgate_cursor\cross.exe
if not exist "%~dp0_config_extract\excelgeneral\pet_tbcommenemybaseconfig.bytes" (
  echo [提示] 尚未从 crosscopy 提取配置，正在提取...
  python "%~dp0extract_seqchapter_configs.py"
  if errorlevel 1 pause & exit /b 1
  python "%~dp0export_pet_appear_bin.py"
)
python "%~dp0pet_appear_gui.py"
if errorlevel 1 pause
