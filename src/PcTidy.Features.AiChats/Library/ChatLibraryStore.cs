using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using PcTidy.Features.AiChats.Model;

namespace PcTidy.Features.AiChats.Library;

/// <summary>
/// PC Tidy's own chat library: one SQLite database in &lt;DataRoot&gt;\aichats\library.db with a full-text index (FTS5).
/// This is the ONLY code in the AI chats section that writes anything itself, and it writes only there, only PC Tidy's
/// derived data: chats arrive as <see cref="MaskedChat"/> (secrets already masked), so neither the tables nor the search
/// index ever contain an unmasked key. Re-importing a chat replaces its text but keeps the user's tags, pins, folder
/// and topic. All calls are serialized; every connection is closed after use (no file stays locked).
/// </summary>
public sealed class ChatLibraryStore
{
    private const int SchemaVersion = 1;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _connectionString;
    private bool _initialized;

    public ChatLibraryStore(string dataRoot)
    {
        Folder = AiChatsIds.DataFolder(dataRoot);
        DatabasePath = Path.Combine(Folder, "library.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
    }

    public string Folder { get; }
    public string DatabasePath { get; }

    // ------------------------------------------------------------------ writing (own data only)

    /// <summary>Adds or replaces chats. Returns how many were new.</summary>
    public async Task<int> UpsertAsync(IReadOnlyList<MaskedChat> chats, CancellationToken ct)
    {
        if (chats.Count == 0) return 0;
        return await WithConnection(async c =>
        {
            int added = 0;
            using var tx = c.BeginTransaction();
            foreach (var chat in chats)
            {
                ct.ThrowIfCancellationRequested();
                var exists = Convert.ToInt64(await Scalar(c, tx, "SELECT COUNT(*) FROM chats WHERE id=$id", ("$id", chat.Id)).ConfigureAwait(false)) > 0;
                if (!exists) added++;
                // boundary-ok: own data in DataRoot (insert or replace one chat row)
                await Exec(c, tx, """
                    INSERT INTO chats(id, source, source_id, title, created_utc, updated_utc, model, project, message_count, char_count,
                        size_bytes, content_hash, origin, origin_kind, alt_count, attachment_count, secret_count, terms, imported_utc)
                    VALUES($id,$source,$sid,$title,$created,$updated,$model,$project,$mc,$cc,$size,$hash,$origin,$ok,$alts,$atts,$secrets,$terms,$now)
                    ON CONFLICT(id) DO UPDATE SET source=excluded.source, source_id=excluded.source_id, title=excluded.title,
                        created_utc=excluded.created_utc, updated_utc=excluded.updated_utc, model=excluded.model, project=excluded.project,
                        message_count=excluded.message_count, char_count=excluded.char_count, size_bytes=excluded.size_bytes,
                        content_hash=excluded.content_hash, origin=excluded.origin, origin_kind=excluded.origin_kind, alt_count=excluded.alt_count,
                        attachment_count=excluded.attachment_count, secret_count=excluded.secret_count, terms=excluded.terms, imported_utc=excluded.imported_utc
                    """,
                    ("$id", chat.Id), ("$source", ChatSources.Key(chat.Source)), ("$sid", chat.SourceId), ("$title", chat.Title),
                    ("$created", Ticks(chat.CreatedUtc)), ("$updated", Ticks(chat.UpdatedUtc)), ("$model", chat.Model), ("$project", chat.Project),
                    ("$mc", chat.Messages.Count), ("$cc", chat.CharCount), ("$size", chat.SizeBytes), ("$hash", chat.ContentHash),
                    ("$origin", chat.Origin), ("$ok", chat.OriginKind.ToString()), ("$alts", chat.Alternates.Count), ("$atts", chat.Attachments.Count),
                    ("$secrets", chat.Secrets.Count), ("$terms", TopicLabeler.EncodeTerms(chat.Terms)), ("$now", DateTime.UtcNow.Ticks)).ConfigureAwait(false);

                foreach (var table in new[] { "messages", "branches", "attachments", "secrets", "chats_fts" })
                    await Exec(c, tx, $"DELETE FROM {table} WHERE {(table == "chats_fts" ? "id" : "chat_id")}=$id", ("$id", chat.Id)).ConfigureAwait(false); // boundary-ok: own data in DataRoot

                await InsertMessages(c, tx, chat.Id, 0, chat.Messages).ConfigureAwait(false);
                for (int b = 0; b < chat.Alternates.Count; b++)
                {
                    await Exec(c, tx, "INSERT INTO branches(chat_id, branch, fork_at) VALUES($id,$b,$f)", // boundary-ok: own data in DataRoot
                        ("$id", chat.Id), ("$b", b + 1), ("$f", chat.Alternates[b].ForkAt)).ConfigureAwait(false);
                    await InsertMessages(c, tx, chat.Id, b + 1, chat.Alternates[b].Messages).ConfigureAwait(false);
                }
                for (int i = 0; i < chat.Attachments.Count; i++)
                {
                    var a = chat.Attachments[i];
                    await Exec(c, tx, "INSERT INTO attachments(chat_id, seq, name, size, archive_entry, file_path, text) VALUES($id,$i,$n,$s,$e,$f,$t)", // boundary-ok: own data in DataRoot
                        ("$id", chat.Id), ("$i", i), ("$n", a.Name), ("$s", a.Size), ("$e", a.ArchiveEntry), ("$f", a.FilePath), ("$t", a.ExtractedText)).ConfigureAwait(false);
                }
                foreach (var s in chat.Secrets)
                    await Exec(c, tx, "INSERT INTO secrets(chat_id, rule_id, rule_title, masked, fingerprint, live, advice) VALUES($id,$r,$rt,$m,$fp,$l,$a)", // boundary-ok: own data in DataRoot
                        ("$id", chat.Id), ("$r", s.RuleId), ("$rt", s.RuleTitle), ("$m", s.Masked), ("$fp", s.Fingerprint), ("$l", s.LooksLive ? 1 : 0), ("$a", s.Advice)).ConfigureAwait(false);
                await Exec(c, tx, "INSERT INTO chats_fts(id, title, body) VALUES($id,$t,$b)", // boundary-ok: own data in DataRoot
                    ("$id", chat.Id), ("$t", chat.Title), ("$b", SearchBody(chat))).ConfigureAwait(false);
            }
            tx.Commit();
            return added;
        }, ct).ConfigureAwait(false);
    }

    private static async Task InsertMessages(SqliteConnection c, SqliteTransaction tx, string id, int branch, IReadOnlyList<ChatMessage> messages)
    {
        for (int i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            await Exec(c, tx, "INSERT INTO messages(chat_id, branch, seq, role, created_utc, model, text) VALUES($id,$b,$s,$r,$t,$m,$x)", // boundary-ok: own data in DataRoot
                ("$id", id), ("$b", branch), ("$s", i), ("$r", m.Role.ToString()), ("$t", Ticks(m.CreatedUtc)), ("$m", m.Model), ("$x", m.Text)).ConfigureAwait(false);
        }
    }

    private static string SearchBody(MaskedChat chat)
    {
        var sb = new StringBuilder();
        foreach (var m in chat.Messages) sb.Append(m.Text).Append('\n');
        foreach (var b in chat.Alternates) foreach (var m in b.Messages) sb.Append(m.Text).Append('\n');
        foreach (var a in chat.Attachments) sb.Append(a.Name).Append('\n');
        if (chat.Project is not null) sb.Append(chat.Project);
        return sb.ToString();
    }

    /// <summary>Recomputes the offline topic of every web chat (after an import).</summary>
    public async Task RelabelTopicsAsync(CancellationToken ct)
    {
        await WithConnection(async c =>
        {
            var docs = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            await using (var cmd = Command(c, null, "SELECT id, terms FROM chats WHERE source IN ('chatgpt','claude')"))
            await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                    docs[r.GetString(0)] = TopicLabeler.DecodeTerms(r.IsDBNull(1) ? null : r.GetString(1));
            var labels = TopicLabeler.Label(docs);
            using var tx = c.BeginTransaction();
            foreach (var (id, (topic, keywords)) in labels)
                await Exec(c, tx, "UPDATE chats SET topic=$t, keywords=$k WHERE id=$id", // boundary-ok: own data in DataRoot
                    ("$t", topic), ("$k", string.Join(' ', keywords)), ("$id", id)).ConfigureAwait(false);
            tx.Commit();
            return 0;
        }, ct).ConfigureAwait(false);
    }

    public Task SetPinnedAsync(string id, bool pinned, CancellationToken ct)
        => Write("UPDATE chats SET pinned=$p WHERE id=$id", ct, ("$p", pinned ? 1 : 0), ("$id", id));

    /// <summary>A folder of the user's own (null or empty = none).</summary>
    public Task SetFolderAsync(string id, string? folder, CancellationToken ct)
        => Write("UPDATE chats SET folder=$f WHERE id=$id", ct, ("$f", CleanLabel(folder)), ("$id", id));

    /// <summary>The user's own topic for a web chat (null = the computed one again).</summary>
    public Task SetTopicAsync(string id, string? topic, CancellationToken ct)
        => Write("UPDATE chats SET topic_user=$t WHERE id=$id", ct, ("$t", CleanLabel(topic)), ("$id", id));

    public Task AddTagAsync(string id, string tag, CancellationToken ct)
        => CleanTag(tag) is { } t ? Write("INSERT OR IGNORE INTO tags(chat_id, tag) VALUES($id,$t)", ct, ("$id", id), ("$t", t)) : Task.CompletedTask;

    public Task RemoveTagAsync(string id, string tag, CancellationToken ct)
        => Write("DELETE FROM tags WHERE chat_id=$id AND tag=$t", ct, ("$id", id), ("$t", tag));

    /// <summary>Removes chats from PC Tidy's library only (the export or transcript they came from is not touched).</summary>
    public async Task RemoveAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        await WithConnection(async c =>
        {
            using var tx = c.BeginTransaction();
            foreach (var id in ids)
                foreach (var table in new[] { "messages", "branches", "attachments", "secrets", "tags", "chats_fts", "chats" })
                    await Exec(c, tx, $"DELETE FROM {table} WHERE {(table is "chats_fts" or "chats" ? "id" : "chat_id")}=$id", ("$id", id)).ConfigureAwait(false); // boundary-ok: own data in DataRoot
            tx.Commit();
            return 0;
        }, ct).ConfigureAwait(false);
    }

