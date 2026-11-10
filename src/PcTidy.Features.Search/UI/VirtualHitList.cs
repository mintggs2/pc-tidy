using System.Collections;
using PcTidy.Core;
using PcTidy.Features.Search.Results;

namespace PcTidy.Features.Search.UI;

/// <summary>One row as the list shows it (built only for rows on screen).</summary>
public sealed class HitRow
{
    public HitRow(SearchHit hit)
    {
        Hit = hit;
        SizeText = hit.IsFolder ? "" : Sizes.Format(hit.Size);
        ModifiedText = hit.ModifiedUtc == DateTime.MinValue ? "" : hit.ModifiedUtc.ToLocalTime().ToString("g");
    }

    public SearchHit Hit { get; }
    public string Name => Hit.Name;
    public string Folder => Hit.Folder;
    public string SizeText { get; }
    public string ModifiedText { get; }
    public Wpf.Ui.Controls.SymbolRegular Icon => Hit.IsFolder ? Wpf.Ui.Controls.SymbolRegular.Folder24 : Wpf.Ui.Controls.SymbolRegular.Document24;

    public override bool Equals(object? obj) => obj is HitRow r && r.Hit.Ordinal == Hit.Ordinal && r.Hit.Name == Hit.Name;
    public override int GetHashCode() => Hit.Ordinal;
}

/// <summary>
/// Read-only <see cref="IList"/> over <see cref="SearchResults"/>: WPF's virtualizing list only asks for the rows it
/// shows, so a million results cost one 8-byte entry each, not one object each. A small cache keeps scrolling smooth.
/// </summary>
public sealed class VirtualHitList : IList
{
    private const int CacheSize = 512;
    private readonly SearchResults _results;
    private readonly Dictionary<int, HitRow> _cache = new();
    private readonly Queue<int> _order = new();

    public VirtualHitList(SearchResults results) => _results = results;

    public SearchResults Results => _results;
    public int Count => _results.Count;
    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;

    public object? this[int index]
    {
        get => Row(index);
        set => throw new NotSupportedException();
    }

    public HitRow Row(int index)
    {
        if (_cache.TryGetValue(index, out var row)) return row;
        row = new HitRow(_results[index]);
        _cache[index] = row;
        _order.Enqueue(index);
        if (_order.Count > CacheSize) _cache.Remove(_order.Dequeue());
        return row;
    }

    public int IndexOf(object? value) => value is HitRow r && r.Hit.Ordinal < Count && Row(r.Hit.Ordinal).Equals(r) ? r.Hit.Ordinal : -1;
    public bool Contains(object? value) => IndexOf(value) >= 0;

    public IEnumerator GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return Row(i);
    }

    public void CopyTo(Array array, int index)
    {
        for (int i = 0; i < Count; i++) array.SetValue(Row(i), index + i); // boundary-ok: Array.SetValue, not the registry
    }

    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}
