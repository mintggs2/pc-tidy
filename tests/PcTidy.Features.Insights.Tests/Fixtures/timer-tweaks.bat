@echo off
rem Lower input lag: timer tweaks
bcdedit /set useplatformclock true
bcdedit /set {current} disabledynamictick yes
bcdedit /deletevalue useplatformtick
echo 0.0.0.0 settings-win.data.microsoft.com >> %windir%\System32\drivers\etc\hosts
echo ran > "{MARKER}"