    private Task Write(string sql, CancellationToken ct, params (string, object?)[] args)
        => WithConnection(async c => { await Exec(c, null, sql, args).ConfigureAwait(false); return 0; }, ct); // boundary-ok: own data in DataRoot

    // ------------------------------------------------------------------ reading

    private const string SummaryColumns = """
        c.id, c.source, c.title, c.created_utc, c.updated_utc, c.model, c.project, COALESCE(c.topic_user, c.topic), c.folder, c.pinned,
        c.message_count, c.char_count, c.size_bytes, c.secret_count, c.alt_count, c.attachment_count, c.origin_kind, c.origin, c.content_hash,
        (SELECT group_concat(tag, char(31)) FROM tags t WHERE t.chat_id = c.id), c.keywords
        """;

    /// <summary>The FROM, WHERE and ORDER BY of a query (everything but the group, which is a computed display name).</summary>
    private static (string From, string Where, string Order, (string, object?)[] Args) Filter(LibraryQuery q)
    {
        var where = new List<string>();
        var args = new List<(string, object?)>();
        var from = "chats c";
        var order = "COALESCE(c.created_utc, c.updated_utc, 0) DESC";
        if (FtsQuery(q.Text) is { } match)
        {
            from = "chats_fts f JOIN chats c ON c.id = f.id";
            where.Add("chats_fts MATCH $match");
            args.Add(("$match", match));
            order = "bm25(chats_fts, 0.0, 3.0, 1.0)";
        }
        if (q.Source is { } s) { where.Add("c.source = $src"); args.Add(("$src", ChatSources.Key(s))); }
        if (!string.IsNullOrEmpty(q.Tag)) { where.Add("EXISTS (SELECT 1 FROM tags t WHERE t.chat_id = c.id AND t.tag = $tag)"); args.Add(("$tag", q.Tag)); }
        if (!string.IsNullOrEmpty(q.Folder)) { where.Add("c.folder = $folder"); args.Add(("$folder", q.Folder)); }
        switch (q.View)
        {
            case LibraryView.Pinned: where.Add("c.pinned = 1"); break;
            case LibraryView.Biggest: order = "c.size_bytes DESC"; break;
            case LibraryView.NearEmpty:
                where.Add($"c.message_count <= {LibraryRules.NearEmptyMaxMessages} AND c.char_count < {LibraryRules.NearEmptyMaxChars}");
                break;
            case LibraryView.Duplicates:
                where.Add("c.message_count > 0 AND c.content_hash IN (SELECT content_hash FROM chats GROUP BY content_hash HAVING COUNT(*) > 1)");
                order = "c.content_hash, c.created_utc";
                break;
            case LibraryView.WithSecrets: where.Add("c.secret_count > 0"); break;
        }
        return (from, where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "", order, args.ToArray());
    }

