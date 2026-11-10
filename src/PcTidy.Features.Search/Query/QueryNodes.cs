namespace PcTidy.Features.Search.Query;

/// <summary>A parsed search (Everything-style subset). Built by <see cref="QueryParser"/>; matched by <see cref="QueryEngine"/>.</summary>
public abstract record QueryNode;

/// <summary>Every part must match (words separated by spaces).</summary>
public sealed record AndNode(IReadOnlyList<QueryNode> Items) : QueryNode;

/// <summary>Any part may match (<c>a|b</c>).</summary>
public sealed record OrNode(IReadOnlyList<QueryNode> Items) : QueryNode;

/// <summary>Must not match (<c>!a</c>).</summary>
public sealed record NotNode(QueryNode Item) : QueryNode;

/// <summary>
/// A word. Without wildcards it matches anywhere in the name; with <c>*</c>/<c>?</c> it must match the whole name.
/// A word with a backslash is matched against the full path instead (like Everything).
/// </summary>
public sealed record TextNode(string Text, bool Wildcard, bool MatchPath) : QueryNode;

/// <summary><c>ext:jpg;png</c>: files with one of these extensions (lower-case, no dot). Empty list = no extension.</summary>
public sealed record ExtNode(IReadOnlyList<string> Extensions) : QueryNode;

/// <summary><c>size:&gt;1gb</c>: files whose size is in [Min, Max] bytes. Folders never match.</summary>
public sealed record SizeNode(long Min, long Max) : QueryNode;

/// <summary><c>dm:lastweek</c>: modified in [FromUtc, ToUtc).</summary>
public sealed record DateNode(DateTime FromUtc, DateTime ToUtc) : QueryNode;

/// <summary><c>path:photos\2024</c>: text anywhere in the full path (or a wildcard over the whole path).</summary>
public sealed record PathNode(string Text, bool Wildcard) : QueryNode;

/// <summary><c>folder:</c> / <c>file:</c>, optionally with a word that must match too.</summary>
public sealed record KindNode(bool Folders, QueryNode? Inner) : QueryNode;

/// <summary>Matches everything (e.g. a lone <c>dupe:</c>).</summary>
public sealed record MatchAllNode : QueryNode
{
    public static readonly MatchAllNode Instance = new();
}

/// <summary>The result of parsing: the tree, the whole-query modifiers, and friendly warnings.</summary>
public sealed record ParsedQuery(string Text, QueryNode? Root, bool Dupe, IReadOnlyList<string> Warnings)
{
    /// <summary>Nothing to search for (blank box).</summary>
    public bool IsEmpty => Root is null;
}
