using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PcTidy.Core;
using PcTidy.Features.AiChats.Library;
using PcTidy.Features.AiChats.Local;
using PcTidy.Features.AiChats.Model;

namespace PcTidy.Features.AiChats.UI;

public enum FilterKind { Header, All, Source, View, Group, Tag, Folder }

/// <summary>One entry of the left filter list ("ChatGPT 214", "Pinned 3", a topic, a tag, a folder).</summary>
public sealed class FilterRow
{
    public required FilterKind Kind { get; init; }
    public required string Label { get; init; }
    public int? Count { get; init; }
    public ChatSource? Source { get; init; }
    public LibraryView? View { get; init; }
    public string? Value { get; init; }

    public bool IsHeader => Kind == FilterKind.Header;
    public string CountText => Count is { } c ? c.ToString("N0", CultureInfo.CurrentCulture) : "";
    public string AutomationName => Count is { } c ? $"{Label}, {c} chats" : Label;

    public static FilterRow Header(string label) => new() { Kind = FilterKind.Header, Label = label };
}

/// <summary>One chat in the list.</summary>
public sealed class ChatRow
{
    public ChatRow(ChatSummary summary) => Summary = summary;

    public ChatSummary Summary { get; }
    public string Title => Summary.Title;
    public bool Pinned => Summary.Pinned;
    public bool HasSecrets => Summary.SecretCount > 0;

    public string Meta
    {
        get
        {
            var parts = new List<string> { ChatSources.Display(Summary.Source) };
            if (Summary.Date is { } d) parts.Add(d.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture));
            parts.Add(Summary.Group);
            parts.Add($"{Summary.MessageCount:N0} message{(Summary.MessageCount == 1 ? "" : "s")}");
            parts.Add(Sizes.Format(Summary.SizeBytes));
            return string.Join(" · ", parts);
        }
    }

    public string AutomationName => $"{Title}, {Meta}{(Pinned ? ", pinned" : "")}{(HasSecrets ? ", contains masked keys" : "")}";
}

/// <summary>One message in the preview (already masked).</summary>
public sealed class MessageRow
{
    public required string Header { get; init; }
    public required string Text { get; init; }
    public bool IsUser { get; init; }
    public bool IsAlternate { get; init; }
}

/// <summary>Agent chats of one project in one month (the "Agent chats on this PC" list).</summary>
public sealed partial class AgentRow : ObservableObject
{
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private string _stateText = "";

    public required ChatSource Source { get; init; }
    public required string Project { get; init; }
    /// <summary>Where the project lives without the user name ("source\repos"), or null (Codex chats with no folder).</summary>
    public string? ProjectHint { get; init; }
    public required string Month { get; init; }
    public required IReadOnlyList<AgentSession> Sessions { get; init; }
    public required int LiveCount { get; init; }

    public string SourceText => ChatSources.Display(Source);
    public string HintText => ProjectHint is null ? "" : " · in " + ProjectHint;
    public long Bytes => Sessions.Sum(s => s.TotalBytes);
    public string SizeText => Sizes.Format(Bytes);
    public string CountText => $"{Sessions.Count:N0} chat{(Sessions.Count == 1 ? "" : "s")}";
    public bool CanSelect => LiveCount < Sessions.Count;
    public string AutomationName => $"{SourceText}, {Project}, {Month}: {CountText}, {SizeText}. {StateText}";
}

/// <summary>One live-looking key in the Exposed secrets report.</summary>
public sealed class SecretRow
{
    public SecretRow(SecretReportRow row) => Row = row;

    public SecretReportRow Row { get; }
    public string Title => Row.RuleTitle;
    public string Masked => Row.Masked;
    public string Where => $"In \"{Row.ChatTitle}\" ({ChatSources.Display(Row.Source)}{(Row.Date is { } d ? ", " + d.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) : "")})"
        + (Row.ChatsWithSameKey > 1 ? $" · the same key is in {Row.ChatsWithSameKey} chats" : "");
    public string Advice => Row.Advice;
    public string AutomationName => $"{Title} {Masked}. {Where}. {Advice}";
}
