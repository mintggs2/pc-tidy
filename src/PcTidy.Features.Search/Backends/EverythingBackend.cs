using System.Diagnostics;
using System.Runtime.InteropServices;
using PcTidy.Features.Search.Query;
using PcTidy.Features.Search.Results;

namespace PcTidy.Features.Search.Backends;

/// <summary>
/// Asks a running Everything (voidtools) through its SDK. The query text is passed through unchanged: PC Tidy's
/// syntax is a subset of Everything's, so <c>ext:</c>, <c>size:</c>, <c>dm:</c>, <c>path:</c>, <c>folder:</c>/<c>file:</c>,
/// <c>|</c>, <c>!</c> and quotes mean the same. (Everything's own <c>dupe:</c> compares names only.)
/// The SDK library is loaded from the running Everything's folder only, and only when that folder is admin-only and the
/// library is signed by voidtools (<see cref="EverythingSdkTrust"/>); never downloaded. It keeps one query at a time
/// (it has global state), so calls are serialised. Any failure makes the router fall back to the own index.
/// </summary>
public sealed class EverythingBackend : ISearchBackend
{
    public const string BackendName = "Everything";
    /// <summary>Everything answers in pages; more than this isn't useful in a list and costs memory.</summary>
    public const int MaxFetched = 50_000;
    private static readonly TimeSpan RecheckEvery = TimeSpan.FromSeconds(10);
    private static readonly object SdkGate = new();

    private const uint RequestFileName = 0x1, RequestPath = 0x2, RequestSize = 0x10, RequestDateModified = 0x40;
    private const uint SortNameAscending = 1;

    private readonly IEverythingLocator _locator;
    private readonly TimeProvider _clock;
    private Sdk? _sdk;
    private string? _sdkPath;
    private long _checkedAtTicks = DateTimeOffset.MinValue.UtcTicks;
    private volatile bool _available;
    private int _refreshing;
    /// <summary>Queries still inside Everything (a timed-out one keeps running until Everything answers).</summary>
    private int _inFlight;

    public EverythingBackend(IEverythingLocator locator, TimeProvider? clock = null)
    {
        _locator = locator;
        _clock = clock ?? TimeProvider.System;
    }

    public string Name => BackendName;

    /// <summary>True while an earlier query is still waiting for Everything: new searches use the own index meanwhile.</summary>
    public bool IsBusy => Volatile.Read(ref _inFlight) > 0;

    private bool IsFresh => _clock.GetUtcNow().UtcTicks - Interlocked.Read(ref _checkedAtTicks) < RecheckEvery.Ticks;

