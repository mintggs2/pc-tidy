namespace PcTidy.Core.Model;

/// <summary>Safety badge shown on every scan item.</summary>
public enum Badge
{
    /// <summary>Rebuilt automatically / clearly safe. May be ticked by default.</summary>
    Safe,
    /// <summary>Your call. Never ticked by default.</summary>
    Check,
    /// <summary>Cannot be undone. Never ticked by default; needs a second "I understand" tick.</summary>
    Permanent,
}

public enum ScanDepth { Quick, Deep }

/// <summary>Real = change things. Simulate = produce a "what would happen" report, change nothing.</summary>
public enum ExecutionMode { Real, Simulate }

public enum Release { V1 = 1, V2 = 2, V3 = 3 }

/// <summary>Navigation group in the left rail.</summary>
public enum NavGroup { Home, CleanUp, SpeedUp, Organize, You, Footer }

/// <summary>
/// Every kind of change PC Tidy can make. Feature projects only DESCRIBE changes with these;
/// the engine (PcTidy.Engine + PcTidy.Platform handlers) carries them out.
/// New kinds are added by the lead only.
/// </summary>
public enum ActionKind
{
    // ---- files (handled by PcTidy.Engine) ----
    /// <summary>Move a file or folder into quarantine (restorable). SourcePath.</summary>
    QuarantinePath,
    /// <summary>Move a file or folder to DestinationPath (same or other drive; cross-drive = copy, verify, remove).</summary>
    MovePath,
    /// <summary>Rename SourcePath to DestinationPath (same folder or same drive).</summary>
    RenamePath,
    /// <summary>Create the folder DestinationPath (undo removes it only if still empty).</summary>
    CreateDirectory,
    /// <summary>Move folder SourcePath to DestinationPath and leave a junction at SourcePath.</summary>
    MoveFolderWithJunction,
    /// <summary>Zip SourcePath into DestinationPath (.zip), verify, then quarantine the original.</summary>
    ArchiveToZip,
    /// <summary>Unzip SourcePath (.zip) to DestinationPath.</summary>
    ExtractZip,
    /// <summary>Write a small text file (e.g. a report). DestinationPath + Args["content"].</summary>
    WriteTextFile,

    // ---- registry / services / tasks / startup (handled by PcTidy.Platform) ----
    RegistrySetValue,
    RegistryDeleteValue,
    RegistryDeleteKey,
    ServiceSetStartType,
    ScheduledTaskSetEnabled,
    /// <summary>Task-Manager-style enable/disable via StartupApproved. Args: "location","name". Enable = on/off.</summary>
    StartupItemSetEnabled,

    // ---- system (handled by PcTidy.Platform) ----
    /// <summary>Empty the current user's Recycle Bin (permanent).</summary>
    EmptyRecycleBin,
    /// <summary>Run a whitelisted system tool. ToolId + Args. See ToolIds.</summary>
    RunSystemTool,
    /// <summary>Redirect a Windows known folder. Args: "folderId"; DestinationPath.</summary>
    KnownFolderRedirect,
    /// <summary>Remove a Store app for the current user. Args: "packageFullName".</summary>
    AppxRemoveForUser,
    /// <summary>Run an installed app's own uninstaller. Args: "uninstallString" | "msiProductCode".</summary>
    RunUninstaller,
    /// <summary>Open a URL / settings page / launcher screen. Harmless, not undoable. Args: "target".</summary>
    OpenExternal,
    /// <summary>Set the active power plan. Args: "schemeGuid".</summary>
    PowerPlanSet,
    /// <summary>Comment out lines in the hosts file. Args: "lines" (\n separated).</summary>
    HostsCommentOut,
    /// <summary>Add/remove a per-user shell verb (right-click entry). Args: "verb","command"; Enable.</summary>
    ShellVerbSetInstalled,

