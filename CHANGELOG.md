# Changelog

All notable changes to PC Tidy are listed here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

PC Tidy is a **beta**. Every change it makes can be previewed, simulated and undone, but please try it on a PC you have a backup of.

## [Unreleased]

## [3.1.2] - 2026-11-11

Fixes from the first real use of 3.1.1.

### Fixed
- **Steps that need admin rights now run.** PC Tidy's admin helper quit as soon as it started, so every step that needs admin rights was skipped ("PC Tidy's admin helper closed before it started"). The scheduled report-only scan had the same problem and now runs too.
- **Big clean-ups no longer get stuck at the safety limit.** When a run would move more than 50,000 files or 200 GB, PC Tidy now measures it and asks once, with the real numbers ("This moves 58,120 files (9.4 GB) to quarantine. Continue?"). Before, the run stopped with "Confirm the size to go ahead", but there was nothing to confirm with. Admin steps keep their own limit per step: PC Tidy sends them in smaller parts. The measured size covers the folders PC Tidy can read without admin rights (admin-only folders such as Windows' own temp folder aren't in that number; the admin helper limits those itself).
- **The preview shows the real size of what can't be undone.** What goes for good (for example emptying the Recycle Bin) now shows its size in its own tile, in the selection line and in the confirm box, and is never folded into the "Clean" number. The quarantine tile shows how many files are inside the picked folders, as well as the item count.
- **Hibernation: "Reduced" and "off" can't both be ticked.** Ticking the group picked both changes.
- **Admin prompt declined or timed out:** the Done card says so and offers "Try the admin steps again", which runs only those steps (after the usual confirm). When the admin helper can't start, the message now says why.
- The Services rail counter is short ("5 changed"), so the name "Services" is no longer cut off.

## [3.1.1] - 2026-11-11

### Added
- **Updates from inside PC Tidy**: PC Tidy checks GitHub for a newer version (at most once a day, and from Settings > Check for updates), shows what's new and asks before doing anything: Update now, Later, or Skip this version. It never updates by itself and never while something is running. The download is checked against the release's `SHA256SUMS.txt` before it's used. Installed copies update with one Windows admin prompt and keep your settings, quarantine and history; portable copies get the new version unpacked into a new folder next to the old one. You can switch the automatic check off in Settings. Copies older than 3.1.1 don't have this yet, so update those by hand once.
- **Resume last scan**: a scan saves its progress after each finished section, so a restart, crash or closed window no longer starts it from zero. On the next start PC Tidy offers Resume or Start over. Saved progress is dropped after 24 hours; if Windows was updated in between, sections that read Windows settings are scanned again, and a section whose files changed a lot is scanned again too.
- Before a long scan, a note says when Windows Update is waiting to restart the PC (PC Tidy only reads this, it never changes update settings).

### Changed
- PC Tidy's own code is now released under the MIT License (see `LICENSE`), and `LICENSE` ships with the app. Third-party components keep their own licences.
- PC Tidy now goes online for the update check (on by default, can be switched off; it sends only its version number to GitHub). See `PRIVACY.md`.

## [3.1.0] - 2026-11-11

Built from PC Tidy's research into what "FPS boost" tweaks actually do (215 tweaks checked against published measurements). Only things with a measured, safe effect can be applied; tweaks that were measured to do nothing are shown with their numbers instead; harmful ones are found and put back.

### Added
- **Measure**: import frame-time recordings you made with PresentMon, CapFrameX, OCAT or FrameView (3+ runs before, 3+ after) and see average FPS, 1% lows, P99 frame time and stutters, with a cautious verdict: Better, Worse, No measurable change, or Too noisy to say. Opens from "Check if this helped" after a run. PC Tidy doesn't record anything itself.
- **PC care**: **Fixes** (Wi-Fi, Bluetooth, Windows Update, Store, Windows Installer errors 2502/2503, game launchers), **adapter power saving off** (per network adapter, with undo) and **Lag check** (idle latency vs latency under load). The lag check is switched off until a test server whose terms allow it is chosen.
- **Tweaks**: search, category chips, "N of M on" from your PC's real settings, a card view, presets that show what they'll change before they tick anything, and a **"Doesn't help (measured)"** shelf: nearly 50 popular tweaks with the published measurement that found no gain.
- **Bad-tweak finder**: Defender real-time protection off, Tamper Protection off (opens Windows Security), BBR2 on Windows 11 24H2, jumbo frames, apps stuck at Realtime priority, a leftover safe-boot flag (one click to start normally again), leftover BIOS-tool and low-level helper drivers, "PC optimizer" suites and registry cleaners, Edge WebView2 missing, and more. Every harmful tweak from the research is checked or explained.
- **Games**: Optimize is now a game list with the details beside it (Tuned, Not tuned, Info only, Not installed), measured numbers on each setting, a **Tips** tab (cap your FPS, Reflex and Anti-Lag, why a game is slow, shader cache), and **GPU choice per game** on laptops with two GPUs.
- **Insights**: cards with measured numbers (NVIDIA App filters, Memory Integrity cost, Windows version vs CPU, Ryzen X3D with two core groups, refresh rate, RAM, Resizable BAR, debloated Windows), **install missing runtimes** (Visual C++, DirectX, .NET Desktop) through winget, and a **System info** window (no serial numbers or network addresses shown).
- **App**: a "More" group in the rail, factual counters on rail items (Tweaks "N of M on", Services "N changed from Windows default", new runs in History), an optional live **Now** card on Home (off by default), per-section progress, **restore points in History**, and a replayable tour from Help.

