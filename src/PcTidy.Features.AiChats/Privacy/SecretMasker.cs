using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PcTidy.Catalog.AiChats;

namespace PcTidy.Features.AiChats.Privacy;

/// <summary>A key or password found in a chat. Never holds the secret itself: only its masked form and a short fingerprint.</summary>
public sealed record SecretFinding(string RuleId, string RuleTitle, string Masked, string Fingerprint, bool LooksLive, string Advice);

public sealed record MaskResult(string Text, IReadOnlyList<SecretFinding> Findings);

/// <summary>
/// Replaces keys, tokens and passwords with a short masked form ("sk-ant-…9f2a") using the rules in
/// <see cref="SecretRules"/> (adapted from gitleaks, MIT). Applied at import, so the library, the search index, previews
/// and (by default) exports only ever contain masked text. Masking is idempotent: masked text is left as it is.
/// </summary>
public sealed class SecretMasker
{
    public const string Ellipsis = "…";
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Words that make a value a placeholder or an example rather than a real secret.</summary>
    private static readonly string[] PlaceholderWords =
    {
        "example", "xxxx", "your", "placeholder", "changeme", "change_me", "dummy", "sample", "redacted", "<", ">", "${", "{{", "****", "…", "...", "test-key", "fake",
    };

    private readonly (SecretRule Rule, Regex Regex)[] _rules;
    private readonly TimeProvider _clock;

    public static SecretMasker Default { get; } = new();

    public SecretMasker(IEnumerable<SecretRule>? rules = null, TimeProvider? clock = null) : this(rules, clock, MatchTimeout) { }

    /// <summary>What replaces a whole text that couldn't be checked in time (fail closed: never stored unchecked).</summary>
    public const string UncheckedText = "[Long text hidden: PC Tidy couldn't check it for keys]";
    public const string UncheckedRuleId = "unchecked-text";

    /// <summary>
    /// A rule that times out on a long text is run again over pieces this long, overlapping by more than the longest
    /// secret any regex rule can match (AIC-3), so no key is cut in two. Private keys, the only long secrets, never go
    /// through the pieces: they are found by a linear scan first (<see cref="PemBlocks"/>).
    /// </summary>
    internal const int ChunkLength = 64 * 1024, ChunkOverlap = 20 * 1024;

    /// <summary>The longest secret a regex rule can match (the login-token rule's capped parts); stays below <see cref="ChunkOverlap"/>.</summary>
    internal const int LongestRegexSecret = 3 + 4096 + 1 + 3 + 12288 + 1 + 1024 + 2;

    internal const string PemRuleId = "pem-private-key";

    private readonly SecretRule? _pemRule;

    /// <summary>Tests: a shorter regex time limit, so a pathological input times out quickly.</summary>
    internal SecretMasker(IEnumerable<SecretRule>? rules, TimeProvider? clock, TimeSpan matchTimeout)
    {
        _clock = clock ?? TimeProvider.System;
        var list = (rules ?? SecretRules.All).ToList();
        // Private keys are found by a linear scan, not their regex: a lazy "[\s\S]{32,}?" over a long text can time out,
        // and a key can be longer than any overlap between pieces (AIC-3).
        _pemRule = list.FirstOrDefault(r => r.Id == PemRuleId);
        _rules = list.Where(r => r.Id != PemRuleId)
            .Select(r => (r, new Regex(r.Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeout)))
            .ToArray();
    }

    /// <summary>
    /// Masks every key in <paramref name="text"/>. Fails closed: if a rule takes too long on the text, it is run again
    /// over overlapping pieces; if a piece still takes too long, the WHOLE text is replaced by
    /// <see cref="UncheckedText"/> and a finding records it, so a key can never reach the library unmasked.
    /// </summary>
    public MaskResult Mask(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new MaskResult(text ?? "", Array.Empty<SecretFinding>());
        var spans = new List<(int Start, int Length, SecretRule Rule, string Secret)>();
        if (_pemRule is { } pem)
            foreach (var (index, length) in PemBlocks(text))
                spans.Add((index, length, pem, text.Substring(index, length)));
        foreach (var (rule, regex) in _rules)
        {
            var found = Matches(regex, text);
            if (found is null) return Unchecked(text);
            foreach (var g in found)
            {
                if (spans.Any(s => g.Index < s.Start + s.Length && s.Start < g.Index + g.Length)) continue; // an earlier, more specific rule (or the previous piece) has it
                if (rule.NeedsEntropyCheck && !LooksLikeRandomSecret(g.Value)) continue;
                spans.Add((g.Index, g.Length, rule, g.Value));
            }
        }
        if (spans.Count == 0) return new MaskResult(text, Array.Empty<SecretFinding>());

        var sb = new StringBuilder(text.Length);
        var findings = new List<SecretFinding>(spans.Count);
        int pos = 0;
        foreach (var s in spans.OrderBy(s => s.Start))
        {
            sb.Append(text, pos, s.Start - pos);
            var masked = MaskedForm(s.Rule, s.Secret);
            sb.Append(masked);
            pos = s.Start + s.Length;
            findings.Add(new SecretFinding(s.Rule.Id, s.Rule.Title, masked, Fingerprint(s.Secret), LooksLive(s.Rule, s.Secret), s.Rule.Advice));
        }
        sb.Append(text, pos, text.Length - pos);
        return new MaskResult(sb.ToString(), findings);
    }