    public async Task<IReadOnlyList<ChatSummary>> QueryAsync(LibraryQuery q, CancellationToken ct)
    {
        var (from, where, order, args) = Filter(q);
        var limit = q.View == LibraryView.Biggest ? Math.Min(q.Limit, LibraryRules.BiggestCount) : q.Limit;
        if (string.IsNullOrEmpty(q.Group))
            return await Read($"SELECT {SummaryColumns} FROM {from} {where} ORDER BY {order} LIMIT {limit}", ReadSummary, ct, args).ConfigureAwait(false);

        // Groups are display names (project short names, topics), so they are matched on light rows over the whole
        // library first (no limit), then only the matching chats are read in full, in the same order.
        var ids = (await Read($"SELECT c.id, c.source, c.project, COALESCE(c.topic_user, c.topic) FROM {from} {where} ORDER BY {order}",
                r => (Id: r.GetString(0), Group: ChatSummary.GroupOf(ChatSources.FromKey(r.GetString(1)), Str(r, 2), Str(r, 3))), ct, args).ConfigureAwait(false))
            .Where(x => string.Equals(x.Group, q.Group, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Id).Take(limit).ToList();
        if (ids.Count == 0) return Array.Empty<ChatSummary>();
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < ids.Count; i++) rank[ids[i]] = i;
        var rows = await Read($"SELECT {SummaryColumns} FROM chats c WHERE c.id IN (SELECT value FROM json_each($ids))", ReadSummary, ct,
            ("$ids", System.Text.Json.JsonSerializer.Serialize(ids))).ConfigureAwait(false);
        return rows.OrderBy(r => rank[r.Id]).ToList();
    }

