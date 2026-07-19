@echo off
setlocal EnableExtensions
cd /d "%~dp0"
call "运行桥接自测.bat" --inject --login --enter-game --multi-login --summon %*
