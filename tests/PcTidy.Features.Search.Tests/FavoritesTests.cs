using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Search.Favorites;
using Xunit;

namespace PcTidy.Features.Search.Tests;

/// <summary>PC Tidy's own Favorites (in settings) and the explicit, undoable "Pin to Quick access".</summary>
[Collection("search-testbed")]
public class FavoritesTests
{
    [Fact]
    public void Add_remove_reorder_and_rename_persist_in_settings()
    {
        var settings = new AppSettings();
        var favs = new FavoritesList(settings);
        Assert.True(favs.Add(@"X:\Users\tester\Documents\Taxes"));
        Assert.True(favs.Add(@"X:\Photos\", "My photos"));
        Assert.True(favs.Add(@"Y:\"));
        Assert.False(favs.Add(@"x:\photos")); // same folder, other spelling
        Assert.Equal(new[] { "Taxes", "My photos", @"Y:\" }, favs.Items.Select(f => f.Label));

        Assert.True(favs.Move(2, 0));
        Assert.Equal(@"Y:\", favs.Items[0].Path);
        Assert.False(favs.Move(5, 0));
        Assert.True(favs.Move(0, 99)); // clamped to the end
        Assert.Equal(@"Y:\", favs.Items[^1].Path);
        Assert.True(favs.Rename(@"X:\Photos", "Pictures"));
        Assert.True(favs.Remove(@"X:\Users\tester\Documents\Taxes"));
        Assert.False(favs.Remove(@"X:\Nope"));

        // Stored with SetFeature("search", ...) and read back by a fresh list.
        Assert.True(settings.Features.ContainsKey(SearchIds.FeatureId));
        var again = new FavoritesList(settings);
        Assert.Equal(new[] { "Pictures", @"Y:\" }, again.Items.Select(f => f.Label));
        // Other Search settings survive a favorites save.
        var state = SearchSettings.Load(settings);
        state.UseEverything = false;
        state.Store(settings);
        new FavoritesList(settings).Add(@"X:\More");
        Assert.False(SearchSettings.Load(settings).UseEverything);
    }

    [Fact]
    public void The_list_has_a_limit_and_refuses_junk()
    {
        var favs = new FavoritesList(new AppSettings());
        Assert.False(favs.Add(""));
        Assert.False(favs.Add("   "));
        for (int i = 0; i < FavoritesList.MaxFavorites; i++) Assert.True(favs.Add($@"X:\F{i}"));
        Assert.False(favs.Add(@"X:\one too many"));
    }

    [Fact]
    public void Pin_to_Quick_access_is_one_explicit_undoable_plan_item_the_real_guard_allows()
    {
        using var s = new SearchSandbox("pin", SandboxProfile.Minimal);
        var folder = Path.Combine(s.K.Documents, "Taxes");
        Directory.CreateDirectory(folder);
        var plan = FavoritesList.PinPlan(folder);
        var item = Assert.Single(plan.Items);
        Assert.Equal(ActionKind.QuickAccessPin, item.Kind);
        Assert.Equal(SearchIds.FeatureId, item.FeatureId);
        Assert.Equal(folder, item.SourcePath);
        Assert.True(item.Reversible);
        Assert.False(item.IsSystemChange);
        Assert.Contains("Undo unpins", item.Description);
        Assert.True(s.Host.Safety.Check(item).Allowed);
        Assert.False(s.Host.Safety.Check(FavoritesList.PinPlan(Path.Combine(s.K.Documents, "missing")).Items[0]).Allowed);
    }
}