### Changed
- Release file names have no spaces (`PC-Tidy-Setup-3.1.0.exe`, `PC-Tidy-3.1.0-portable.zip`, `PC-Tidy-Rescue-3.1.0.exe`), so they match `SHA256SUMS.txt` after download. (GitHub renamed the 3.0.0 files with dots: `PC.Tidy.Setup.3.0.0.exe` and so on; their checksums are unchanged.)
- While a task runs, other run buttons are disabled and say why.
- Confirm dialogs list everything that will change, with Back before Confirm.
- Services: a card explains why PC Tidy has no "service presets".

## [3.0.0] - 2026-11-10

### Added
- **Services**: every Windows service with what it does, who made it, how it starts and what depends on it. Changes come only from an evidence table (proven / situational / placebo / harmful / protected): proven items always, situational ones only in Expert mode, never anything critical or anti-cheat. Every change keeps an exact backup of the start type and recovery settings. **Bad-services repair** puts back the Windows default for services other tools switched off.
- **AI chats**: imports ChatGPT and Claude exports and Claude Code / Codex transcripts into a private local library with search, tags and topics. Keys and tokens are masked before anything is stored. Exports to Obsidian. Archives old agent sessions (never a running one; never settings, logins or memory).
- **Search and navigation**: fast file search (uses Everything when it's installed and trusted, otherwise PC Tidy's own index), favourites, and "Suggest a folder structure".
- **Insights**: check-up cards (stability, backups, drives, display, memory, BitLocker, Smart App Control and more) and **Scan this script**: reads a tweak script as text and explains what it would change, without running it.
- **Game boost**, **Schedule and reports** (weekly scan, monthly checkup, drift check), **Ask PC Tidy** (local AI helper, optional), **Learned** (what PC Tidy has learned from your choices, all editable), **Privacy sweep** and **Notifications**.
- **Organize**: Google Takeout sorter, one-by-one keyboard triage, rule presets.
- **Duplicates**: very large files compared quickly and only removed after a full check, side-by-side compare, wrong-file-extension finder.
- **App**: optional tray icon and alerts, "What is this?" and "Tidy this folder" in Explorer's right-click menu, settings export/import with a per-setting preview, update check (off until you set an address), clean uninstall.
- **Bad-tweak finder** checks for the Administrator account, System Protection, UAC, memory compression, pagefile, Defender policies, paused updates, LSA protection, network bindings, leftover low-level drivers and more.

### Changed
- A step can now depend on an earlier one (for example: driver removals only run if the driver backup finished).
- Cleanup results say "Freed X of about Y" and why any gap.

### Security
- Setup checks its payload against a hash built into the program and installs from an admin-only copy. Updates must be signed by the same publisher. Imported settings can't silently turn on anything that runs on its own.

## [2.0.0] - 2026-11-10

### Added
- **Duplicates and junk**, **Apps** (clean uninstall and leftovers), **Speed** (startup apps, tasks, context menu, "what's locking this file?"), **Tweaks** (from an evidence table, with a bad-tweak finder that repairs what other tools broke), **Drivers** (backup first, remove unused packages and ghost devices, restore from PC Tidy's own backups), **Games**, **Move to drive**, **Developer cleanup**, **PC care** (repairs, network, restore points, security status, shredder, health report), **Photos** and **Desktop tidy**.
- Expert mode for situational tweaks.

### Changed
- **PC Tidy now installs for everyone in Program Files** (one admin prompt), so the parts that run with admin rights can't be changed by other programs. A copy installed just for you by 1.0 is replaced; your data stays.

## [1.0.0] - 2026-11-10

### Added
- **Safety core.** Every action follows scan, preview, confirm, do. Nothing is deleted outright: removed files go to a quarantine you can restore from. Every run is written to a journal first, so it can be undone, resumed or rolled back, even after a crash or power cut. Protected places (Windows, apps, games, your own folders) are never touched. System changes make a restore point first.
- **Disk cleanup** of temporary files, caches, logs, crash dumps, old update leftovers and other safe-to-remove clutter, each with a plain-English reason. Things that can't be undone are marked in red and never ticked for you.
- **Space map**: see what's using space on each drive, and set big, old files aside.
- **Organize files**: sort Downloads (and other folders you choose) into category folders with rules you can edit, a rename helper, and an optional watch mode.
- **Optimize & Organize everything**: one run of the safe recommendations from every section, undoable as a whole.
- **Simulation mode**: a full "what would happen" report for any action, with no changes at all.
- **Quick and Deep scans.**
- **Home dashboard, Settings and first-run guide.**
- **PC Tidy Rescue**: a small separate tool that undoes PC Tidy's runs even if PC Tidy itself won't start.
- **PC Tidy Setup** and a **portable zip**.
- **Practice mode** (`PcTidy.exe --sandbox <folder>`) and a demo with sample data (`PcTidy.exe --demo`).
- Light and dark themes that follow Windows.