    /// <summary>
    /// The "secret" groups a rule finds, with indexes into <paramref name="text"/>. On a timeout over the whole text,
    /// the rule runs over overlapping pieces instead (each match then has a short input). Null when even a piece times
    /// out: the caller must not keep the text.
    /// </summary>
    private static List<(int Index, int Length, string Value)>? Matches(Regex regex, string text)
    {
        var whole = Run(regex, text, 0);
        if (whole is not null || text.Length <= ChunkLength) return whole;
        var all = new List<(int Index, int Length, string Value)>();
        for (int start = 0; start < text.Length; start += ChunkLength - ChunkOverlap)
        {
            int length = Math.Min(ChunkLength, text.Length - start);
            var piece = Run(regex, text.Substring(start, length), start);
            if (piece is null) return null;
            all.AddRange(piece);
            if (start + length >= text.Length) break;
        }
        return all;
    }

    /// <summary>
    /// Every private key block in <paramref name="text"/>, found by one linear scan (no regex, so no timeout and no
    /// length limit): a <c>-----BEGIN … PRIVATE KEY-----</c> line up to its <c>-----END … PRIVATE KEY-----</c> line. A
    /// BEGIN line without its END (a key cut off, or the next BEGIN comes first) still hides the key lines after it
    /// (base64 and "Proc-Type: …" header lines), so part of a key is never stored either. A lone BEGIN line with nothing
    /// after it holds no key and is left as it is.
    /// </summary>
    internal static List<(int Index, int Length)> PemBlocks(string text)
    {
        const string begin = "-----BEGIN", end = "-----END";
        var blocks = new List<(int Index, int Length)>();
        int pos = 0;
        while (pos < text.Length && (pos = text.IndexOf(begin, pos, StringComparison.Ordinal)) >= 0)
        {
            int headerEnd = PrivateKeyLabelEnd(text, pos + begin.Length);
            if (headerEnd < 0) { pos += begin.Length; continue; }
            int nextBegin = text.IndexOf(begin, headerEnd, StringComparison.Ordinal);
            int limit = nextBegin < 0 ? text.Length : nextBegin;
            // The END is only looked for before the next BEGIN, so each part of the text is searched once (linear).
            int footer = text.IndexOf(end, headerEnd, limit - headerEnd, StringComparison.Ordinal);
            int footerEnd = footer >= 0 ? PrivateKeyLabelEnd(text, footer + end.Length) : -1;
            if (footerEnd > 0 && footer - headerEnd >= 32)
            {
                blocks.Add((pos, footerEnd - pos));
                pos = footerEnd;
                continue;
            }
            int bodyEnd = KeyBodyEnd(text, headerEnd, limit);
            if (bodyEnd > headerEnd) blocks.Add((pos, bodyEnd - pos));
            pos = Math.Max(bodyEnd, headerEnd);
        }
        return blocks;
    }

