@echo off
rem Made-up admin batch file for PC Tidy's tests: the restore point is made first, by a subroutine written at the end.
call :restorepoint
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v HideFileExt /t REG_DWORD /d 0 /f
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v Hidden /t REG_DWORD /d 1 /f
copy /b "%~dp0notes.txt" +,,
echo Done. Settings applied: 2 of 2.
echo ran > "{MARKER}"
goto :eof

:restorepoint
wmic.exe /Namespace:\\root\default Path SystemRestore Call CreateRestorePoint "Before team settings", 100, 7
exit /b