    /// <summary>
    /// The last known answer, never blocking: the UI thread asks this for the status line. A stale answer starts a
    /// background check (looking for the Everything window and checking the SDK's signature can take a moment).
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            if (IsBusy) return false;
            if (!IsFresh && Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
                _ = Task.Run(() => { try { Refresh(); } finally { Volatile.Write(ref _refreshing, 0); } });
            return _available;
        }
    }

    /// <summary>An up-to-date answer (runs the check now if it's stale). Called off the UI thread before a search.</summary>
    public bool CheckAvailable()
    {
        if (IsBusy) return false;
        if (!IsFresh) Refresh();
        return _available && !IsBusy;
    }

    /// <summary>
    /// Looks for a running Everything and loads its SDK. Never waits for a running query: the SDK keeps one query at a
    /// time, so if one holds it, this check is simply skipped (Everything counts as busy).
    /// </summary>
    private void Refresh()
    {
        if (!Monitor.TryEnter(SdkGate)) return;
        try
        {
            if (IsFresh) return;
            Interlocked.Exchange(ref _checkedAtTicks, _clock.GetUtcNow().UtcTicks);
            bool available = false;
            var install = _locator.Locate();
            if (install is not null)
            {
                if (_sdk is null || !string.Equals(_sdkPath, install.SdkPath, StringComparison.OrdinalIgnoreCase))
                {
                    _sdk = Sdk.TryLoad(install.SdkPath);
                    _sdkPath = install.SdkPath;
                }
                try { available = _sdk is not null && _sdk.IsDBLoaded(); }
                catch (Exception ex) when (ex is SEHException or AccessViolationException or EntryPointNotFoundException) { _sdk = null; }
            }
            _available = available;
        }
        finally { Monitor.Exit(SdkGate); }
    }

    /// <summary>Forget the last check (e.g. after Everything closed mid-query).</summary>
    public void Invalidate() => Interlocked.Exchange(ref _checkedAtTicks, DateTimeOffset.MinValue.UtcTicks);

    /// <summary>
    /// Runs the query on its own thread: the SDK's <c>QueryW</c> blocks until Everything answers and can't be cancelled,
    /// so it must not hold a pool thread or the caller. The router stops waiting after a few seconds (or on a cancel)
    /// and answers from the own index; this thread finishes in the background, and Everything counts as busy
    /// (<see cref="IsBusy"/>) until it does, so the next search doesn't queue behind it.
    /// </summary>
    public Task<SearchResults> SearchAsync(ParsedQuery query, int maxResults, CancellationToken ct)
    {
        if (query.IsEmpty) return Task.FromResult(SearchResults.Empty(BackendName, query.Warnings));
        if (ct.IsCancellationRequested) return Task.FromCanceled<SearchResults>(ct);
        var done = new TaskCompletionSource<SearchResults>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Increment(ref _inFlight);
        var thread = new Thread(() =>
        {
            try { done.TrySetResult(Run(query, Math.Min(maxResults, MaxFetched), ct)); }
            catch (Exception ex) { done.TrySetException(ex); }
            finally { Interlocked.Decrement(ref _inFlight); }
        }) { IsBackground = true, Name = "PC Tidy Everything query" };
        thread.Start();
        _ = done.Task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // a late failure nobody waits for
        return done.Task;
    }

    private SearchResults Run(ParsedQuery query, int max, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        lock (SdkGate)
        {
            var sdk = _sdk ?? throw new InvalidOperationException("Everything isn't available.");
            ct.ThrowIfCancellationRequested();
            sdk.SetSearchW(query.Text);
            sdk.SetRequestFlags(RequestFileName | RequestPath | RequestSize | RequestDateModified);
            sdk.SetSort(SortNameAscending);
            sdk.SetMax((uint)max);
            if (!sdk.QueryW(true)) throw new InvalidOperationException($"Everything didn't answer (error {sdk.GetLastError()}).");
            uint n = sdk.GetNumResults();
            uint total = sdk.GetTotResults();
            var hits = new List<SearchHit>((int)n);
            for (uint i = 0; i < n; i++)
            {
                if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
                var name = Marshal.PtrToStringUni(sdk.GetResultFileNameW(i)) ?? "";
                var folder = Marshal.PtrToStringUni(sdk.GetResultPathW(i)) ?? "";
                bool isFolder = sdk.IsFolderResult(i);
                long size = sdk.GetResultSize(i, out var s) && !isFolder ? Math.Max(0, s) : 0;
                var modified = sdk.GetResultDateModified(i, out var ft) && ft > 0 && ft < DateTime.MaxValue.ToFileTimeUtc()
                    ? DateTime.FromFileTimeUtc(ft) : DateTime.MinValue;
                hits.Add(new SearchHit((int)i, name, folder, isFolder, size, modified));
            }
            var notes = query.Dupe
                ? query.Warnings.Append("Everything's dupe: compares names only (PC Tidy's own index also compares sizes).").ToList()
                : query.Warnings;
            return new ListResults(BackendName, hits, sw.Elapsed, notes, total > n);
        }
    }

    /// <summary>The SDK's exports, bound at run time so a missing or different DLL is just "not available".</summary>
    private sealed class Sdk
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] public delegate void SetSearchWFn(string search);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void SetUIntFn(uint value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] [return: MarshalAs(UnmanagedType.Bool)] public delegate bool QueryWFn([MarshalAs(UnmanagedType.Bool)] bool wait);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate uint GetUIntFn();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] [return: MarshalAs(UnmanagedType.Bool)] public delegate bool GetBoolFn();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate IntPtr GetStringFn(uint index);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] [return: MarshalAs(UnmanagedType.Bool)] public delegate bool IndexBoolFn(uint index);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] [return: MarshalAs(UnmanagedType.Bool)] public delegate bool GetLongFn(uint index, out long value);

        public required SetSearchWFn SetSearchW { get; init; }
        public required SetUIntFn SetRequestFlags { get; init; }
        public required SetUIntFn SetSort { get; init; }
        public required SetUIntFn SetMax { get; init; }
        public required QueryWFn QueryW { get; init; }
        public required GetUIntFn GetNumResults { get; init; }
        public required GetUIntFn GetTotResults { get; init; }
        public required GetUIntFn GetLastError { get; init; }
        public required GetBoolFn IsDBLoaded { get; init; }
        public required GetStringFn GetResultFileNameW { get; init; }
        public required GetStringFn GetResultPathW { get; init; }
        public required IndexBoolFn IsFolderResult { get; init; }
        public required GetLongFn GetResultSize { get; init; }
        /// <summary>FILETIME as a 64-bit value.</summary>
        public required GetLongFn GetResultDateModified { get; init; }

        /// <summary>Only an SDK in an admin-only folder, signed by voidtools (<see cref="EverythingSdkTrust"/>); anything else is "not available".</summary>
        public static Sdk? TryLoad(string path)
        {
            var lib = EverythingSdkTrust.LoadTrusted(path, out _);
            if (lib == IntPtr.Zero) return null;
            try
            {
                T F<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
                return new Sdk
                {
                    SetSearchW = F<SetSearchWFn>("Everything_SetSearchW"),
                    SetRequestFlags = F<SetUIntFn>("Everything_SetRequestFlags"),
                    SetSort = F<SetUIntFn>("Everything_SetSort"),
                    SetMax = F<SetUIntFn>("Everything_SetMax"),
                    QueryW = F<QueryWFn>("Everything_QueryW"),
                    GetNumResults = F<GetUIntFn>("Everything_GetNumResults"),
                    GetTotResults = F<GetUIntFn>("Everything_GetTotResults"),
                    GetLastError = F<GetUIntFn>("Everything_GetLastError"),
                    IsDBLoaded = F<GetBoolFn>("Everything_IsDBLoaded"),
                    GetResultFileNameW = F<GetStringFn>("Everything_GetResultFileNameW"),
                    GetResultPathW = F<GetStringFn>("Everything_GetResultPathW"),
                    IsFolderResult = F<IndexBoolFn>("Everything_IsFolderResult"),
                    GetResultSize = F<GetLongFn>("Everything_GetResultSize"),
                    GetResultDateModified = F<GetLongFn>("Everything_GetResultDateModified"),
                };
            }
            catch (EntryPointNotFoundException)
            {
                NativeLibrary.Free(lib);
                return null;
            }
        }
    }
}
