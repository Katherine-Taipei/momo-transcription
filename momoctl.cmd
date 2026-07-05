@echo off
setlocal
set SCRIPT_DIR=%~dp0
python "%SCRIPT_DIR%src\momo_worker\momoctl.py" %*
endlocal
