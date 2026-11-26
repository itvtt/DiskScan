using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace DiskScan.Core;

/// <summary>
/// File search over a scanned tree. Names (files and folders) are checked first in
/// memory and returned at once, ranked by how well they match; file contents are
/// then read in parallel and streamed as they match.
/// </summary>
public sealed class ContentSearchService
{
    private const int ReadBufferCharacters = 32 * 1024;
    private const int ProbeBytes = 8 * 1024;
    public const long MaximumOfficeExpandedXmlBytes = 256L * 1024 * 1024;
    public const int MaximumOfficeArchiveEntries = 20_000;

    static ContentSearchService()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
        catch { /* code pages unavailable: CP949 text is then skipped */ }
    }

    public static IReadOnlyList<string> SupportedOfficeFileExtensions { get; } = Array.AsReadOnly(
    [
        ".docx", ".docm", ".dotx", ".dotm", ".xlsx", ".xlsm", ".xltx", ".xltm",
        ".pptx", ".pptm", ".potx", ".potm", ".ppsx", ".ppsm", ".hwpx", ".odt", ".ods", ".odp",
        ".hwp", ".doc", ".dot", ".xls", ".xlt", ".ppt", ".pps", ".pot", ".pdf"
    ]);

    /// <summary>What content search can read.</summary>
    public static string CapabilitySummary =>
        "Text/code/log files (UTF-8, UTF-16/32, CP949), plausible text with unknown extensions, " +
        "Office Open XML (.docx/.xlsx/.pptx and macro/template variants), HWPX, OpenDocument, HWP 5, " +
        "legacy .doc/.xls/.ppt and PDF text. Scanned images (OCR), encrypted documents and archives are not read.";

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".md", ".markdown", ".log", ".csv", ".tsv", ".xml", ".json", ".jsonl",
        ".html", ".htm", ".css", ".scss", ".less", ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx",
        ".cs", ".fs", ".vb", ".java", ".kt", ".kts", ".c", ".h", ".cpp", ".hpp", ".cc", ".go",
        ".rs", ".py", ".rb", ".php", ".swift", ".sql", ".r", ".lua", ".sh", ".bash", ".zsh",
        ".ps1", ".bat", ".cmd", ".ini", ".cfg", ".conf", ".config", ".properties", ".toml", ".yaml",
        ".yml", ".xaml", ".csproj", ".fsproj", ".vbproj", ".sln", ".props", ".targets", ".gradle",
        ".cmake", ".makefile", ".dockerfile", ".gitignore", ".gitattributes", ".editorconfig", ".env"
    };

    public static IReadOnlyList<string> SupportedTextFileExtensions { get; } =
        Array.AsReadOnly(TextExtensions.Order(StringComparer.OrdinalIgnoreCase).ToArray());

    private static readonly HashSet<string> KnownBinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".com", ".obj", ".pdb", ".bin", ".dat", ".db", ".sqlite", ".mdb", ".accdb",
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".ico", ".heic", ".raw", ".avif", ".psd",
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a", ".opus", ".mp4", ".mkv", ".avi", ".mov", ".wmv",
        ".m4v", ".webm", ".mpg", ".mpeg", ".3gp", ".zip", ".rar", ".7z", ".gz", ".bz2", ".xz", ".tar", ".tgz",
        ".zst", ".lz4", ".iso", ".img", ".vhd", ".vhdx", ".vmdk", ".wim", ".esd", ".cab", ".msi", ".msix", ".appx",
        ".apk", ".jar", ".war", ".class", ".pyc", ".pyd", ".whl", ".nupkg", ".snupkg", ".lib", ".a", ".so",
        ".dylib", ".pack", ".idx", ".woff", ".woff2", ".ttf", ".otf", ".pst", ".ost", ".edb", ".dmp", ".etl",
        ".evtx", ".pf", ".blf", ".regtrans-ms", ".ldf", ".mdf", ".ndf"
    };

    public async Task<SearchReport> SearchAsync(
        IEnumerable<ScanNode> files,
        SearchOptions options,
        IProgress<SearchProgress>? progress = null,
        CancellationToken cancellationToken = default,
        IProgress<IReadOnlyList<SearchHit>>? hitProgress = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxResults <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxResults must be greater than zero.");

        cancellationToken.ThrowIfCancellationRequested();
        var plan = new SearchPlan(QueryExpression.Parse(options.Query, options.Conditions), options);
        var stopwatch = Stopwatch.StartNew();

        // Snapshot, eligibility and the name pass are CPU-only and run off the
        // caller's thread (commonly the UI thread).
        NamePass names = await Task.Run(() => RunNamePass(files, plan, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        var hits = new List<SearchHit>(Math.Min(options.MaxResults, 4096));
        bool limitReached = false;

        void Emit(List<SearchHit> batch)
        {
            if (batch.Count == 0)
                return;
            hits.AddRange(batch);
            hitProgress?.Report(batch.ToArray());
        }

        var exact = names.Exact;
        if (exact.Count > options.MaxResults)
        {
            exact = exact.GetRange(0, options.MaxResults);
            limitReached = true;
        }
        Emit(exact.ConvertAll(item => item.Hit));

        long examined = 0, skipped = 0;
        int contentTotal = names.ContentCandidates.Count;
        progress?.Report(new SearchProgress(0, contentTotal, hits.Count, 0));

        if (!limitReached && contentTotal > 0)
        {
            ScanNode[] candidates = OrderContentCandidates(names.ContentCandidates, names.Flags);
            var found = new ConcurrentQueue<SearchHit>();
            int accepted = hits.Count;
            object flushLock = new();
            long nextFlush = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 8;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            void Flush(bool force)
            {
                long now = Stopwatch.GetTimestamp();
                if (!force && now < Volatile.Read(ref nextFlush))
                    return;
                if (!Monitor.TryEnter(flushLock))
                {
                    if (!force)
                        return;
                    Monitor.Enter(flushLock);
                }
                try
                {
                    Volatile.Write(ref nextFlush, now + Stopwatch.Frequency / 8);
                    var batch = new List<SearchHit>();
                    while (found.TryDequeue(out SearchHit? hit))
                        batch.Add(hit);
                    Emit(batch);
                    progress?.Report(new SearchProgress(Interlocked.Read(ref examined), contentTotal,
                        hits.Count, Interlocked.Read(ref skipped)));
                }
                finally
                {
                    Monitor.Exit(flushLock);
                }
            }

            try
            {
                await Task.Run(() => Parallel.ForEach(
                    candidates,
                    new ParallelOptions
                    {
                        CancellationToken = limit.Token,
                        MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount * 2, 4, 16)
                    },
                    node =>
                    {
                        ContentOutcome outcome = SearchContents(node, plan, limit.Token);
                        Interlocked.Increment(ref examined);
                        if (outcome.Skipped)
                            Interlocked.Increment(ref skipped);
                        if (outcome.Hit is not null)
                        {
                            if (Interlocked.Increment(ref accepted) <= options.MaxResults)
                                found.Enqueue(outcome.Hit);
                            else
                            {
                                limitReached = true;
                                limit.Cancel();
                            }
                        }
                        Flush(force: false);
                    }), CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && limitReached)
            {
                // The result limit stopped the remaining reads.
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref accepted) >= options.MaxResults && examined < contentTotal)
                limitReached = true;
            Flush(force: true);
        }

        stopwatch.Stop();
        progress?.Report(new SearchProgress(examined, contentTotal, hits.Count, skipped));
        return new SearchReport(hits, names.Examined, names.Excluded, skipped, limitReached, stopwatch.Elapsed, examined);
    }

    // ───────────────────────── name pass ─────────────────────────

    private static NamePass RunNamePass(IEnumerable<ScanNode> files, SearchPlan plan, CancellationToken cancellationToken)
    {
        var nodes = new List<ScanNode>();
        foreach (ScanNode node in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(node);
            nodes.Add(node);
        }

        var flags = new SearchPolicy.Eligibility[nodes.Count];
        var cache = new SearchPolicy.EligibilityCache();
        for (int index = 0; index < nodes.Count; index++)
        {
            if ((index & 0xFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            flags[index] = cache.Get(nodes[index]);
        }

        var outcomes = new NameOutcome[nodes.Count];
        Parallel.For(0, nodes.Count, new ParallelOptions { CancellationToken = cancellationToken }, index =>
        {
            outcomes[index] = (flags[index] & SearchPolicy.Eligibility.HiddenFromNames) != 0
                ? new NameOutcome(NameResult.Excluded, 0, null)
                : plan.CheckName(nodes[index], flags[index]);
        });
        cancellationToken.ThrowIfCancellationRequested();

        var exact = new List<RankedHit>();
        var content = new List<ScanNode>();
        var contentFlags = new Dictionary<ScanNode, SearchPolicy.Eligibility>(ReferenceEqualityComparer.Instance);
        long excluded = 0;
        for (int index = 0; index < nodes.Count; index++)
        {
            NameOutcome outcome = outcomes[index];
            ScanNode node = nodes[index];
            switch (outcome.Result)
            {
                case NameResult.Excluded:
                    excluded++;
                    break;
                case NameResult.Hit:
                    exact.Add(new RankedHit(outcome.Hit!, RankKey.Create(node, flags[index], outcome.Quality)));
                    break;
                case NameResult.NeedsContent:
                    content.Add(node);
                    contentFlags[node] = flags[index];
                    break;
            }
        }

        exact.Sort(static (left, right) => left.Key.CompareTo(right.Key));
        return new NamePass(exact, content, contentFlags, nodes.Count - excluded, excluded);
    }

    private static ScanNode[] OrderContentCandidates(List<ScanNode> candidates, Dictionary<ScanNode, SearchPolicy.Eligibility> flags)
    {
        // The user's own files first; build output, caches and tool folders after.
        const SearchPolicy.Eligibility later = SearchPolicy.Eligibility.Noisy | SearchPolicy.Eligibility.OperatingSystem;
        var ordered = new List<ScanNode>(candidates.Count);
        ordered.AddRange(candidates.Where(node => (flags.GetValueOrDefault(node) & later) == 0));
        ordered.AddRange(candidates.Where(node => (flags.GetValueOrDefault(node) & later) != 0));
        return ordered.ToArray();
    }

    // ───────────────────────── content pass ─────────────────────────

    private static ContentOutcome SearchContents(ScanNode node, SearchPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var matcher = plan.CreateContentMatcher(node);
        ReadStatus status;
        try
        {
            status = FormatOf(node) switch
            {
                ContentFormat.ZipXml => ReadZipXml(node, matcher, cancellationToken),
                ContentFormat.Hwp => LegacyDocumentText.ReadHwp(node.FullPath, matcher, cancellationToken),
                ContentFormat.LegacyOffice => LegacyDocumentText.ReadLegacyOffice(node.FullPath, node.Extension, matcher, cancellationToken),
                ContentFormat.Pdf => PdfText.Read(node.FullPath, matcher, cancellationToken),
                ContentFormat.Text => ReadText(node, matcher, cancellationToken),
                _ => ReadStatus.NotSearchable
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException
                                       or NotSupportedException or DecoderFallbackException or ArgumentException
                                       or IndexOutOfRangeException or OverflowException or FormatException)
        {
            status = ReadStatus.NotSearchable;
        }

        bool skipped = status == ReadStatus.NotSearchable;
        SearchHit? hit = plan.FinishContent(node, matcher, readable: !skipped);
        return new ContentOutcome(hit, skipped);
    }

    internal enum ContentFormat { None, Text, ZipXml, Hwp, LegacyOffice, Pdf }

    internal static ContentFormat FormatOf(ScanNode node)
    {
        if (node.IsDirectory)
            return ContentFormat.None;
        string extension = node.Extension;
        if (ZipFamily(extension) is not null)
            return ContentFormat.ZipXml;
        return extension switch
        {
            ".hwp" => ContentFormat.Hwp,
            ".doc" or ".dot" or ".xls" or ".xlt" or ".ppt" or ".pps" or ".pot" => ContentFormat.LegacyOffice,
            ".pdf" => ContentFormat.Pdf,
            _ => KnownBinaryExtensions.Contains(extension) ? ContentFormat.None : ContentFormat.Text
        };
    }

    internal enum ReadStatus { NotSearchable, Completed, Decided }

    private static ReadStatus ReadText(ScanNode node, ContentMatcher matcher, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);

        Encoding? encoding;
        byte[] probe = ArrayPool<byte>.Shared.Rent(ProbeBytes);
        try
        {
            int probeLength = stream.ReadAtLeast(probe.AsSpan(0, ProbeBytes), ProbeBytes, throwOnEndOfStream: false);
            encoding = DetectTextEncoding(probe.AsSpan(0, probeLength), probeLength < ProbeBytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(probe);
        }
        if (encoding is null)
            return ReadStatus.NotSearchable;

        stream.Position = 0;
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true,
            bufferSize: 16 * 1024, leaveOpen: false);
        char[] buffer = ArrayPool<char>.Shared.Rent(ReadBufferCharacters);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = reader.Read(buffer, 0, ReadBufferCharacters);
                if (read == 0)
                    return ReadStatus.Completed;
                if (matcher.Feed(buffer.AsSpan(0, read)))
                    return ReadStatus.Decided;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    internal static Encoding? DetectTextEncoding(ReadOnlySpan<byte> bytes, bool isWholeFile)
    {
        if (HasUnicodeBom(bytes) || bytes.IsEmpty)
            return new UTF8Encoding(false, false);

        int zeros = 0, evenZeros = 0, oddZeros = 0, controls = 0;
        for (int index = 0; index < bytes.Length; index++)
        {
            byte value = bytes[index];
            if (value == 0)
            {
                zeros++;
                if ((index & 1) == 0) evenZeros++; else oddZeros++;
            }
            else if (value < 0x20 && value is not (0x09 or 0x0A or 0x0C or 0x0D or 0x1B))
            {
                controls++;
            }
        }

        // UTF-16 without a byte order mark: mostly-ASCII text has a zero in every other byte.
        int half = Math.Max(1, bytes.Length / 2);
        if (bytes.Length >= 16 && oddZeros * 10 > half * 4 && evenZeros * 20 < half)
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: false);
        if (bytes.Length >= 16 && evenZeros * 10 > half * 4 && oddZeros * 20 < half)
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: false);

        if (zeros != 0 || controls * 100 > bytes.Length * 2)
            return null;

        if (Decodes(new UTF8Encoding(false, true), bytes, isWholeFile))
            return new UTF8Encoding(false, false);

        // Korean ANSI text (CP949 / EUC-KR) written by older Windows tools.
        try
        {
            Encoding strict = Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            if (Decodes(strict, bytes, isWholeFile))
                return Encoding.GetEncoding(949);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
        }
        return null;
    }

    private static bool Decodes(Encoding strict, ReadOnlySpan<byte> bytes, bool flush)
    {
        try
        {
            _ = strict.GetDecoder().GetCharCount(bytes, flush);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool HasUnicodeBom(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ||
        bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) ||
        bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]) ||
        bytes.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]);

    // ───────────────────────── zip + XML documents ─────────────────────────

    private static string? ZipFamily(string extension) => extension switch
    {
        ".docx" or ".docm" or ".dotx" or ".dotm" => "word",
        ".xlsx" or ".xlsm" or ".xltx" or ".xltm" => "xl",
        ".pptx" or ".pptm" or ".potx" or ".potm" or ".ppsx" or ".ppsm" => "ppt",
        ".hwpx" => "hwpx",
        ".odt" or ".ods" or ".odp" or ".odg" => "odf",
        _ => null
    };

    private static ReadStatus ReadZipXml(ScanNode node, ContentMatcher matcher, CancellationToken cancellationToken)
    {
        string family = ZipFamily(node.Extension)!;
        using var stream = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        if (archive.Entries.Count > MaximumOfficeArchiveEntries)
            return ReadStatus.NotSearchable;

        long expandedBytes = 0;
        bool anyPart = false;
        char[] valueBuffer = ArrayPool<char>.Shared.Rent(ReadBufferCharacters);
        try
        {
            foreach (ZipArchiveEntry entry in archive.Entries
                         .Where(entry => IsSearchableZipXml(entry.FullName, family))
                         .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Length < 0 || expandedBytes > MaximumOfficeExpandedXmlBytes - entry.Length)
                    return ReadStatus.NotSearchable;
                expandedBytes += entry.Length;
                anyPart = true;

                using Stream entryStream = entry.Open();
                using XmlReader xml = XmlReader.Create(entryStream, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    IgnoreComments = true,
                    IgnoreProcessingInstructions = true
                });

                if (!xml.CanReadValueChunk)
                    return ReadStatus.NotSearchable;

                while (xml.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((xml.NodeType == XmlNodeType.EndElement && IsZipTextBoundary(xml.LocalName, entry.FullName, family)) ||
                        (xml.NodeType == XmlNodeType.Element && IsZipExplicitBreak(xml.LocalName, family)))
                    {
                        if (matcher.Feed(" "))
                            return ReadStatus.Decided;
                        continue;
                    }

                    if (xml.NodeType is not (XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace))
                        continue;

                    while (true)
                    {
                        int read = xml.ReadValueChunk(valueBuffer, 0, ReadBufferCharacters);
                        if (read == 0)
                            break;
                        if (matcher.Feed(valueBuffer.AsSpan(0, read)))
                            return ReadStatus.Decided;
                    }
                }

                if (matcher.Feed(" "))
                    return ReadStatus.Decided;
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(valueBuffer);
        }

        return anyPart ? ReadStatus.Completed : ReadStatus.NotSearchable;
    }

    private static bool IsSearchableZipXml(string entryName, string family)
    {
        string normalized = entryName.Replace('\\', '/');
        if (!normalized.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            return false;

        return family switch
        {
            "word" => normalized.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase) ||
                      normalized.StartsWith("word/header", StringComparison.OrdinalIgnoreCase) ||
                      normalized.StartsWith("word/footer", StringComparison.OrdinalIgnoreCase) ||
                      normalized.Equals("word/footnotes.xml", StringComparison.OrdinalIgnoreCase) ||
                      normalized.Equals("word/endnotes.xml", StringComparison.OrdinalIgnoreCase) ||
                      normalized.Equals("word/comments.xml", StringComparison.OrdinalIgnoreCase),
            "xl" => normalized.Equals("xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase) ||
                    normalized.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase),
            "ppt" => normalized.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) ||
                     normalized.StartsWith("ppt/notesSlides/notesSlide", StringComparison.OrdinalIgnoreCase),
            "hwpx" => normalized.StartsWith("Contents/section", StringComparison.OrdinalIgnoreCase),
            "odf" => normalized.Equals("content.xml", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool IsZipTextBoundary(string localName, string entryName, string family)
    {
        switch (family)
        {
            case "word":
            case "ppt":
            case "hwpx":
                return localName.Equals("p", StringComparison.OrdinalIgnoreCase);
            case "odf":
                return localName is "p" or "h" or "table-cell" or "list-item";
            case "xl":
                string normalized = entryName.Replace('\\', '/');
                return normalized.Equals("xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase)
                    ? localName.Equals("si", StringComparison.OrdinalIgnoreCase)
                    : localName.Equals("c", StringComparison.OrdinalIgnoreCase);
            default:
                return false;
        }
    }

    private static bool IsZipExplicitBreak(string localName, string family) => family switch
    {
        "word" => localName.Equals("br", StringComparison.OrdinalIgnoreCase) ||
                  localName.Equals("cr", StringComparison.OrdinalIgnoreCase) ||
                  localName.Equals("tab", StringComparison.OrdinalIgnoreCase),
        "ppt" => localName.Equals("br", StringComparison.OrdinalIgnoreCase),
        "hwpx" => localName is "lineBreak" or "tab",
        "odf" => localName is "line-break" or "tab" or "s",
        _ => false
    };

    // ───────────────────────── query plan ─────────────────────────

    /// <summary>Per-search view of the expression: which terms are known from metadata and which need contents.</summary>
    private sealed class SearchPlan
    {
        private readonly QueryExpression _expression;
        private readonly SearchTerm[] _terms;
        private readonly SearchMode _mode;
        private readonly StringComparison _comparison;
        private readonly bool _hasContentTerms;

        public SearchPlan(QueryExpression expression, SearchOptions options)
        {
            _expression = expression;
            _terms = expression.Terms.ToArray();
            _mode = options.Mode;
            _comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            _hasContentTerms = _terms.Any(IsContentTerm);
        }

        private bool IsContentTerm(SearchTerm term) => term.Field switch
        {
            SearchTermField.Content => true,
            SearchTermField.Default => _mode != SearchMode.FileName && !term.IsWildcard,
            _ => false
        };

        public NameOutcome CheckName(ScanNode node, SearchPolicy.Eligibility flags)
        {
            string name = QueryExpression.Normalize(node.Name ?? string.Empty);
            bool readable = _hasContentTerms && !node.IsDirectory &&
                            (flags & SearchPolicy.Eligibility.ContentExcluded) == 0 &&
                            FormatOf(node) != ContentFormat.None;
            Span<sbyte> states = stackalloc sbyte[_terms.Length];
            int quality = ComputeStates(node, name, readable, states, out bool nameMatched);
            sbyte result = _expression.Evaluate(states);

            if (result > 0)
                return new NameOutcome(NameResult.Hit, quality,
                    new SearchHit(node, nameMatched ? "Name" : "Condition", string.Empty));

            return result == 0
                ? new NameOutcome(NameResult.NeedsContent, 0, null)
                : new NameOutcome(NameResult.None, 0, null);
        }

        /// <summary>Fills term states from metadata. Returns the best name-match quality (lower is better).</summary>
        private int ComputeStates(ScanNode node, string name, bool readable, Span<sbyte> states, out bool nameMatched)
        {
            int quality = 9;
            nameMatched = false;
            for (int index = 0; index < _terms.Length; index++)
            {
                SearchTerm term = _terms[index];
                sbyte state;
                switch (term.Field)
                {
                    case SearchTermField.Default when term.IsWildcard:
                    case SearchTermField.Name:
                    case SearchTermField.Default when _mode != SearchMode.Contents:
                        int found = NameMatching.Quality(term, name, node.IsDirectory, _comparison);
                        if (found >= 0)
                        {
                            state = 1;
                            nameMatched = true;
                            quality = Math.Min(quality, found);
                        }
                        else
                        {
                            state = term.Field == SearchTermField.Default && !term.IsWildcard && _mode == SearchMode.NameOrContents && readable
                                ? (sbyte)0
                                : (sbyte)-1;
                        }
                        break;
                    case SearchTermField.Default:
                    case SearchTermField.Content:
                        state = readable ? (sbyte)0 : (sbyte)-1;
                        break;
                    case SearchTermField.Path:
                        state = (node.FullPath ?? string.Empty).Contains(term.Text, _comparison) ? (sbyte)1 : (sbyte)-1;
                        break;
                    case SearchTermField.Extension:
                        state = !node.IsDirectory && Array.IndexOf(term.Extensions, node.Extension) >= 0 ? (sbyte)1 : (sbyte)-1;
                        break;
                    case SearchTermField.Size:
                        state = node.Size >= term.MinimumSize && node.Size <= term.MaximumSize ? (sbyte)1 : (sbyte)-1;
                        break;
                    case SearchTermField.Modified:
                        state = InDateRange(node.LastWriteTimeUtc, term) ? (sbyte)1 : (sbyte)-1;
                        break;
                    case SearchTermField.Kind:
                        state = node.IsDirectory == term.WantsFolder ? (sbyte)1 : (sbyte)-1;
                        break;
                    default:
                        state = -1;
                        break;
                }
                states[index] = state;
            }
            return quality;
        }

        private static bool InDateRange(DateTime utc, SearchTerm term)
        {
            if (utc == default)
                return false;
            DateTime local = utc.ToLocalTime();
            return (term.FromLocal is not { } from || local >= from) &&
                   (term.BeforeLocal is not { } before || local < before);
        }

        public ContentMatcher CreateContentMatcher(ScanNode node)
        {
            string name = QueryExpression.Normalize(node.Name ?? string.Empty);
            var states = new sbyte[_terms.Length];
            ComputeStates(node, name, readable: true, states, out bool nameMatched);
            return new ContentMatcher(_expression, _terms, states, _comparison, nameMatched);
        }

        public SearchHit? FinishContent(ScanNode node, ContentMatcher matcher, bool readable)
        {
            if (!readable && _mode == SearchMode.Contents)
                return null;
            if (matcher.Finish() <= 0)
                return null;
            if (matcher.Details is { } details)
                return new SearchHit(node, "Contents", details.Preview, details.LineNumber);
            return new SearchHit(node, matcher.NameMatched ? "Name" : "Condition", string.Empty);
        }
    }

    /// <summary>Streams text through the unknown content terms and decides the expression as early as possible.</summary>
    internal sealed class ContentMatcher
    {
        private readonly QueryExpression _expression;
        private readonly SearchTerm[] _terms;
        private readonly sbyte[] _states;
        private readonly StringComparison _comparison;
        private readonly StreamingTermMatcher?[] _matchers;
        private sbyte _result;

        public ContentMatcher(QueryExpression expression, SearchTerm[] terms, sbyte[] states, StringComparison comparison, bool nameMatched)
        {
            _expression = expression;
            _terms = terms;
            _states = states;
            _comparison = comparison;
            _matchers = new StreamingTermMatcher?[terms.Length];
            NameMatched = nameMatched;
            _result = expression.Evaluate(states);
        }

        public bool NameMatched { get; }
        public MatchDetails? Details { get; private set; }

        /// <summary>True once the outcome is decided and reading can stop.</summary>
        public bool IsDecided => _result != 0;

        public bool Feed(ReadOnlySpan<char> characters)
        {
            if (_result != 0)
                return true;
            if (characters.IsEmpty)
                return false;

            string incoming = characters.ToString();
            for (int index = 0; index < _states.Length; index++)
            {
                if (_states[index] != 0)
                    continue;

                StreamingTermMatcher matcher = _matchers[index] ??= new StreamingTermMatcher(_terms[index].Text, _comparison);
                MatchDetails? match = matcher.Feed(incoming);
                if (match is null)
                    continue;

                _states[index] = 1;
                Details ??= match;
                _result = _expression.Evaluate(_states);
                if (_result != 0)
                    return true;
            }
            return false;
        }

        public sbyte Finish()
        {
            if (_result != 0)
                return _result;
            for (int index = 0; index < _states.Length; index++)
            {
                if (_states[index] == 0)
                    _states[index] = -1;
            }
            _result = _expression.Evaluate(_states);
            return _result;
        }
    }

    private sealed class StreamingTermMatcher
    {
        private const int PreviewRadius = 80;
        private readonly string _query;
        private readonly StringComparison _comparison;
        private string _tail = string.Empty;
        private long _lineAtTailStart = 1;
        private bool _pendingCarriageReturn;

        public StreamingTermMatcher(string query, StringComparison comparison)
        {
            _query = query;
            _comparison = comparison;
        }

        public MatchDetails? Feed(string incoming)
        {
            if (incoming.Length == 0)
                return null;

            long lineAtIncomingStart = _lineAtTailStart;
            bool pendingAtIncomingStart = _pendingCarriageReturn;
            AdvanceLineState(_tail, ref lineAtIncomingStart, ref pendingAtIncomingStart);

            if (_tail.Length != 0)
            {
                int prefixLength = Math.Min(incoming.Length, _query.Length - 1);
                string bridge = string.Concat(_tail.AsSpan(), incoming.AsSpan(0, prefixLength));
                int bridgeIndex = bridge.IndexOf(_query, _comparison);
                if (bridgeIndex >= 0)
                    return CreateMatch(bridge, bridgeIndex, _lineAtTailStart, _pendingCarriageReturn);
            }

            int incomingIndex = incoming.IndexOf(_query, _comparison);
            if (incomingIndex >= 0)
                return CreateMatch(incoming, incomingIndex, lineAtIncomingStart, pendingAtIncomingStart);

            int keep = Math.Min(_query.Length - 1, _tail.Length + incoming.Length);
            if (keep == 0)
            {
                _lineAtTailStart = lineAtIncomingStart;
                _pendingCarriageReturn = pendingAtIncomingStart;
                AdvanceLineState(incoming, ref _lineAtTailStart, ref _pendingCarriageReturn);
                _tail = string.Empty;
            }
            else if (incoming.Length >= keep)
            {
                _lineAtTailStart = lineAtIncomingStart;
                _pendingCarriageReturn = pendingAtIncomingStart;
                AdvanceLineState(incoming.AsSpan(0, incoming.Length - keep),
                    ref _lineAtTailStart, ref _pendingCarriageReturn);
                _tail = incoming[^keep..];
            }
            else
            {
                int consumeFromTail = _tail.Length + incoming.Length - keep;
                AdvanceLineState(_tail.AsSpan(0, consumeFromTail),
                    ref _lineAtTailStart, ref _pendingCarriageReturn);
                _tail = string.Concat(_tail.AsSpan(consumeFromTail), incoming.AsSpan());
            }

            return null;
        }

        private MatchDetails CreateMatch(string text, int index, long startingLine, bool pendingCarriageReturn)
        {
            long line = startingLine;
            AdvanceLineState(text.AsSpan(0, index), ref line, ref pendingCarriageReturn);
            int previewStart = Math.Max(0, index - PreviewRadius);
            int previewEnd = Math.Min(text.Length, index + _query.Length + PreviewRadius);
            return new MatchDetails(NormalizePreview(text[previewStart..previewEnd]), line);
        }

        private static void AdvanceLineState(ReadOnlySpan<char> text, ref long line, ref bool pendingCarriageReturn)
        {
            foreach (char value in text)
            {
                if (value == '\r')
                {
                    line++;
                    pendingCarriageReturn = true;
                }
                else if (value == '\n')
                {
                    if (!pendingCarriageReturn)
                        line++;
                    pendingCarriageReturn = false;
                }
                else
                {
                    pendingCarriageReturn = false;
                }
            }
        }

        private static string NormalizePreview(string preview)
        {
            var builder = new StringBuilder(preview.Length);
            bool previousWhitespace = false;
            foreach (char value in preview)
            {
                bool whitespace = char.IsWhiteSpace(value) || char.IsControl(value);
                if (!whitespace || !previousWhitespace)
                    builder.Append(whitespace ? ' ' : value);
                previousWhitespace = whitespace;
            }
            return builder.ToString().Trim();
        }
    }

    // ───────────────────────── ranking ─────────────────────────

    private readonly record struct RankKey(int Quality, int OperatingSystem, int Noisy, int File, int Depth, int NameLength, string Path)
        : IComparable<RankKey>
    {
        public static RankKey Create(ScanNode node, SearchPolicy.Eligibility flags, int quality)
        {
            string path = node.FullPath ?? string.Empty;
            int depth = 0;
            foreach (char value in path)
            {
                if (value is '\\' or '/')
                    depth++;
            }
            return new RankKey(quality,
                (flags & SearchPolicy.Eligibility.OperatingSystem) != 0 ? 1 : 0,
                (flags & SearchPolicy.Eligibility.Noisy) != 0 ? 1 : 0,
                node.IsDirectory ? 0 : 1,
                depth,
                (node.Name ?? string.Empty).Length,
                path);
        }

        public int CompareTo(RankKey other)
        {
            int result = Quality.CompareTo(other.Quality);
            if (result == 0) result = OperatingSystem.CompareTo(other.OperatingSystem);
            if (result == 0) result = Noisy.CompareTo(other.Noisy);
            if (result == 0) result = File.CompareTo(other.File);
            if (result == 0) result = Depth.CompareTo(other.Depth);
            if (result == 0) result = NameLength.CompareTo(other.NameLength);
            if (result == 0) result = StringComparer.OrdinalIgnoreCase.Compare(Path, other.Path);
            return result;
        }
    }

    private enum NameResult : byte { None, Excluded, Hit, NeedsContent }
    private readonly record struct NameOutcome(NameResult Result, int Quality, SearchHit? Hit);
    private sealed record RankedHit(SearchHit Hit, RankKey Key);
    private sealed record NamePass(List<RankedHit> Exact, List<ScanNode> ContentCandidates,
        Dictionary<ScanNode, SearchPolicy.Eligibility> Flags, long Examined, long Excluded);
    internal sealed record MatchDetails(string Preview, long LineNumber);
    private readonly record struct ContentOutcome(SearchHit? Hit, bool Skipped);
}

/// <summary>Name comparison helpers: match quality and wildcards.</summary>
internal static class NameMatching
{
    /// <summary>-1 when the term does not match the name; otherwise 0 (whole name) … 3 (inside a word).</summary>
    public static int Quality(SearchTerm term, string name, bool isDirectory, StringComparison comparison)
    {
        bool ignoreCase = comparison != StringComparison.Ordinal;
        string stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        if (term.IsWildcard)
        {
            if (Wildcard(term.Text, name, ignoreCase))
                return 1;
            return !term.Text.Contains('.') && !isDirectory && Wildcard(term.Text, stem, ignoreCase) ? 1 : -1;
        }

        int index = name.IndexOf(term.Text, comparison);
        if (index < 0)
            return -1;
        if (name.Length == term.Text.Length || (stem.Length == term.Text.Length && index == 0))
            return 0;
        if (index == 0)
            return 1;
        char previous = name[index - 1];
        if (!char.IsLetterOrDigit(previous) || (char.IsLower(previous) && char.IsUpper(name[index])))
            return 2;
        return 3;
    }

    /// <summary>* matches any run of characters and ? one character; the whole text must match.</summary>
    public static bool Wildcard(string pattern, string text, bool ignoreCase)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || Same(pattern[p], text[t], ignoreCase)))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }

    private static bool Same(char left, char right, bool ignoreCase) =>
        left == right || (ignoreCase && char.ToUpperInvariant(left) == char.ToUpperInvariant(right));
}
