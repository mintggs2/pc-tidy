# PC Tidy

**Clean up, speed up and organize your Windows PC, safely.**

> [!WARNING]
> **PC Tidy is a beta.** Every change can be previewed, simulated and undone, and it has been tested on generated test PCs, but not yet on many real ones. Please keep a backup, and try **Simulation mode** or **practice mode** first.
>
> **It isn't code-signed yet**, so Windows SmartScreen will probably warn you the first time you run it. See [“Windows protected your PC”](#windows-protected-your-pc).

PC Tidy finds the clutter that builds up on a PC (temporary files, old caches, leftovers, forgotten big files, a messy Downloads folder) and tidies it up. It is built around one idea: **you should never lose anything you wanted.**

- [What's inside](#whats-inside)
- [The safety promise](#the-safety-promise)
- [Install](#install)
- [“Windows protected your PC”](#windows-protected-your-pc)
- [First run](#first-run)
- [Practice mode](#practice-mode)
- [PC Tidy Rescue](#pc-tidy-rescue)
- [Uninstall](#uninstall)
- [Privacy](#privacy)
- [System requirements](#system-requirements)
- [FAQ](#faq)
- [For developers](#for-developers)

## What's inside

| Group | Sections |
|---|---|
| **Clean up** | Disk cleanup · Space map · Duplicates and junk · Developer cleanup |
| **Speed up** | Apps · Speed (startup, context menu, locked files) · Tweaks (evidence-based, with a bad-tweak finder) · Services · Drivers · Games · Game boost · PC care · Insights |
| **Organize** | Organize files · Photos · Desktop tidy · Search · Move to drive · AI chats |
| **You** | Ask PC Tidy (optional local AI) · Learned · Schedule and reports · Privacy sweep · Notifications |

Plus **Optimize & Organize everything** (one run of the safe picks from every section), **Simulation mode**, **History** with Undo, the **Quarantine**, and **PC Tidy Rescue**.

**What PC Tidy deliberately doesn't do:** registry "cleaning", RAM "boosting", or switching off Windows Update, Defender, SmartScreen, UAC, the firewall or anti-cheat. Tweaks that measurably do nothing are shown as such, not sold as speed-ups.

## The safety promise

1. **You see everything first.** PC Tidy scans, shows you a list with a plain-English reason for every item, and changes nothing until you press the button.
2. **Nothing is deleted outright.** Files PC Tidy removes go into a **quarantine** (a private recycle bin). You can put them back for as long as you keep them (7 days by default, and PC Tidy asks before emptying it).
3. **Everything can be undone,** from History in the app or with PC Tidy Rescue. The few things Windows itself can't undo (like emptying the Recycle Bin) are marked in red and are never ticked for you.
4. **Your important places are protected.** Windows, your apps and games, and your own documents and photos are never touched by cleanup. Organizing only moves things where you tell it to.
5. **Crash-proof.** Every change is written down *before* it is made. If your PC crashes or loses power in the middle, PC Tidy (or Rescue) can finish the job or put everything back.
6. **Practice first if you like.** Simulation mode shows exactly what *would* happen, without changing anything.

## Install

You have two choices. Both are free.

### Option A: PC Tidy Setup (recommended)

1. Download **`PC Tidy Setup 3.0.0.exe`** and leave it in your normal **Downloads** folder. Run it from there (not from a shared, network or temporary folder, and not from a folder other programs put files in).
2. Double-click it. If you see a blue “Windows protected your PC” box, see [the next section](#windows-protected-your-pc).
3. Click **Next**, choose your shortcuts and click **Install**.
4. Windows asks for **admin permission once**: PC Tidy goes in `Program Files\PC Tidy` for everyone on this PC, so the parts of PC Tidy that run with admin rights can't be tampered with by other programs. PC Tidy itself then runs with your normal rights.
5. PC Tidy and **PC Tidy Rescue** appear in the Start menu.

**Setup checks it hasn't been tampered with.** Before it asks for admin permission, and again once it has it, Setup checks that no other program has hooked into it (for example a code-injection setting, or a program file loaded from a temporary folder or from the folder Setup was started in). If it finds anything like that, it stops and installs nothing. Download Setup again into your Downloads folder and run it from there; if it still stops, scan your PC with Microsoft Defender first.

Installing a newer version later? Just run the new Setup: it updates PC Tidy in place and keeps your settings, history and quarantine. (An older copy installed just for you in `%LOCALAPPDATA%\Programs\PC Tidy` is removed; your data stays.)

### Option B: portable zip

1. Download **`PC Tidy 3.0.0 portable.zip`**.
2. Right-click it, choose **Extract All…**, and pick a folder (for example `Documents\PC Tidy`).
3. Double-click **`PcTidy.exe`**.

The portable version needs no admin rights at all and works the same for everything that runs as you; it just doesn't add Start-menu shortcuts or an entry in Settings > Apps. Its data still goes to `%LOCALAPPDATA%\PC Tidy`.

**Portable copies can't run admin steps.** A few system cleanups and repairs need admin rights. PC Tidy only asks Windows for them when it runs from `Program Files`, where no other program can change its files; a portable folder could be changed by any program, which would then get those admin rights too. In a portable copy those steps are skipped with a note saying so: install with PC Tidy Setup to use them.

**`PC Tidy Rescue 3.0.0.exe`** is also offered as a separate, single-file download of [PC Tidy Rescue](#pc-tidy-rescue), handy if PC Tidy won't start. Like a portable copy it can't undo steps that needed admin rights; the Rescue that Setup installs can.

## “Windows protected your PC”

When you open PC Tidy Setup (or `PcTidy.exe`) for the first time, Windows may show a blue box saying **“Windows protected your PC”** (this is called *Microsoft Defender SmartScreen*).

**Why it happens:** PC Tidy isn't yet *code-signed*. Signing costs money every year and new apps also need time to build up a “reputation” with Microsoft. SmartScreen shows this warning for every new, unsigned program, not because it found anything wrong.

**To run it anyway:**

1. Click **More info** (small text under the message).
2. Check that the app name is `PC Tidy Setup 3.0.0.exe` (or `PcTidy.exe`).
3. Click **Run anyway**.

**Want to be sure your download is genuine?** Compare its fingerprint (SHA-256 checksum) with the one published in `SHA256SUMS.txt` next to the download. It lists all three downloads: the Setup exe, the portable zip and `PC Tidy Rescue 3.0.0.exe`. To check one:

1. Open the folder with the download, click the address bar, type `powershell` and press Enter.
2. Type this and press Enter (use your file's name):

   ```powershell
   Get-FileHash ".\PC Tidy Setup 3.0.0.exe" -Algorithm SHA256
   ```

3. The long code under **Hash** must be exactly the same as the one for that file in `SHA256SUMS.txt` (upper/lower case doesn't matter). If it isn't, don't run the file: download it again from the official page.

PC Tidy Setup also checks itself before installing: if the download is damaged or incomplete it says so and installs nothing.

## First run

1. Open **PC Tidy** from the Start menu.
2. A short guide explains the basics and offers to try **Simulation mode** first. That's a good idea: you'll see exactly what PC Tidy would do, with no changes.
3. On **Home**, choose **Scan**. A *Quick* scan takes a minute or so; a *Deep* scan looks further.
4. Look through the list. Items marked **Safe** are things Windows or your apps rebuild on their own; they're ticked for you. Items marked **Check** are your call and are never ticked for you. Anything in **red** can't be undone.
5. Press the button to clean or organize. When it's done you'll see what changed, how much space was freed, and an **Undo** button.

Everything PC Tidy has done is listed under **History**, where you can undo any run.

## Practice mode

Want to try PC Tidy without it touching your real files? Start it in practice mode with a folder of its own:

```
"%ProgramFiles%\PC Tidy\PcTidy.exe" --sandbox "C:\PC Tidy practice"
```

PC Tidy then treats that folder as a pretend PC: everything happens inside it and nothing else can be touched. Windows tools (like Disk Cleanup) only pretend to run. Its pretend Downloads folder is `C:\PC Tidy practice\C\Users\tester\Downloads`: copy some files in there and try organizing them, then undo.

To just look around with sample data, use `PcTidy.exe --demo` (nothing on your PC is read or changed).

## PC Tidy Rescue

**PC Tidy Rescue** is a small separate tool that can undo PC Tidy's changes **even if PC Tidy itself won't start** (or has been uninstalled).

- Open it from the Start menu (**PC Tidy Rescue**), or run `PC Tidy Rescue.exe` from the PC Tidy folder. A spare copy is also kept in your data folder (`%LOCALAPPDATA%\PC Tidy\Rescue\`), so it's still there after an uninstall. Like a portable copy, the spare one can't undo steps that needed admin rights; the one in Program Files can.
- It shows every run, newest first. Pick one and choose:
  - **Undo**: put back everything that run changed.
  - **Resume** / **Roll back** (for a run that was interrupted by a crash or power cut): finish it, or put back what it had already done.
  - **Restore everything in quarantine for this run**: the fallback if Undo can't finish.
- It always asks before changing anything, and never overwrites a file: if something with the same name is there now, the restored file is named “(restored)”.
- If your data folder is somewhere else, use **Browse…**.
- Every session writes a log to the data folder's `logs` folder (**Open log folder**).

From the command line:

```
"PC Tidy Rescue.exe" --list              list runs, newest first
"PC Tidy Rescue.exe" --undo <runId>      undo one run (ids are shown by --list)
"PC Tidy Rescue.exe" --undo-last         undo the newest run (asks first; add --yes to skip the question)
"PC Tidy Rescue.exe" --resume <runId>    finish an interrupted run
"PC Tidy Rescue.exe" --rollback <runId>  undo an interrupted run
"PC Tidy Rescue.exe" --restore-quarantine <runId>
"PC Tidy Rescue.exe" --data <folder>     use a data folder somewhere else
```

## Uninstall

Open **Settings > Apps > Installed apps**, find **PC Tidy**, choose **Uninstall**. Windows asks for admin permission once (PC Tidy is in Program Files).

You'll be asked whether to **also delete your PC Tidy data**. The answer is **No** unless you tick the box, because your data folder holds the **quarantine** (files PC Tidy set aside so you can get them back) and your history. If you keep it, PC Tidy Rescue stays in it and can still put things back.

The uninstaller removes PC Tidy's files and shortcuts, and anything PC Tidy added to Windows: its right-click menu entries, “start with Windows”, and its scheduled tasks. Nothing else. The uninstaller file itself is removed the next time you restart.

Using the portable zip? Close PC Tidy and delete its folder. Your data is in `%LOCALAPPDATA%\PC Tidy` if you want to remove that too.

## Privacy

**Nothing leaves your PC.** No telemetry, no account, no ads, no tracking. PC Tidy works fully offline. Details: [PRIVACY.md](PRIVACY.md).

## System requirements

- **Windows 10 (22H2)** or **Windows 11**, 64-bit (x64).
- About 300 MB of disk space for the program (more for the quarantine, which you control).
- No .NET or other downloads needed: everything is included.
- Works with standard and administrator accounts. Installing asks for admin permission once. Steps that need admin rights (a few system cleanups) ask Windows for permission once per run; everything else runs as you. Admin steps only work in the installed copy (Program Files), not a portable one.

## FAQ

**Is it really safe?**
PC Tidy is designed so that a mistake is always recoverable: you confirm everything, removed files go to quarantine, every run can be undone, and a crash can't leave things half done. Things that truly can't be undone are marked in red and you must tick them yourself.

**Will it delete my photos or documents?**
No. Cleanup never touches your Documents, Pictures, Desktop, Music or Videos. Organizing only moves files when you ask, and that can be undone.

**Does it clean the registry?**
No. “Registry cleaning” doesn't make PCs faster and can break things, so PC Tidy doesn't offer it. The app has a page called “What we don't clean, and why”.

**Where is the quarantine, and how long are files kept?**
In your data folder (`%LOCALAPPDATA%\PC Tidy\Quarantine`). They're kept 7 days by default (changeable in Settings), and PC Tidy asks before emptying it.

**PC Tidy won't start. How do I undo something?**
Use **PC Tidy Rescue** (see above).

**Why is the download so big?**
It includes everything it needs to run (the .NET runtime), so you don't have to install anything else.

**Can I move PC Tidy's data to another drive?**
Yes, in Settings. PC Tidy Rescue finds it automatically.

## For developers

PC Tidy is a .NET 8 WPF app (`PcTidy.sln`). You need the .NET 8 SDK on Windows.

```powershell
dotnet build PcTidy.sln                                   # build everything
dotnet test PcTidy.sln                                    # all tests (they only use the testbed\ folder and HKCU\Software\PCTidyTest)
dotnet run --project src\PcTidy.App -- --demo             # run with sample data
dotnet run --project tools\PcTidy.TestbedGen -- generate testbed\try   # generate a fake messy PC
dotnet run --project src\PcTidy.App -- --sandbox testbed\try           # run the app against it
.\build-release.ps1                                       # release: publish\PC Tidy Setup <v>.exe, portable zip, PC Tidy Rescue <v>.exe, SHA256SUMS.txt
```

- The version lives in `src\Directory.Build.props`.
- `build-release.ps1` publishes the app, Rescue and Setup self-contained single-file for win-x64 (the app, Rescue and the installed `uninstall.exe` keep WPF's native DLLs next to the exe instead of unpacking them to `%TEMP%`, and have startup hooks switched off, because they also run with admin rights), appends the app payload to Setup (format documented in `src\PcTidy.Setup\Payload\PayloadFile.cs`), builds the standalone `PC Tidy Rescue <v>.exe` separately as one self-contained file (WPF's native DLLs inside, so it runs on its own; it never runs with admin rights, so its checksum differs from the Rescue inside the payload and the zip), writes `SHA256SUMS.txt` for the Setup exe, the portable zip and the standalone Rescue exe, and also builds `installer\PcTidy.iss` (per-machine, `PrivilegesRequired=admin`) if Inno Setup 6 is installed.
- A developer build (`dotnet run`, `bin\`) is like a portable copy: admin steps are skipped. Test them from an installed build.
- Tests never change the real PC: file tests run in `testbed\<area>\`, registry tests only under `HKCU\Software\PCTidyTest\<area>`.

**Licence:** PC Tidy is not released under an open-source licence yet, so all rights are reserved: you're welcome to download and use the published builds and to read the code, but please don't redistribute it. Third-party components keep their own licences: see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt). Changes are listed in [CHANGELOG.md](CHANGELOG.md).
