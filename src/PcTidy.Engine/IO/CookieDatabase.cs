using Microsoft.Data.Sqlite;

namespace PcTidy.Engine.IO;

/// <summary>
/// The only code that touches a browser cookie database (SQLite), and only ever on PC Tidy's own temp COPY of it.
/// Chromium (Chrome, Edge, Brave...): table <c>cookies</c>, column <c>host_key</c>. Firefox: <c>moz_cookies</c>, <c>host</c>.
/// Kept in its own class so Microsoft.Data.Sqlite is loaded only when a cookie step actually runs.
/// </summary>
internal static class CookieDatabase
{
    public sealed record Result(int Deleted, int Kept);

    /// <summary>Deletes every cookie whose host isn't one of <paramref name="keep"/> (or a subdomain of one).</summary>
    public static Result DeleteExcept(string copyPath, bool firefox, IReadOnlyCollection<string> keep)
    {
        var (table, column) = firefox ? ("moz_cookies", "host") : ("cookies", "host_key");
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = LongPath.Io(copyPath),
            Mode = SqliteOpenMode.ReadWrite, // never creates a database
            Pooling = false,                 // the file is closed (and renamed) right after
        }.ToString();
        using var c = new SqliteConnection(cs);
        c.Open();
        RequireIntact(c);

        var hosts = new List<string>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = $"SELECT DISTINCT {column} FROM {table}";
            using var r = q.ExecuteReader();
            while (r.Read()) hosts.Add(r.IsDBNull(0) ? "" : r.GetString(0));
        }

        int deleted = 0;
        using (var tx = c.BeginTransaction())
        {
            foreach (var host in hosts.Where(h => !CookieHosts.IsKept(h, keep)))
            {
                using var d = c.CreateCommand();
                d.Transaction = tx;
                d.CommandText = $"DELETE FROM {table} WHERE {column} = $h";
                d.Parameters.AddWithValue("$h", host);
                deleted += d.ExecuteNonQuery();
            }
            tx.Commit();
        }
        int kept;
        using (var n = c.CreateCommand())
        {
            n.CommandText = $"SELECT COUNT(*) FROM {table}";
            kept = Convert.ToInt32(n.ExecuteScalar());
        }
        RequireIntact(c);
        return new Result(deleted, kept);
    }

    private static void RequireIntact(SqliteConnection c)
    {
        using var check = c.CreateCommand();
        check.CommandText = "PRAGMA integrity_check";
        if (check.ExecuteScalar() as string != "ok") throw new InvalidDataException("The cookie database is damaged, so PC Tidy didn't change it.");
    }
}

/// <summary>Keep-list matching: a kept site keeps its own cookies and its subdomains' (".example.com" and "a.example.com").</summary>
internal static class CookieHosts
{
    /// <summary>"https://www.Example.com:443/x" → "www.example.com"; ".example.com" → "example.com". Empty when unusable.</summary>
    public static string Normalise(string host)
    {
        var h = host.Trim().ToLowerInvariant();
        if (h.Contains("://") && Uri.TryCreate(h, UriKind.Absolute, out var uri)) h = uri.Host;
        var slash = h.IndexOf('/');
        if (slash >= 0) h = h[..slash];
        var colon = h.LastIndexOf(':');
        if (colon > 0 && !h.Contains(']')) h = h[..colon];
        return h.TrimStart('.').TrimEnd('.');
    }

    public static IReadOnlyCollection<string> Parse(string? keepHosts)
        => (keepHosts ?? "").Split('\n').Select(Normalise).Where(h => h.Length > 0).ToHashSet(StringComparer.Ordinal);

    public static bool IsKept(string cookieHost, IReadOnlyCollection<string> keep)
    {
        var h = Normalise(cookieHost);
        return h.Length > 0 && keep.Any(k => h == k || h.EndsWith("." + k, StringComparison.Ordinal));
    }
}