    public async Task<ChatSummary?> GetSummaryAsync(string id, CancellationToken ct)
        => (await Read($"SELECT {SummaryColumns} FROM chats c WHERE c.id = $id", ReadSummary, ct, ("$id", id)).ConfigureAwait(false)).FirstOrDefault();

    public async Task<ChatDetail?> GetAsync(string id, CancellationToken ct)
    {
        var summary = await GetSummaryAsync(id, ct).ConfigureAwait(false);
        if (summary is null) return null;
        var rows = await Read("SELECT branch, role, created_utc, model, text FROM messages WHERE chat_id=$id ORDER BY branch, seq",
            r => (Branch: r.GetInt32(0), Message: new ChatMessage(Enum.Parse<ChatRole>(r.GetString(1)), r.GetString(4), FromTicks(r, 2), r.IsDBNull(3) ? null : r.GetString(3))),
            ct, ("$id", id)).ConfigureAwait(false);
        var forks = (await Read("SELECT branch, fork_at FROM branches WHERE chat_id=$id", r => (r.GetInt32(0), r.GetInt32(1)), ct, ("$id", id)).ConfigureAwait(false))
            .ToDictionary(x => x.Item1, x => x.Item2);
        var attachments = await Read("SELECT name, size, archive_entry, file_path, text FROM attachments WHERE chat_id=$id ORDER BY seq",
            r => new ChatAttachment(r.GetString(0), r.GetInt64(1), Str(r, 2), Str(r, 3), Str(r, 4)), ct, ("$id", id)).ConfigureAwait(false);
        var terms = await Read("SELECT terms FROM chats WHERE id=$id", r => TopicLabeler.DecodeTerms(Str(r, 0)), ct, ("$id", id)).ConfigureAwait(false);
        var alternates = rows.Where(x => x.Branch > 0).GroupBy(x => x.Branch).OrderBy(g => g.Key)
            .Select(g => new ChatBranch(forks.GetValueOrDefault(g.Key), g.Select(x => x.Message).ToList())).ToList();
        return new ChatDetail(summary, rows.Where(x => x.Branch == 0).Select(x => x.Message).ToList(), alternates, attachments, terms.FirstOrDefault() ?? new());
    }

