namespace PcTidy.Platform;

/// <summary>The system handlers PcTidy.Platform provides. The lead adds these to the Engine's file handlers.</summary>
public static class PlatformHandlers
{
    /// <summary>Real handlers (they still only simulate when <c>IKnownLocations.IsSandbox</c> is true).</summary>
    public static IReadOnlyList<IActionHandler> CreateAll() => new IActionHandler[]
    {
        new RegistryHandler(),
        new ServiceStartTypeHandler(),
        new ScheduledTaskHandler(),
        new StartupApprovedHandler(),
        new RecycleBinHandler(),
        new SystemToolHandler(),
        new OpenExternalHandler(),
        // v2
        new KnownFolderRedirectHandler(),
        new AppxRemoveForUserHandler(),
        new UninstallerHandler(),
        new PowerPlanHandler(),
        new ShellVerbHandler(),
        new ShellExtensionHandler(),
        new RestorePointDeleteHandler(),
        new WlanProfileHandler(),
        new UserProfileDeleteHandler(),
        new OptionalFeatureHandler(),
        new VolumeOptimizeHandler(),
        new CloseAppHandler(),
        new StoreRepairHandler(),
        // v3
        new ScheduledTaskCreateHandler(),
        new ScheduledTaskDeleteHandler(),
        new StartWithWindowsHandler(),
        new LaunchAppHandler(),
        new QuickAccessPinHandler(),
    };
}
