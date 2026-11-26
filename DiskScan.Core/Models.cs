namespace DiskScan.Core;

public sealed class ScanNode
{
    private string? _fullPath;

    public string Name { get; set; } = "";

    /// <summary>
    /// The item's path. Scanners store it only on the scanned root; below that it is built from
    /// the parent's path and the name when asked, so a drive of a million files does not keep a
    /// copy of every folder path per file. A path that is set is returned as set.
    /// </summary>
    public string FullPath
    {
        get => _fullPath ?? ComposePath();
        set => _fullPath = value;
    }
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public long AllocatedSize { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
    public FileAttributes Attributes { get; set; }
    public ScanNode? Parent { get; set; }
    public List<ScanNode> Children { get; } = [];
    public long FileCount { get; set; }
    public string Extension => IsDirectory ? "" : Path.GetExtension(Name).ToLowerInvariant();

    private string ComposePath()
    {
        if (Parent is null) return "";
        var length = 0;
        var anchor = this;
        while (anchor._fullPath is null && anchor.Parent is not null)
        {
            length += anchor.Name.Length + 1;
            anchor = anchor.Parent;
        }
        var basePath = anchor._fullPath ?? "";
        if (basePath.Length == 0 || basePath[^1] is '\\' or '/') length--; // no separator after the base
        return string.Create(basePath.Length + length, (Leaf: this, Anchor: anchor, Base: basePath), static (span, state) =>
        {
            var end = span.Length;
            for (var node = state.Leaf; !ReferenceEquals(node, state.Anchor); node = node.Parent!)
            {
                end -= node.Name.Length;
                node.Name.AsSpan().CopyTo(span[end..]);
                if (end > state.Base.Length) span[--end] = '\\';
            }
            state.Base.AsSpan().CopyTo(span);
        });
    }
}

public sealed record ScanProgress(long Files, long Directories, long Bytes, string CurrentPath, string Mode, TimeSpan Elapsed);
public sealed record ScanSummary(long Files, long Directories, long Skipped, TimeSpan Elapsed,
    string Mode, bool AllocationIsEstimated, IReadOnlyList<string> Warnings);
public sealed record ScanResult(ScanNode Root, IReadOnlyList<ScanNode> Files, ScanSummary Summary);

public enum SearchMode { FileName, Contents, NameOrContents }
/// <summary>Search request. Conditions (for example "ext:pdf size:>1mb") must match together with Query.</summary>
public sealed record SearchOptions(string Query, SearchMode Mode = SearchMode.NameOrContents,
    bool MatchCase = false, int MaxResults = 10000, string? Conditions = null);
public sealed record SearchProgress(long Examined, long Total, long Matches, long Skipped);
/// <summary>MatchKind is "Name", "Contents" or "Condition" (matched by conditions such as ext: or size: only).</summary>
public sealed record SearchHit(ScanNode Node, string MatchKind, string Preview, long LineNumber = 0);
public sealed record SearchReport(IReadOnlyList<SearchHit> Hits, long Examined, long Excluded,
    long Skipped, bool LimitReached, TimeSpan Elapsed, long ContentFiles = 0);