    /// <summary>
    /// After "-----BEGIN" or "-----END" at <paramref name="i"/>: the index just past " … PRIVATE KEY[ BLOCK]-----", or -1
    /// when the label isn't a private key's (certificates and public keys are not secrets).
    /// </summary>
    private static int PrivateKeyLabelEnd(string text, int i)
    {
        if (i >= text.Length) return -1;
        int close = text.IndexOf("-----", i, Math.Min(130, text.Length - i), StringComparison.Ordinal);
        if (close < 0) return -1;
        var label = text.AsSpan(i, close - i);
        foreach (var c in label)
            if (!(c is ' ' or '_' or '-' || char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c))) return -1;
        return label.EndsWith("PRIVATE KEY", StringComparison.Ordinal) || label.EndsWith("PRIVATE KEY BLOCK", StringComparison.Ordinal)
            ? close + 5 : -1;
    }

    /// <summary>
    /// Where the key lines after a BEGIN line end (before <paramref name="limit"/>): "Name: value" header lines first,
    /// then base64 lines; the first other line, or a blank line after base64, ends them.
    /// </summary>
    private static int KeyBodyEnd(string text, int from, int limit)
    {
        int bodyEnd = from, lineStart = from;
        bool sawBase64 = false;
        while (lineStart < limit)
        {
            int nl = text.IndexOf('\n', lineStart, limit - lineStart);
            int lineEnd = nl < 0 ? limit : nl;
            var line = text.AsSpan(lineStart, lineEnd - lineStart).Trim();
            if (line.Length == 0)
            {
                if (sawBase64) break;
            }
            else if (line.Length >= 4 && IsBase64Line(line)) { sawBase64 = true; bodyEnd = lineEnd; }
            else if (!sawBase64 && line.Length < 200 && line.IndexOf(':') > 0) bodyEnd = lineEnd;
            else break;
            lineStart = lineEnd + 1;
        }
        return sawBase64 ? bodyEnd : from;
    }

    private static bool IsBase64Line(ReadOnlySpan<char> line)
    {
        foreach (var c in line)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=')) return false;
        return true;
    }

    private static List<(int Index, int Length, string Value)>? Run(Regex regex, string input, int offset)
    {
        try
        {
            var list = new List<(int Index, int Length, string Value)>();
            foreach (Match m in regex.Matches(input))
            {
                var g = m.Groups["secret"];
                if (g.Success && g.Length > 0) list.Add((g.Index + offset, g.Length, g.Value));
            }
            return list;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    /// <summary>The whole text hidden, with a finding so the "Exposed secrets" report says a text wasn't checked.</summary>
    private static MaskResult Unchecked(string text)
        => new(UncheckedText, new[]
        {
            new SecretFinding(UncheckedRuleId, "Long text PC Tidy couldn't check", UncheckedText, Fingerprint(text), LooksLive: true,
                "This message was too long or too unusual to check for keys in time, so PC Tidy hid all of it. The original chat still has it: if it held a key, change that key."),
        });

    /// <summary>"sk-ant-api03-…9f2a" style: the provider's fixed prefix, an ellipsis, the last 4 characters of long keys.</summary>
    internal static string MaskedForm(SecretRule rule, string secret)
    {
        if (rule.Id == "pem-private-key") return "[private key hidden by PC Tidy]";
        var prefix = rule.KeepPrefix > 0 && rule.KeepPrefix < secret.Length - 8 ? secret[..rule.KeepPrefix] : "";
        var tail = rule.KeepTail && secret.Length >= 20 ? secret[^4..] : "";
        return prefix + Ellipsis + tail;
    }

    /// <summary>12 hex characters of SHA-256: tells "the same key in three chats" apart without keeping the key.</summary>
    internal static string Fingerprint(string secret)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)))[..12].ToLowerInvariant();

    /// <summary>Not a placeholder or documentation example, random-looking, and (for login tokens) not already expired.</summary>
    internal bool LooksLive(SecretRule rule, string secret)
    {
        if (IsPlaceholder(secret)) return false;
        if (rule.Id == "pem-private-key") return !secret.Contains("EXAMPLE", StringComparison.OrdinalIgnoreCase);
        if (rule.Id == "jwt" && JwtExpiry(secret) is { } exp && exp < _clock.GetUtcNow().UtcDateTime) return false;
        var body = rule.KeepPrefix > 0 && rule.KeepPrefix < secret.Length ? secret[rule.KeepPrefix..] : secret;
        return Entropy(body) >= 3.0 && !HasLongRun(body);
    }

    private static bool IsPlaceholder(string secret)
        => PlaceholderWords.Any(w => secret.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>For the generic "password = …" rule: long enough, mixed characters, random-looking, not a placeholder.</summary>
    internal static bool LooksLikeRandomSecret(string value)
    {
        if (value.Length < 8 || IsPlaceholder(value)) return false;
        if (value.Contains(Ellipsis, StringComparison.Ordinal)) return false; // already masked
        if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase) || value.Contains('\\') || value.Count(c => c == '/') > 1) return false; // a URL or path
        if (value.All(c => char.IsLetter(c) || c is '_' or '-' or '.')) return false; // a word or identifier ("password=getPassword")
        if (value.All(c => char.IsDigit(c) || c is '.' or '-' or ':')) return false; // a number, date or version
        if (value.EndsWith("()", StringComparison.Ordinal) || value.Contains('(')) return false; // code: password = getPass(x)
        bool letters = value.Any(char.IsLetter), digitsOrSymbols = value.Any(c => !char.IsLetter(c));
        return letters && digitsOrSymbols && Entropy(value) >= 3.0;
    }

    /// <summary>Shannon entropy in bits per character.</summary>
    internal static double Entropy(string s)
    {
        if (s.Length == 0) return 0;
        var counts = new Dictionary<char, int>();
        foreach (var c in s) counts[c] = counts.GetValueOrDefault(c) + 1;
        double h = 0;
        foreach (var n in counts.Values)
        {
            var p = (double)n / s.Length;
            h -= p * Math.Log2(p);
        }
        return h;
    }

    private static bool HasLongRun(string s)
    {
        int run = 1;
        for (int i = 1; i < s.Length; i++)
        {
            run = s[i] == s[i - 1] ? run + 1 : 1;
            if (run >= 6) return true;
        }
        return false;
    }

    private static DateTime? JwtExpiry(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            var b64 = parts[1].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(b64));
            return doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var secs) && secs > 0 && secs < 253_402_300_799
                ? DateTime.UnixEpoch.AddSeconds(secs) : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException) { return null; }
    }
}