    /// <summary>Word counts of many chats at once (for related-chat links).</summary>
    public async Task<IReadOnlyDictionary<string, Dictionary<string, int>>> TermsAsync(CancellationToken ct)
        => (await Read("SELECT id, terms FROM chats", r => (r.GetString(0), TopicLabeler.DecodeTerms(Str(r, 1))), ct).ConfigureAwait(false))
            .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);

    public Task<IReadOnlyList<SourceCount>> CountsAsync(CancellationToken ct)
        => Read("SELECT source, COUNT(*), COALESCE(SUM(size_bytes),0) FROM chats GROUP BY source",
            r => new SourceCount(ChatSources.FromKey(r.GetString(0)), r.GetInt32(1), r.GetInt64(2)), ct);

    /// <summary>How many chats a view holds, counted in SQL over the whole library (no row limit, nothing read).</summary>
    public async Task<int> CountAsync(LibraryView view, CancellationToken ct)
    {
        if (view == LibraryView.Biggest) return 0;
        var (from, where, _, args) = Filter(new LibraryQuery { View = view });
        var n = await Read($"SELECT COUNT(*) FROM {from} {where}", r => r.GetInt64(0), ct, args).ConfigureAwait(false);
        return (int)Math.Min(int.MaxValue, n.FirstOrDefault());
    }

    public Task<IReadOnlyList<GroupCount>> TagsAsync(CancellationToken ct)
        => Read("SELECT t.tag, COUNT(*), COALESCE(SUM(c.size_bytes),0) FROM tags t JOIN chats c ON c.id = t.chat_id GROUP BY t.tag ORDER BY t.tag COLLATE NOCASE",
            r => new GroupCount(r.GetString(0), r.GetInt32(1), r.GetInt64(2)), ct);

    public Task<IReadOnlyList<GroupCount>> FoldersAsync(CancellationToken ct)
        => Read("SELECT folder, COUNT(*), COALESCE(SUM(size_bytes),0) FROM chats WHERE folder IS NOT NULL GROUP BY folder ORDER BY folder COLLATE NOCASE",
            r => new GroupCount(r.GetString(0), r.GetInt32(1), r.GetInt64(2)), ct);

    /// <summary>Projects (agent chats) and topics (web chats) with counts, biggest groups first.</summary>
    public async Task<IReadOnlyList<GroupCount>> GroupsAsync(CancellationToken ct)
    {
        // The group is a display name computed from the project or topic: grouped per distinct stored value in SQL,
        // then merged by display name. Covers the whole library, however big.
        var stored = await Read("SELECT source, project, COALESCE(topic_user, topic), COUNT(*), COALESCE(SUM(size_bytes),0) FROM chats GROUP BY 1, 2, 3",
            r => (Group: ChatSummary.GroupOf(ChatSources.FromKey(r.GetString(0)), Str(r, 1), Str(r, 2)), Chats: r.GetInt32(3), Bytes: r.GetInt64(4)), ct).ConfigureAwait(false);
        return stored.GroupBy(g => g.Group, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GroupCount(g.First().Group, g.Sum(x => x.Chats), g.Sum(x => x.Bytes)))
            .OrderByDescending(g => g.Chats).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The "Exposed secrets" report: live-looking keys only (unless <paramref name="liveOnly"/> is false), masked.</summary>
    public Task<IReadOnlyList<SecretReportRow>> SecretsAsync(bool liveOnly, CancellationToken ct)
        => Read($"""
            SELECT s.chat_id, c.title, c.source, COALESCE(c.created_utc, c.updated_utc), s.rule_title, s.masked, s.advice, s.fingerprint,
                   (SELECT COUNT(DISTINCT s2.chat_id) FROM secrets s2 WHERE s2.fingerprint = s.fingerprint)
            FROM secrets s JOIN chats c ON c.id = s.chat_id
            {(liveOnly ? "WHERE s.live = 1" : "")}
            GROUP BY s.chat_id, s.fingerprint
            ORDER BY COALESCE(c.created_utc, c.updated_utc, 0) DESC
            """,
            r => new SecretReportRow(r.GetString(0), r.GetString(1), ChatSources.FromKey(r.GetString(2)), FromTicks(r, 3), r.GetString(4), r.GetString(5),
                r.GetString(6), r.GetString(7), r.GetInt32(8)), ct);

    // ------------------------------------------------------------------ plumbing

    private static ChatSummary ReadSummary(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Source = ChatSources.FromKey(r.GetString(1)),
        Title = r.GetString(2),
        CreatedUtc = FromTicks(r, 3),
        UpdatedUtc = FromTicks(r, 4),
        Model = Str(r, 5),
        Project = Str(r, 6),
        Topic = Str(r, 7),
        Folder = Str(r, 8),
        Pinned = r.GetInt64(9) != 0,
        MessageCount = r.GetInt32(10),
        CharCount = r.GetInt32(11),
        SizeBytes = r.GetInt64(12),
        SecretCount = r.GetInt32(13),
        AlternateCount = r.GetInt32(14),
        AttachmentCount = r.GetInt32(15),
        OriginKind = Enum.TryParse<OriginKind>(Str(r, 16), out var ok) ? ok : OriginKind.ExportZip,
        Origin = Str(r, 17) ?? "",
        ContentHash = Str(r, 18) ?? "",
        Tags = Str(r, 19) is { } tags ? tags.Split('\u001f').OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList() : Array.Empty<string>(),
        Keywords = Str(r, 20) is { } k ? k.Split(' ', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>(),
    };

    /// <summary>User words → an FTS5 query: each word as a quoted prefix term, all required. Null when there's nothing to search.</summary>
    internal static string? FtsQuery(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var words = new List<string>();
        var sb = new StringBuilder();
        foreach (var ch in text.Append(' '))
        {
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); continue; }
            if (sb.Length > 0) { words.Add(sb.ToString()); sb.Clear(); }
        }
        return words.Count == 0 ? null : string.Join(' ', words.Take(12).Select(w => "\"" + w + "\"*"));
    }

    private static string? CleanLabel(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length <= 80 ? t : t[..80];
    }

    /// <summary>Tags as Obsidian accepts them: no spaces or #, letters/digits/-/_// only.</summary>
    internal static string? CleanTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var sb = new StringBuilder();
        foreach (var ch in tag.Trim().TrimStart('#'))
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '/' ? char.ToLowerInvariant(ch) : '-');
        var t = sb.ToString().Trim('-', '/');
        return t.Length == 0 || t.All(char.IsDigit) ? null : t.Length <= 40 ? t : t[..40];
    }

    private static long? Ticks(DateTime? d) => d?.ToUniversalTime().Ticks;

    private static DateTime? FromTicks(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : new DateTime(r.GetInt64(i), DateTimeKind.Utc);

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private async Task<IReadOnlyList<T>> Read<T>(string sql, Func<SqliteDataReader, T> map, CancellationToken ct, params (string, object?)[] args)
        => await WithConnection(async c =>
        {
            var list = new List<T>();
            await using var cmd = Command(c, null, sql, args);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(map(r));
            return (IReadOnlyList<T>)list;
        }, ct).ConfigureAwait(false);

    /// <summary>
    /// Runs one unit of work on its own connection, on the thread pool: Microsoft.Data.Sqlite's *Async methods run
    /// synchronously on the calling thread, so without Task.Run every query would run on the page's UI thread.
    /// </summary>
    private async Task<T> WithConnection<T>(Func<SqliteConnection, Task<T>> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                await using var c = new SqliteConnection(_connectionString);
                if (!_initialized) Directory.CreateDirectory(Folder); // boundary-ok: own data in DataRoot
                await c.OpenAsync(ct).ConfigureAwait(false);
                if (!_initialized)
                {
                    await CreateSchema(c).ConfigureAwait(false);
                    _initialized = true;
                }
                return await work(c).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    // ------------------------------------------------------------------ a damaged library

    /// <summary>True when SQLite says the file isn't a usable database (corrupt, or not a database at all).</summary>
    public static bool IsDamaged(Exception ex)
        => ex is SqliteException { SqliteErrorCode: SqliteCorrupt or SqliteNotADb };

    private const int SqliteCorrupt = 11, SqliteNotADb = 26;

    /// <summary>
    /// Sets a damaged library.db aside as library.damaged-&lt;time&gt;.db in the same folder (kept, never deleted) and
    /// starts an empty one on the next call. Re-importing the exports and agent chats brings the chats back.
    /// Returns the name it was set aside under, or null when there was no file.
    /// </summary>
    public async Task<string?> SetAsideDamagedAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                SqliteConnection.ClearAllPools();
                if (!File.Exists(DatabasePath)) { _initialized = false; return null; }
                var aside = Path.Combine(Folder, $"library.damaged-{DateTime.Now:yyyyMMdd-HHmmss}.db");
                File.Move(DatabasePath, aside); // boundary-ok: own data in DataRoot (the damaged file is kept, renamed)
                foreach (var extra in new[] { "-journal", "-wal", "-shm" })
                    if (File.Exists(DatabasePath + extra)) File.Move(DatabasePath + extra, aside + extra); // boundary-ok: own data in DataRoot
                _initialized = false;
                return Path.GetFileName(aside);
            }, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static async Task CreateSchema(SqliteConnection c)
    {
        await Exec(c, null, """
            PRAGMA journal_mode=DELETE;
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS chats(
                id TEXT PRIMARY KEY, source TEXT NOT NULL, source_id TEXT NOT NULL, title TEXT NOT NULL,
                created_utc INTEGER, updated_utc INTEGER, model TEXT, project TEXT, topic TEXT, topic_user TEXT, keywords TEXT, folder TEXT,
                pinned INTEGER NOT NULL DEFAULT 0, message_count INTEGER NOT NULL DEFAULT 0, char_count INTEGER NOT NULL DEFAULT 0,
                size_bytes INTEGER NOT NULL DEFAULT 0, content_hash TEXT, origin TEXT, origin_kind TEXT, alt_count INTEGER NOT NULL DEFAULT 0,
                attachment_count INTEGER NOT NULL DEFAULT 0, secret_count INTEGER NOT NULL DEFAULT 0, terms TEXT, imported_utc INTEGER);
            CREATE INDEX IF NOT EXISTS ix_chats_source ON chats(source);
            CREATE INDEX IF NOT EXISTS ix_chats_hash ON chats(content_hash);
            CREATE TABLE IF NOT EXISTS messages(chat_id TEXT NOT NULL, branch INTEGER NOT NULL, seq INTEGER NOT NULL, role TEXT NOT NULL,
                created_utc INTEGER, model TEXT, text TEXT NOT NULL, PRIMARY KEY(chat_id, branch, seq));
            CREATE TABLE IF NOT EXISTS branches(chat_id TEXT NOT NULL, branch INTEGER NOT NULL, fork_at INTEGER NOT NULL, PRIMARY KEY(chat_id, branch));
            CREATE TABLE IF NOT EXISTS attachments(chat_id TEXT NOT NULL, seq INTEGER NOT NULL, name TEXT NOT NULL, size INTEGER NOT NULL,
                archive_entry TEXT, file_path TEXT, text TEXT, PRIMARY KEY(chat_id, seq));
            CREATE TABLE IF NOT EXISTS secrets(chat_id TEXT NOT NULL, rule_id TEXT NOT NULL, rule_title TEXT NOT NULL, masked TEXT NOT NULL,
                fingerprint TEXT NOT NULL, live INTEGER NOT NULL, advice TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_secrets_chat ON secrets(chat_id);
            CREATE TABLE IF NOT EXISTS tags(chat_id TEXT NOT NULL, tag TEXT NOT NULL, PRIMARY KEY(chat_id, tag));
            CREATE VIRTUAL TABLE IF NOT EXISTS chats_fts USING fts5(id UNINDEXED, title, body, tokenize = 'unicode61 remove_diacritics 2');
            """).ConfigureAwait(false); // boundary-ok: own data in DataRoot
        await Exec(c, null, "INSERT OR REPLACE INTO meta(key, value) VALUES('schema', $v)", ("$v", SchemaVersion.ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false); // boundary-ok: own data in DataRoot
    }

    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static async Task Exec(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        await using var cmd = Command(c, tx, sql, args);
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false); // boundary-ok: own data in DataRoot
    }

    private static async Task<object?> Scalar(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] args)
    {
        await using var cmd = Command(c, tx, sql, args);
        return await cmd.ExecuteScalarAsync().ConfigureAwait(false);
    }
}