    // ---- v2 additions ----
    /// <summary>
    /// Replace a text/config file's content with Args["content"], keeping a backup of the original (undo restores it).
    /// For game config edits, Steam launch options (localconfig.vdf), etc. Engine handler. Target app must be closed.
    /// </summary>
    EditTextFile,
    /// <summary>Overwrite then delete a file. PERMANENT, no quarantine. Double confirm in UI. Engine handler.</summary>
    ShredFile,
    /// <summary>Block/unblock a shell extension or verb (Shell Extensions\Blocked or LegacyDisable). Args: "clsid"|"verbKey"; Enable.</summary>
    ShellExtensionSetEnabled,
    /// <summary>Delete browser cookies except whitelisted sites. Args: "browser","profileDir","keepHosts" (\n). Backup first.</summary>
    CookiesDeleteExcept,
    /// <summary>Delete a restore point. Args: "sequenceNumber". Not reversible.</summary>
    RestorePointDelete,
    /// <summary>Delete a saved Wi-Fi profile. Args: "profileName","interfaceGuid". Backup (XML export) first.</summary>
    WlanProfileDelete,
    /// <summary>Delete a leftover local user profile via DeleteProfile. Args: "sid". Never current/loaded profiles.</summary>
    UserProfileDelete,
    /// <summary>Disable/remove an optional feature or capability. Args: "name","kind"=feature|capability. Re-add path recorded.</summary>
    OptionalFeatureRemove,
    /// <summary>Optimize a volume (ReTrim SSD / defrag HDD) via MSFT_Volume.Optimize. Args: "drive". Not reversible, harmless.</summary>
    VolumeOptimize,
    /// <summary>Politely close an app (WM_CLOSE). Args: "pid","processName". Never system/security/anti-cheat. Not reversible.</summary>
    CloseApp,
    /// <summary>Re-register Store app(s) / run wsreset. Args: "packageFamilyName" or "wsreset". Not reversible, harmless.</summary>
    StoreRepair,

    // ---- v3 additions ----
    /// <summary>
    /// Create (or replace) one of PC Tidy's OWN scheduled tasks under the "\PC Tidy\" folder for the current user.
    /// Args: "name" (no backslashes), "schedule" = weekly|monthly|daily|logon, "day" (e.g. Sunday / 1), "time" (HH:mm),
    /// "arguments" (only PC Tidy's own report-only switches, e.g. "--scheduled-scan"). The action always runs PcTidy.exe itself.
    /// Undo deletes it (or restores the previous definition).
    /// </summary>
    ScheduledTaskCreate,
    /// <summary>Delete one of PC Tidy's own tasks under "\PC Tidy\". Args: "name". Undo re-creates it from the saved definition.</summary>
    ScheduledTaskDelete,
    /// <summary>
    /// Start PC Tidy with Windows (HKCU Run value "PC Tidy" = "&lt;PcTidy.exe&gt; --tray") or remove it. Enable = true/false.
    /// Respects Task Manager's StartupApproved flag. Undo restores the previous state.
    /// </summary>
    StartWithWindowsSet,
    /// <summary>
    /// Copy a file or folder to DestinationPath (never overwrites; AutoNumber clash), verify by hash, leave the original.
    /// Undo quarantines the copy. Used for archiving AI-agent transcripts (moving them would break resume).
    /// </summary>
    CopyPath,
    /// <summary>
    /// Start a user program again, unelevated (e.g. apps Game boost closed). Args: "exePath" (an existing .exe that is not
    /// a Windows/system tool), "arguments" (optional, as recorded when it was closed). Harmless, not reversible.
    /// </summary>
    LaunchApp,
    /// <summary>
    /// OneDrive "Free up space": mark a fully downloaded file inside a OneDrive folder online-only (UNPINNED, clear PINNED),
    /// so OneDrive removes the local copy but keeps it in the cloud. SourcePath. Undo restores the previous pin state
    /// (the file re-downloads when opened). Never for files outside a cloud root.
    /// </summary>
    OneDriveFreeUpSpace,
    /// <summary>Pin a folder to Explorer's Quick access via the shell's own "pintohome" verb. SourcePath = folder. Undo unpins.</summary>
    QuickAccessPin,
}

public enum GpuVendor { Unknown, Nvidia, Amd, Intel, Microsoft, Other }

public enum DriveMedia { Unknown, Ssd, Hdd, Removable, Network }

public enum StepStatus { Pending, Started, Done, Skipped, Failed, Undone, UndoFailed, Simulated }

public enum RunStatus
{
    Planned,
    Running,
    Completed,
    CompletedWithSkips,
    Failed,
    /// <summary>Process died mid-run; can be resumed or rolled back.</summary>
    Interrupted,
    RolledBack,
    Undone,
    PartiallyUndone,
    Simulated,
}

public enum ServiceStart { Boot, System, Automatic, AutomaticDelayed, Manual, Disabled }

public enum NameClashPolicy { AutoNumber, Skip, Overwrite }

public enum RegistryHive { CurrentUser, LocalMachine }

public enum RegistryKind { String, ExpandString, DWord, QWord, Binary, MultiString }

/// <summary>Protection level of a path.</summary>
public enum PathProtection
{
    /// <summary>Ordinary path.</summary>
    None,
    /// <summary>Allowed only after the user granted permission for this folder (OneDrive, user libraries for non-organize actions, code projects).</summary>
    AskFirst,
    /// <summary>Never touched (system, apps, games, PC Tidy's own data...). Carve-outs may apply for specific cleanup targets.</summary>
    Never,
}
