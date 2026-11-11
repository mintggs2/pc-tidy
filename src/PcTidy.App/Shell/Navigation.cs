using System.Reflection;
using System.Reflection.Emit;
using PcTidy.Core.Model;
using Wpf.Ui.Abstractions;

namespace PcTidy.App.Shell;

/// <summary>
/// WPF-UI's NavigationView navigates by page TYPE, but feature modules hand us page INSTANCES (or none:
/// then the shell builds a StandardFlowPage). So every module gets its own tiny marker type, used only as a
/// lookup key; the page provider maps that key to the module's page. The marker types are never instantiated.
/// </summary>
public static class NavKeys
{
    private static readonly ModuleBuilder Builder =
        AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("PcTidy.NavKeys"), AssemblyBuilderAccess.Run).DefineDynamicModule("NavKeys");
    private static readonly Dictionary<string, Type> ById = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<Type, string> ByType = new();

    public static Type For(string moduleId)
    {
        lock (ById)
        {
            if (ById.TryGetValue(moduleId, out var t)) return t;
            var safe = new string(moduleId.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
            t = Builder.DefineType($"PcTidy.NavKeys.Module_{safe}_{ById.Count}", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract).CreateType();
            ById[moduleId] = t;
            ByType[t] = moduleId;
            return t;
        }
    }

    public static string? ModuleIdOf(Type t)
    {
        lock (ById) return ByType.TryGetValue(t, out var id) ? id : null;
    }
}

/// <summary>Creates each page once (built-in pages and module pages) and hands it to the NavigationView.</summary>
public sealed class PageProvider : INavigationViewPageProvider
{
    private readonly Dictionary<Type, Func<object>> _factories = new();
    private readonly Dictionary<Type, object> _pages = new();

    public void Register(Type key, Func<object> factory) => _factories[key] = factory;

    public object? GetPage(Type pageType)
    {
        if (_pages.TryGetValue(pageType, out var page)) return page;
        if (!_factories.TryGetValue(pageType, out var f)) return null;
        page = f();
        _pages[pageType] = page;
        return page;
    }

    /// <summary>The page for a key if it was created already (never creates one).</summary>
    public bool IsCreated(Type pageType, out object? page) => _pages.TryGetValue(pageType, out page);

    /// <summary>Gets (creating if needed) a page by key, for code that drives pages (Home actions, screenshots).</summary>
    public T? Get<T>(Type key) where T : class => GetPage(key) as T;
}

/// <summary>Rail grouping from Plan §3; data-driven from IFeatureModule.Group / Order so new sections just appear.</summary>
public static class NavGroups
{
    public static readonly IReadOnlyList<(NavGroup Group, string Header)> Body = new[]
    {
        (NavGroup.Home, ""),
        (NavGroup.CleanUp, "CLEAN UP"),
        (NavGroup.SpeedUp, "SPEED UP"),
        (NavGroup.Organize, "ORGANIZE"),
        (NavGroup.You, "YOU"),
    };
}
