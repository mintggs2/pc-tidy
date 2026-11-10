using System.Text.Json;
using PcTidy.Core.Model;
using PcTidy.Features.Assistant.Engine;
using PcTidy.Features.Assistant.Ollama;
using Xunit;

namespace PcTidy.Features.Assistant.Tests;

/// <summary>Every suggestion is checked against the allow-list and the real cached scan; answers are cleaned.</summary>
public class ValidationTests
{
    private readonly ScanResultsCache _cache = new();
    private readonly ScanResult _clean = Scans.Clean();
    private readonly ScanDataBlock _data;
    private readonly AssistantEngine _engine;

    public ValidationTests()
    {
        _cache.Publish(_clean);
        _data = ScanDataBlock.Build(_cache, Scans.System());
        _engine = new AssistantEngine(new OllamaClient(transport: new FakeOllama()), _cache, Scans.System(), id => id == "cleanup" ? "Clean" : id);
    }

    private ValidationReport Validate(string suggestionsJson)
    {
        using var doc = JsonDocument.Parse(suggestionsJson);
        return ReplyValidator.Validate(doc.RootElement.Clone(), _data, _cache, id => id == "cleanup" ? "Clean" : id);
    }

    private string H(string title) => Scans.HandleOf(_data, title);

    [Fact]
    public void A_valid_suggestion_maps_to_the_real_items_of_one_section()
    {
        var r = Validate($$"""[{"actionId":"review_in_section","itemIds":["{{H("Chrome cache")}}","{{H("setup-old.exe")}}","{{H("Chrome cache")}}"]}]""");
        var s = Assert.Single(r.Suggestions);
        Assert.Equal("cleanup", s.FeatureId);
        Assert.Equal("Review in Clean →", s.ButtonText);
        Assert.Equal(new[] { "Chrome cache", "setup-old.exe" }, s.Items.Select(i => i.Title));
        Assert.All(s.Items, i => Assert.Contains(i, _clean.AllItems));
        Assert.Equal(3_700_000_000, s.TotalBytes);
        var request = s.ToRequest();
        Assert.Equal(s.Items.Select(i => i.Id), request.ItemIds);
        Assert.Equal(_clean.FinishedUtc, request.ScanFinishedUtc);
        Assert.Empty(r.Rejected);
    }

    [Theory]
    [InlineData("delete_items")]
    [InlineData("run_command")]
    [InlineData("Review_In_Section")]
    [InlineData("review_in_section ")]
    [InlineData("")]
    public void Actions_off_the_allow_list_are_rejected(string action)
    {
        var r = Validate($$"""[{"actionId":"{{action}}","itemIds":["{{H("Chrome cache")}}"]}]""");
        Assert.Empty(r.Suggestions);
        Assert.Single(r.Rejected);
    }

    [Theory]
    [InlineData("i999")]
    [InlineData("C:\\\\Windows")]
    [InlineData("C:\\\\Users\\\\tester\\\\Downloads\\\\setup-old.exe")]
    [InlineData("../../Windows/System32")]
    [InlineData("i1; del C:\\\\Windows")]
    [InlineData("I1")]
    public void Ids_that_are_not_handles_from_this_data_are_rejected(string id)
    {
        var r = Validate($$"""[{"actionId":"review_in_section","itemIds":["{{id}}"]}]""");
        Assert.Empty(r.Suggestions);
        Assert.Contains(r.Rejected, x => x.StartsWith("unknown item"));
    }

    [Fact]
    public void Real_scan_item_ids_are_not_accepted_either()
    {
        var guid = _clean.AllItems.First().Id; // the model never sees these, and can't use them if guessed
        var r = Validate($$"""[{"actionId":"review_in_section","itemIds":["{{guid}}"]}]""");
        Assert.Empty(r.Suggestions);
    }

    [Fact]
    public void Permanent_and_protected_items_can_never_be_suggested()
    {
        // They have no handle, so every possible handle misses them.
        var everyHandle = string.Join(",", Enumerable.Range(1, 50).Select(i => $"\"i{i}\""));
        var r = Validate($$"""[{"actionId":"review_in_section","itemIds":[{{everyHandle}}]}]""");
        var s = Assert.Single(r.Suggestions);
        Assert.DoesNotContain(s.Items, i => i.Title is "Recycle Bin" or "Windows Update store");
        Assert.All(s.Items, i => Assert.True(ScanDataBlock.IsReviewable(i)));
    }

    [Fact]
    public void Suggestions_into_a_replaced_scan_are_rejected()
    {
        var rescanned = Scans.Clean();
        _cache.Publish(rescanned);
        var r = Validate($$"""[{"actionId":"review_in_section","itemIds":["{{H("Chrome cache")}}"]}]""");
        Assert.Empty(r.Suggestions);
        Assert.Contains(r.Rejected, x => x.StartsWith("item no longer"));
    }

