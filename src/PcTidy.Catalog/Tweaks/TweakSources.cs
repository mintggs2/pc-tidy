namespace PcTidy.Catalog.Tweaks;

/// <summary>
/// Source links behind the verdicts (Tweaks.md "Sources" S1-S22 and the Community tweaks appendices).
/// Shown as links on every row so users can check the evidence themselves.
/// </summary>
public static class TweakSources
{
    // Microsoft documentation
    public const string Mmcss = "https://learn.microsoft.com/windows/win32/procthread/multimedia-class-scheduler-service";
    public const string DiagnosticData = "https://learn.microsoft.com/windows/privacy/configure-windows-diagnostic-data-in-your-organization";
    public const string WindowsAiCsp = "https://learn.microsoft.com/windows/client-management/mdm/policy-csp-windowsai";
    public const string MemoryIntegrity = "https://learn.microsoft.com/windows/security/hardware-security/enable-virtualization-based-protection-of-code-integrity";
    public const string ExperienceCsp = "https://learn.microsoft.com/windows/client-management/mdm/policy-csp-experience";
    public const string PrivacyCsp = "https://learn.microsoft.com/windows/client-management/mdm/policy-csp-privacy";
    public const string SearchCsp = "https://learn.microsoft.com/windows/client-management/mdm/policy-csp-search";
    public const string UpdateCsp = "https://learn.microsoft.com/windows/client-management/mdm/policy-csp-update";
    public const string DeliveryOptimization = "https://learn.microsoft.com/windows/deployment/do/waas-delivery-optimization-reference";
    public const string ManageConnections = "https://learn.microsoft.com/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services";
    public const string Endpoints = "https://learn.microsoft.com/windows/privacy/manage-windows-11-endpoints";
    public const string IotServices = "https://learn.microsoft.com/windows/iot/iot-enterprise/optimize/services";
    public const string PerUserServices = "https://learn.microsoft.com/windows/application-management/per-user-services-in-windows";
    public const string Bcdedit = "https://learn.microsoft.com/windows-hardware/drivers/devtest/bcdedit--set";
    public const string BitLockerBcd = "https://learn.microsoft.com/windows/security/operating-system-security/data-protection/bitlocker/bcd-settings-and-bitlocker";
    public const string Qpc = "https://learn.microsoft.com/windows/win32/sysinfo/acquiring-high-resolution-time-stamps";
    public const string TimeBeginPeriod = "https://learn.microsoft.com/windows/win32/api/timeapi/nf-timeapi-timebeginperiod";
    public const string Ipv6 = "https://learn.microsoft.com/troubleshoot/windows-server/networking/configure-ipv6-in-windows";
    public const string NetworkTuning = "https://learn.microsoft.com/windows-server/networking/technologies/network-subsystem/net-sub-performance-tuning-nics";
    public const string Smb1 = "https://learn.microsoft.com/windows-server/storage/file-server/troubleshoot/detect-enable-and-disable-smbv1-v2-v3";
    public const string RemoveAppx = "https://learn.microsoft.com/powershell/module/appx/remove-appxpackage";
    public const string Hibernation = "https://learn.microsoft.com/troubleshoot/windows-client/setup-upgrade-and-drivers/disable-and-re-enable-hibernation";
    public const string Uac = "https://learn.microsoft.com/windows/security/application-security/application-control/user-account-control/settings-and-configuration";
    public const string DefenderDisableAntiSpyware = "https://learn.microsoft.com/windows-hardware/customize/desktop/unattend/security-malware-windows-defender-disableantispyware";
    public const string DefenderTamperProtection = "https://learn.microsoft.com/defender-endpoint/prevent-changes-to-security-settings-with-tamper-protection";
    /// <summary>Defender's policy settings and the registry values they set (group policy mappings).</summary>
    public const string DefenderPolicySettings = "https://learn.microsoft.com/windows/client-management/mdm/policy-csp-defender";
    public const string SpectreGuidance = "https://support.microsoft.com/topic/kb4073119-windows-client-guidance-for-it-pros-to-protect-against-silicon-based-microarchitectural-and-speculative-execution-side-channel-vulnerabilities-35820a8a-ae13-1299-88cc-357f104f5b11";
    public const string ExecutionPolicy = "https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_execution_policies";
    public const string FsutilBehavior = "https://learn.microsoft.com/windows-server/administration/windows-commands/fsutil-behavior";
    public const string Pagefile = "https://learn.microsoft.com/troubleshoot/windows-client/performance/introduction-to-the-page-file";
    public const string VulnerableDriverBlocklist = "https://learn.microsoft.com/windows/security/application-security/application-control/app-control-for-business/design/microsoft-recommended-driver-block-rules";
    public const string Sehop = "https://learn.microsoft.com/defender-endpoint/exploit-protection-reference";
    public const string Firewall = "https://learn.microsoft.com/windows/security/operating-system-security/network-security/windows-firewall/";
    public const string SmartScreen = "https://learn.microsoft.com/windows/security/operating-system-security/virus-and-threat-protection/microsoft-defender-smartscreen/";
    public const string SystemRestore = "https://support.microsoft.com/windows/system-restore-a5ae3ed9-07c4-fd56-45ee-096777ecd14e";
    public const string GameMode = "https://support.xbox.com/help/games-apps/game-setup-and-play/use-game-mode-gaming-on-pc";
    public const string GraphicsSettings = "https://support.microsoft.com/windows/optimizations-for-windowed-games-in-windows-11-3f006843-2c7e-4ed0-9a5e-f9389e535952";
    public const string DirectXBlog = "https://devblogs.microsoft.com/directx/";
    public const string Hags = "https://devblogs.microsoft.com/directx/hardware-accelerated-gpu-scheduling/";
    public const string FileExplorerHelp = "https://support.microsoft.com/windows/file-explorer-in-windows-ef370130-1cca-9dc5-e0df-2f7416fe1cb1";
    public const string TaskbarHelp = "https://support.microsoft.com/windows/customize-the-taskbar-in-windows-0657a50f-0cc7-dbfd-ae6b-05020b195b07";
    public const string PrivacySettings = "https://support.microsoft.com/windows/windows-privacy-settings";
    public const string PowerMode = "https://support.microsoft.com/windows/change-the-power-mode-for-your-windows-pc-c2aff038-22c9-f46d-5ca0-78696fdf2de8";
    public const string FastStartup = "https://learn.microsoft.com/troubleshoot/windows-client/setup-upgrade-and-drivers/fast-startup-causes-hibernation-related-issues";
    public const string DriverUpdates = "https://learn.microsoft.com/windows-hardware/drivers/install/how-setup-selects-drivers";
    public const string DefenderCloudProtection = "https://learn.microsoft.com/defender-endpoint/enable-cloud-protection-microsoft-defender-antivirus";
    public const string ClearPagefileAtShutdown = "https://learn.microsoft.com/previous-versions/windows/it-pro/windows-10/security/threat-protection/security-policy-settings/shutdown-clear-virtual-memory-pagefile";
    public const string MemoryCompression = "https://learn.microsoft.com/powershell/module/mmagent/enable-mmagent";
    public const string WriteCacheFlushing = "https://devblogs.microsoft.com/oldnewthing/20100909-00/?p=12913";
    public const string SmartAppControl = "https://learn.microsoft.com/windows/apps/develop/smart-app-control/test-your-app-with-smart-app-control";
    public const string NetAdapterBindings = "https://learn.microsoft.com/powershell/module/netadapter/get-netadapterbinding";
    public const string CleanBoot = "https://support.microsoft.com/topic/how-to-perform-a-clean-boot-in-windows-da2f9573-6eec-00ad-2f8a-a97a1807f3dd";
    public const string KnownBadDrivers = "https://www.loldrivers.io/";
    public const string WindowsBuiltInAdmin ="https://learn.microsoft.com/windows-hardware/manufacture/desktop/enable-and-disable-the-built-in-administrator-account";
    public const string SystemRequirements = "https://support.microsoft.com/windows/installing-windows-11-on-devices-that-don-t-meet-minimum-system-requirements-0b2dc4a2-5933-4ad4-9c09-ef0a331518f1";
    public const string Copilot = "https://support.microsoft.com/topic/getting-started-with-copilot-on-windows-1159c61f-86c3-4755-bf83-7fbff7e0982d";
    public const string LsaProtection = "https://learn.microsoft.com/windows-server/security/credentials-protection-and-management/configuring-additional-lsa-protection";
    public const string LsaProtectionInWindowsSecurity = "https://learn.microsoft.com/windows/security/book/identity-protection-advanced-credential-protection";
    public const string DefenderStatus = "https://learn.microsoft.com/previous-versions/windows/desktop/defender/msft-mpcomputerstatus";
    public const string DefenderPreferences = "https://learn.microsoft.com/powershell/module/defender/set-mppreference";
    public const string UpdateClientPolicies = "https://learn.microsoft.com/windows/deployment/update/waas-configure-wufb";
    public const string BcdBootOptions = "https://learn.microsoft.com/previous-versions/windows/desktop/bcd/mapping-boot-options-to-elements";
    public const string DeviceDisabled = "https://learn.microsoft.com/windows-hardware/drivers/install/cm-prob-disabled";
    public const string PowerSchemes = "https://learn.microsoft.com/windows/win32/power/power-setting-guids";

