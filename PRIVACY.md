# PC Tidy privacy

**Short version: nothing about you or your PC leaves it.** The one thing PC Tidy does online by itself is a daily update check, described below, and you can switch it off.

- **No telemetry.** PC Tidy doesn't collect usage statistics, crash reports or "anonymous" data. The update check below only asks for the newest version and sends nothing else.
- **No account.** There is nothing to sign up for or sign in to.
- **No ads, no tracking.**
- **One automatic network request: the update check** (see below). Apart from that, PC Tidy works fully offline and only goes online when *you* start something that needs it and that PC Tidy tells you about first: winget (a PC care scan asks it about app updates, and it installs apps you pick), the DNS speed test, the Lag check (off until you choose a test server), or a web page or Windows screen you clicked on.
- **Your data stays in your data folder** (by default `%LOCALAPPDATA%\PC Tidy`, changeable in Settings):
  - `settings.json`: your settings.
  - `runs\`: the history of what PC Tidy changed, so it can be undone.
  - `Quarantine\`: files PC Tidy set aside instead of deleting them, so you can get them back.
  - `logs\`: plain-text logs to help you (or someone you choose to share them with) understand a problem.
  - `scan-checkpoints\`: the results of a scan that stopped part way (for example the paths of the files it found), so you can resume it. They are deleted when the scan finishes, when you choose Start over, after a run, or once they are more than a day old.
  - `updates\`: update files PC Tidy downloaded after you pressed Update now (an installer or a portable zip). Old ones are deleted once a newer PC Tidy has started.
  - Learned preferences (what you usually keep or remove), if learning is on.
- **Nothing is shared.** If you want help with a problem, you decide whether to send a log to anyone. Logs can contain file and folder names from your PC, so look before you share.
- **Saved passwords are never read or touched.** Browser cleanup never includes saved passwords.
- **Uninstalling** asks whether to keep your data folder (the default is to keep it, because it holds the quarantine). If you choose to delete it, it's gone from your PC.

## The update check

- **What:** at most once a day (a little while after PC Tidy starts), and whenever you press **Check for updates** in Settings › Updates, PC Tidy asks GitHub for the newest release of PC Tidy: `https://api.github.com/repos/mintggs2/pc-tidy/releases/latest`. The address is fixed in the program; no setting or imported settings file can change it.
- **What is sent:** an ordinary HTTPS request with two headers, `User-Agent: PC-Tidy/<version>` (for example `PC-Tidy/3.1.1`) and `Accept`. No cookies, no account or token, no ID, nothing about this PC, its files or you. Like any web request, GitHub sees your IP address; GitHub's own privacy statement applies to that.
- **What happens with the answer:** if there is a newer finished release (never a draft or a test release), PC Tidy shows "PC Tidy X.Y.Z is available" with its release notes as plain text, and **Update now**, **Later** and **Skip this version**. Nothing is downloaded until you press Update now, and nothing is installed until you confirm once more.
- **Downloads** come only from that release on GitHub (github.com and GitHub's download servers, HTTPS only): the one file for your kind of copy (`PC-Tidy-Setup-<version>.exe` or `PC-Tidy-<version>-portable.zip`) and the release's `SHA256SUMS.txt`. The file is saved in your data folder's `updates\` folder and must match its SHA-256 checksum, or it is deleted. A portable copy is unpacked into a new folder; PC Tidy never starts anything from it by itself.
- **Switch it off:** Settings › Updates › **Check for updates automatically**. Then PC Tidy only checks when you press Check for updates. Demo and practice mode never go online.
- When the check last ran and a version you skipped are kept in `settings.json` on this PC only, and are left out of settings exports.

Optional features that use other software (for example a local AI helper, if one is installed and you switch it on) only talk to that software on your own PC (loopback), never to the internet.

Questions or concerns: open an issue on the project page.
