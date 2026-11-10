@echo off
rem Made-up "one-click" menu for PC Tidy's tests.
echo [1] Apply all tweaks (recommended)
echo [2] Exit
set /p pick=Choose:
if "%pick%"=="1" goto applyall
goto :eof
:applyall
reg add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management" /v LargeSystemCache /t REG_DWORD /d 1 /f
echo ran > "{MARKER}"