    // Measured latency tests (Community tweaks §9.7): camera / LDAT end-to-end input-lag measurements
    public const string FrameCapLatencyTest = "https://www.youtube.com/watch?v=7CKnJ5ujL_Q";
    public const string Cs2LatencyTests = "https://www.youtube.com/watch?v=zNHFAAJkIvo";

    // Community research
    public const string DjdallmannGamingPcSetup = "https://github.com/djdallmann/GamingPCSetup";
    public const string PcTuning = "https://github.com/valleyofdoom/PC-Tuning";
    public const string RandomAsciiTimers = "https://randomascii.wordpress.com/2020/10/04/windows-timer-resolution-the-great-rule-change/";
    public const string BlurBustersTimers = "https://forums.blurbusters.com/viewtopic.php?t=15665";
    public const string MelodysTimerTests = "https://sites.google.com/view/melodystweaks";
    public const string WinUtilTweaks = "https://winutil.christitus.com/guides/tweaks";
    public const string Win11DebloatRevert = "https://github.com/Raphire/Win11Debloat/wiki/Reverting-Changes";
    public const string PrivacySexy = "https://privacy.sexy";
    public const string AtlasSecurity = "https://docs.atlasos.net";
    public const string RobloxFastFlags = "https://devforum.roblox.com/t/allowlist-for-local-client-configuration-via-clientappsettingsjson/3966569";
    public const string RiotVanguard = "https://support-valorant.riotgames.com/hc/en-us/articles/22291331362067";
}
