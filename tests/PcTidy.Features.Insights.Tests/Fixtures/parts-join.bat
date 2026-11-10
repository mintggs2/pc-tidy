@echo off
rem Made-up "installer" for PC Tidy's tests: two pieces glued into one program.
copy /b "%TEMP%\setup.part1" + "%TEMP%\setup.part2" "%TEMP%\setup.exe"
echo ran > "{MARKER}"