    [Fact]
    public void A_suggestion_shown_earlier_knows_when_its_section_was_rescanned()
    {
        var s = Assert.Single(Validate($$"""[{"actionId":"review_in_section","itemIds":["{{H("Edge cache")}}"]}]""").Suggestions);
        Assert.True(s.IsCurrent(_cache));
        _cache.Publish(Scans.Clean());
        Assert.False(s.IsCurrent(_cache));
    }

    [Theory]
    [InlineData("""{"actionId":"review_in_section"}""")]
    [InlineData("""[{"actionId":"review_in_section","itemIds":"i1"}]""")]
    [InlineData("""[{"actionId":"review_in_section","itemIds":[1,2,3]}]""")]
    [InlineData("""[{"actionId":7,"itemIds":["i1"]}]""")]
    [InlineData("""["review_in_section"]""")]
    [InlineData("""[null, [], {}]""")]
    public void Malformed_suggestions_are_dropped_without_throwing(string json)
    {
        var r = Validate(json);
        Assert.Empty(r.Suggestions);
        Assert.NotEmpty(r.Rejected);
    }

    [Fact]
    public void At_most_three_suggestions_and_25_items_each_are_taken()
    {
        var ids = string.Join(",", Enumerable.Repeat($"\"{H("Edge cache")}\"", 40));
        var one = $$"""{"actionId":"review_in_section","itemIds":[{{ids}}]}""";
        var r = Validate("[" + string.Join(",", Enumerable.Repeat(one, 5)) + "]");
        Assert.Single(r.Suggestions); // same section merges into one button
        Assert.Contains("too many suggestions", r.Rejected);
        Assert.Contains("too many items in one suggestion", r.Rejected);
    }

    [Fact]
    public void Replies_that_are_not_json_are_shown_as_cleaned_text_with_no_buttons()
    {
        var a = _engine.Interpret("Sure! Just run rd /s C:\\Windows\\Temp2 and visit https://evil.example/fix", _data);
        Assert.Empty(a.Suggestions);
        Assert.DoesNotContain("https://", a.Text);
        Assert.DoesNotContain(@"C:\Windows\Temp2", a.Text);
        Assert.Contains(AnswerSanitizer.UnknownPath, a.Text);
    }

    [Fact]
    public void An_empty_answer_says_so()
    {
        Assert.Equal(AssistantEngine.EmptyAnswer, _engine.Interpret("""{"answer":"  ","suggestions":[]}""", _data).Text);
        Assert.Equal(AssistantEngine.EmptyAnswer, _engine.Interpret("[1,2]", _data).Text);
    }

    // ---------------- answer text ----------------

    [Fact]
    public void Known_paths_stay_and_unknown_paths_go()
    {
        var known = _data.KnownPaths;
        var text = AnswerSanitizer.Clean(
            @"Your biggest item is C:\Users\tester\Downloads\setup-old.exe. Also look at C:\Users\tester\AppData\Local\Google\Chrome\User Data\Default\Cache, " +
            @"but never touch C:\Windows\System32\drivers or \\server\share\secrets.", known);
        Assert.Contains(@"C:\Users\tester\Downloads\setup-old.exe.", text);
        Assert.Contains(@"C:\Users\tester\AppData\Local\Google\Chrome\User Data\Default\Cache,", text); // spaces in the name are fine
        Assert.DoesNotContain("System32", text);
        Assert.DoesNotContain("secrets", text);
        Assert.Equal(2, Occurrences(text, AnswerSanitizer.UnknownPath));
    }

    [Fact]
    public void A_known_folder_does_not_vouch_for_unknown_files_inside_it()
    {
        Assert.Contains(@"C:\Users\tester\Downloads", _data.KnownPaths); // parent of a scanned item
        var text = AnswerSanitizer.Clean(@"Open C:\Users\tester\Downloads and also C:\Users\tester\Downloads\passwords.txt now.", _data.KnownPaths);
        Assert.Contains(@"Open C:\Users\tester\Downloads and", text);
        Assert.DoesNotContain("passwords.txt", text);
    }

    [Fact]
    public void Links_control_characters_and_bidi_tricks_are_removed_and_length_is_capped()
    {
        var text = AnswerSanitizer.Clean("See www.example.com or http://x.test/a or file:///C:/x\u0007 done\u202E evil", _data.KnownPaths);
        Assert.DoesNotContain("example.com", text);
        Assert.DoesNotContain("x.test", text);
        Assert.DoesNotContain("file:", text);
        Assert.DoesNotContain('\u0007', text);
        Assert.DoesNotContain('\u202E', text);
        Assert.Equal(3, Occurrences(text, AnswerSanitizer.RemovedLink));

        var longText = AnswerSanitizer.Clean(string.Join(" ", Enumerable.Repeat("word", 1000)), _data.KnownPaths);
        Assert.True(longText.Length <= AnswerSanitizer.MaxLength + 1);
        Assert.EndsWith("…", longText);
    }

    private static int Occurrences(string s, string part)
    {
        int n = 0, at = 0;
        while ((at = s.IndexOf(part, at, StringComparison.Ordinal)) >= 0) { n++; at += part.Length; }
        return n;
    }
}
