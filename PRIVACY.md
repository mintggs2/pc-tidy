# PC Tidy privacy

**Short version: nothing leaves your PC.**

- **No telemetry.** PC Tidy doesn't collect usage statistics, crash reports or "anonymous" data, and doesn't phone home.
- **No account.** There is nothing to sign up for or sign in to.
- **No ads, no tracking.**
- **No network use for its own sake.** PC Tidy works fully offline. It only goes online when *you* start something that needs it and that PC Tidy tells you about first, for example opening a web page or a Windows update screen you clicked on.
- **Your data stays in your data folder** (by default `%LOCALAPPDATA%\PC Tidy`, changeable in Settings):
  - `settings.json`: your settings.
  - `runs\`: the history of what PC Tidy changed, so it can be undone.
  - `Quarantine\`: files PC Tidy set aside instead of deleting them, so you can get them back.
  - `logs\`: plain-text logs to help you (or someone you choose to share them with) understand a problem.
  - Learned preferences (what you usually keep or remove), if learning is on.
- **Nothing is shared.** If you want help with a problem, you decide whether to send a log to anyone. Logs can contain file and folder names from your PC, so look before you share.
- **Saved passwords are never read or touched.** Browser cleanup never includes saved passwords.
- **Uninstalling** asks whether to keep your data folder (the default is to keep it, because it holds the quarantine). If you choose to delete it, it's gone from your PC.

Optional features that use other software (for example a local AI helper, if one is installed and you switch it on) only talk to that software on your own PC (loopback), never to the internet.

Questions or concerns: open an issue on the project page.
