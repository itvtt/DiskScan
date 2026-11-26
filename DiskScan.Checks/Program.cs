using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using DiskScan.Core;
using DiskScan.Core.Scanning;

var fixture = Path.Combine(Path.GetTempPath(), "DiskScan-Checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
var scanner = new DiskScanner();
var search = new ContentSearchService();
if (args.Length == 2 && args[0] == "--inspect-drive")
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    var measured = await scanner.ScanAsync(args[1], cancellationToken: timeout.Token);
    var resultJson = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.Now, root = measured.Root.FullPath, bytes = measured.Root.Size,
        measured.Summary.Files, measured.Summary.Directories, measured.Summary.Skipped, measured.Summary.Elapsed,
        measured.Summary.Mode, measured.Summary.AllocationIsEstimated, warningCount = measured.Summary.Warnings.Count,
        warnings = measured.Summary.Warnings }, new JsonSerializerOptions { WriteIndented = true });
    var resultDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts"));
    Directory.CreateDirectory(resultDirectory);
    File.WriteAllText(Path.Combine(resultDirectory, "drive-check.json"), resultJson);
    Console.WriteLine(resultJson);
    return 0;
}
var outcomes = new List<object>();
var failed = 0;
async Task Check(string name, Func<Task> action)
{
    var timer = Stopwatch.StartNew();
    try
    {
        await action();
        outcomes.Add(new { name, passed = true, milliseconds = timer.Elapsed.TotalMilliseconds });
        Console.WriteLine($"PASS {name} ({timer.Elapsed.TotalMilliseconds:F1} ms)");
    }
    catch (Exception error)
    {
        failed++;
        outcomes.Add(new { name, passed = false, error = error.ToString() });
        Console.Error.WriteLine($"FAIL {name}: {error}");
    }
}
void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
string Write(string relative, string text, Encoding? encoding = null)
{
    var path = Path.Combine(fixture, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false));
    return path;
}
ScanNode FileNode(string path) => new()
{
    Name = Path.GetFileName(path), FullPath = path, Size = new FileInfo(path).Length,
    Attributes = File.GetAttributes(path)
};
async Task AssertArgumentRejected(Func<Task> action, string message)
{
    try
    {
        await action();
        throw new InvalidOperationException(message);
    }
    catch (ArgumentException) { }
}

var smallRoot = Path.Combine(fixture, "scan");
Write("scan/a/one.txt", "abc");
Write("scan/a/한글.txt", "가나다라마바사");
Write("scan/b/two.bin", new string('x', 8193));
var systemFile = Write("scan/b/system.txt", "private-system-marker");
File.SetAttributes(systemFile, File.GetAttributes(systemFile) | FileAttributes.System);
Directory.CreateDirectory(Path.Combine(smallRoot, "empty"));

await Check("NTFS records: fixups, resident/nonresident sizes, sparse runs and corruption rejection", () =>
{
    ParserChecks.Run();
    return Task.CompletedTask;
});

await Check("scan totals, nested folders, empty folders, Unicode and system usage", async () =>
{
    var scan = await scanner.ScanAsync(smallRoot);
    long actual = Directory.EnumerateFiles(smallRoot, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
    Assert(scan.Files.Count == 4 && scan.Summary.Files == 4, "All four files must be included in disk analysis");
    Assert(scan.Root.Size == actual && scan.Root.FileCount == 4, "Root totals mismatch");
    Assert(scan.Root.Children.Any(node => node.Name == "empty" && node.IsDirectory && node.Size == 0), "Empty folder missing");
    Assert(scan.Files.All(node => node.Parent != null && File.Exists(node.FullPath)), "Parent/path reconstruction failed");
    Assert(scan.Root.Children.Single(node => node.Name == "a").Size == 3 + Encoding.UTF8.GetByteCount("가나다라마바사"), "Nested total mismatch");
    Assert(scan.Summary.Elapsed >= TimeSpan.Zero && !string.IsNullOrWhiteSpace(scan.Summary.Mode), "Scan metadata missing");
});

await Check("scan warnings group repeated outcomes and retain exact counts with examples", () =>
{
    var warnings = new FileSystemScanner.WarningCollector(["MFT fallback detail"]);
    Parallel.For(0, 250, index => warnings.Add($"distinct failure {index}"));
    Parallel.For(0, 78, index => warnings.AddSkipped($@"C:\protected\{index}", new UnauthorizedAccessException("denied")));
    Parallel.For(0, 12, index => warnings.AddSkipped($@"C:\damaged\{index}", new IOException("device error")));
    Parallel.For(0, 45, index => warnings.AddReparsePoint($@"C:\links\{index}"));
    var snapshot = warnings.Snapshot();
    Assert(snapshot.Count == 106, "Warning limit or grouped warning count changed unexpectedly");
    Assert(snapshot.Count(item => item.StartsWith("Skipped 78 directories because access was denied.")) == 1,
        "Access-denied failures were not grouped with their exact count");
    Assert(snapshot.Count(item => item.StartsWith("Skipped 12 directories because of file-system I/O errors.")) == 1,
        "I/O failures were not grouped with their exact count");
    Assert(snapshot.Count(item => item.StartsWith("Did not traverse 45 reparse-point directories")) == 1,
        "Reparse-point advisories were not grouped with their exact count");
    Assert(snapshot.Single(item => item.StartsWith("Skipped 78 directories because access was denied."))
        .Contains("(+75 more).", StringComparison.Ordinal),
        "Grouped warning did not bound its path examples");
    Assert(snapshot.Contains("151 additional warnings were omitted."),
        "Concurrent general-warning cap did not retain the exact omitted count");
    return Task.CompletedTask;
});

await Check("empty root and pre-cancelled scan", async () =>
{
    var empty = Path.Combine(fixture, "empty-root");
    Directory.CreateDirectory(empty);
    var scan = await scanner.ScanAsync(empty);
    Assert(scan.Root.Size == 0 && scan.Files.Count == 0, "Empty root incorrect");
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    try { await scanner.ScanAsync(smallRoot, cancellationToken: cancelled.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { }
});

await Check("in-flight scan cancellation and clean subsequent rescan", async () =>
{
    using var cancellation = new CancellationTokenSource();
    var progress = new InlineProgress<ScanProgress>(_ => cancellation.Cancel());
    try { await scanner.ScanAsync(smallRoot, progress, cancellation.Token); throw new Exception("In-flight cancellation ignored"); }
    catch (OperationCanceledException) { }
    var subsequent = await scanner.ScanAsync(smallRoot);
    Assert(subsequent.Files.Count == 4, "Cancelled scan leaked state into subsequent scan");
});

await Check("all search modes exclude system files and protected OS roots", async () =>
{
    var normal = Write("search/normal.txt", "private-system-marker");
    var nodes = new[] { FileNode(normal), FileNode(systemFile) };
    foreach (var mode in Enum.GetValues<SearchMode>())
    {
        var report = await search.SearchAsync(nodes, new SearchOptions(mode == SearchMode.FileName ? ".txt" : "private-system-marker", mode));
        Assert(report.Hits.Count == 1 && report.Hits[0].Node.FullPath == normal, $"System file escaped filter in {mode}");
    }
    foreach (var path in new[] { @"C:\Windows\notes.txt", @"C:\Windows.old\notes.txt", @"C:\Program Files\app\notes.txt",
        @"C:\ProgramData\notes.txt", @"C:\$Recycle.Bin\notes.txt", @"C:\System Volume Information\notes.txt", @"C:\pagefile.sys" })
        Assert(SearchPolicy.IsExcluded(new ScanNode { FullPath = path, Name = Path.GetFileName(path) }), $"Protected path accepted: {path}");
    Assert(!SearchPolicy.IsExcluded(new ScanNode { FullPath = @"C:\Users\me\Documents\Windows\notes.txt", Name = "notes.txt" }), "User Windows-named folder over-excluded");
    Assert(!SearchPolicy.IsExcluded(new ScanNode { FullPath = @"C:\WindowsProjects\notes.txt", Name = "notes.txt" }), "Path prefix boundary failed");
});

await Check("boolean query parsing, literal phrases, precedence and parentheses", async () =>
{
    var nodes = new[]
    {
        FileNode(Write("boolean/plain-phrase.txt", "여기에 코드 분석 결과가 있습니다")),
        FileNode(Write("boolean/separated-phrase.txt", "코드 상세 분석")),
        FileNode(Write("boolean/alpha-only.txt", "alpha")),
        FileNode(Write("boolean/beta-gamma.txt", "beta and then gamma")),
        FileNode(Write("boolean/beta-only.txt", "beta")),
        FileNode(Write("boolean/literal-or.txt", "left OR right")),
        FileNode(Write("boolean/separated-or.txt", "left then OR then right"))
    };

    var words = await search.SearchAsync(nodes, new SearchOptions("코드 분석", SearchMode.Contents));
    Assert(words.Hits.Select(hit => hit.Node.Name).Order().SequenceEqual(new[] { "plain-phrase.txt", "separated-phrase.txt" }),
        "Space-separated words must all match anywhere in the file");
    var phrase = await search.SearchAsync(nodes, new SearchOptions("\"코드 분석\"", SearchMode.Contents));
    Assert(phrase.Hits.Count == 1 && phrase.Hits[0].Node.Name == "plain-phrase.txt",
        "A quoted query must remain one literal phrase");

    var precedence = await search.SearchAsync(nodes, new SearchOptions("alpha OR beta AND gamma", SearchMode.Contents));
    Assert(precedence.Hits.Select(hit => hit.Node.Name).Order().SequenceEqual(new[] { "alpha-only.txt", "beta-gamma.txt" }),
        "AND must bind more tightly than OR");

    var parentheses = await search.SearchAsync(nodes, new SearchOptions("(alpha OR beta) AND gamma", SearchMode.Contents));
    Assert(parentheses.Hits.Count == 1 && parentheses.Hits[0].Node.Name == "beta-gamma.txt",
        "Parentheses did not override precedence");

    var quoted = await search.SearchAsync(nodes, new SearchOptions("\"left OR right\"", SearchMode.Contents));
    Assert(quoted.Hits.Count == 1 && quoted.Hits[0].Node.Name == "literal-or.txt",
        "A quoted operator must be searched as literal phrase text");

    var operatorCase = await search.SearchAsync(nodes, new SearchOptions("BeTa aNd GaMmA", SearchMode.Contents));
    Assert(operatorCase.Hits.Count == 1 && operatorCase.Hits[0].Node.Name == "beta-gamma.txt",
        "Operators or default term matching were not case-insensitive");
});

await Check("boolean terms span lines, stream buffers, filenames and content", async () =>
{
    string splitPath = Write("boolean-stream/split.txt",
        "first-term\n" + new string('x', 40 * 1024) + "\nsecond-term");
    string combinedPath = Write("boolean-stream/name-token-file.txt", "body-token appears here");
    var contentNodes = new[] { FileNode(splitPath), FileNode(combinedPath) };

    var split = await search.SearchAsync(contentNodes,
        new SearchOptions("first-term AND second-term", SearchMode.Contents));
    Assert(split.Hits.Count == 1 && split.Hits[0].Node.FullPath == splitPath,
        "AND terms on different lines or buffers were not accumulated for one file");

    var nameAndBody = await search.SearchAsync(contentNodes,
        new SearchOptions("name-token AND body-token", SearchMode.NameOrContents));
    Assert(nameAndBody.Hits.Count == 1 && nameAndBody.Hits[0].Node.FullPath == combinedPath &&
           nameAndBody.Hits[0].MatchKind == "Contents",
        "NameOrContents did not combine filename and body term matches");

    var virtualNames = new[]
    {
        new ScanNode { Name = "red-report.txt", FullPath = Path.Combine(fixture, "missing", "red-report.txt") },
        new ScanNode { Name = "blue-notes.txt", FullPath = Path.Combine(fixture, "missing", "blue-notes.txt") },
        new ScanNode { Name = "green.txt", FullPath = Path.Combine(fixture, "missing", "green.txt") }
    };
    var filename = await search.SearchAsync(virtualNames,
        new SearchOptions("red OR blue", SearchMode.FileName));
    Assert(filename.Hits.Count == 2 && filename.Skipped == 0,
        "FileName mode did not evaluate the full expression without content reads");
});

await Check("boolean terms span searchable Office XML parts", async () =>
{
    string path = Path.Combine(fixture, "boolean-office.docx");
    using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
    {
        using (var writer = new StreamWriter(archive.CreateEntry("word/document.xml").Open()))
            writer.Write("<document><p><r><t>document-term</t></r></p></document>");
        using (var writer = new StreamWriter(archive.CreateEntry("word/header1.xml").Open()))
            writer.Write("<header><p><r><t>header-term</t></r></p></header>");
    }

    var report = await search.SearchAsync([FileNode(path)],
        new SearchOptions("document-term AND header-term", SearchMode.Contents));
    Assert(report.Hits.Count == 1, "AND terms in different Office parts were not accumulated");

    string boundaryPath = Path.Combine(fixture, "boolean-office-boundaries.docx");
    using (var archive = ZipFile.Open(boundaryPath, ZipArchiveMode.Create))
    using (var writer = new StreamWriter(archive.CreateEntry("word/document.xml").Open()))
        writer.Write("<document><p><r><t>alpha</t></r></p><p><r><t>beta</t></r></p></document>");
    var falsePhrase = await search.SearchAsync([FileNode(boundaryPath)],
        new SearchOptions("alphabeta", SearchMode.Contents));
    Assert(falsePhrase.Hits.Count == 0,
        "Text in separate Office paragraphs was incorrectly concatenated into a literal match");

    string breakPath = Path.Combine(fixture, "boolean-office-break.docx");
    using (var archive = ZipFile.Open(breakPath, ZipArchiveMode.Create))
    using (var writer = new StreamWriter(archive.CreateEntry("word/document.xml").Open()))
        writer.Write("<document><p><r><t>line</t><br/><t>break</t><t>run</t></r></p></document>");
    var falseBreakPhrase = await search.SearchAsync([FileNode(breakPath)],
        new SearchOptions("linebreak", SearchMode.Contents));
    var contiguousRuns = await search.SearchAsync([FileNode(breakPath)],
        new SearchOptions("breakrun", SearchMode.Contents));
    Assert(falseBreakPhrase.Hits.Count == 0 && contiguousRuns.Hits.Count == 1,
        "Office explicit breaks and contiguous formatting runs were not distinguished");
});

await Check("malformed and excessive boolean queries are rejected in Korean", () =>
{
    string[] malformed =
    [
        "alpha AND", "OR alpha", "alpha AND OR beta", "(alpha OR beta", "alpha OR beta)",
        "()", "\"unterminated", "\"\"", "alpha NOT", "size:abc", "date:2026-13-45", "type:other", "ext:",
        new string('(', QueryExpression.MaximumNestingDepth + 1) + "alpha" +
            new string(')', QueryExpression.MaximumNestingDepth + 1)
    ];

    foreach (string query in malformed)
    {
        try
        {
            QueryExpression.Parse(query);
            throw new Exception($"Malformed query was accepted: {query}");
        }
        catch (ArgumentException error) when (error.ParamName == "query")
        {
            Assert(error.Message.Any(character => character is >= '\uac00' and <= '\ud7a3'),
                $"Malformed query did not return a Korean message: {query}");
        }
    }

    string tooManyTerms = string.Join(" OR ", Enumerable.Repeat("term", QueryExpression.MaximumTerms + 1));
    try
    {
        QueryExpression.Parse(tooManyTerms);
        throw new Exception("Term-count limit was ignored");
    }
    catch (ArgumentException error) when (error.ParamName == "query") { }

    return Task.CompletedTask;
});

await Check("UTF-8, UTF-16, UTF-32 text and case-sensitive content search", async () =>
{
    var nodes = new[]
    {
        FileNode(Write("encoding/utf8.txt", "before\n검색대상 NeedleCase\nafter")),
        FileNode(Write("encoding/utf16.txt", "검색대상 NeedleCase", Encoding.Unicode)),
        FileNode(Write("encoding/utf16be.txt", "검색대상 NeedleCase", Encoding.BigEndianUnicode)),
        FileNode(Write("encoding/utf32.txt", "검색대상 NeedleCase", Encoding.UTF32))
    };
    Assert((await search.SearchAsync(nodes, new SearchOptions("검색대상", SearchMode.Contents))).Hits.Count == 4, "Unicode encoding detection failed");
    Assert((await search.SearchAsync(nodes, new SearchOptions("needlecase", SearchMode.Contents))).Hits.Count == 4, "Case-insensitive matching failed");
    Assert((await search.SearchAsync(nodes, new SearchOptions("needlecase", SearchMode.Contents, true))).Hits.Count == 0, "Case-sensitive matching ignored");
});

await Check("streaming content matches across buffers and inside a large file", async () =>
{
    var paths = new List<ScanNode>();
    foreach (int boundary in new[] { 4096, 32768, 65536, 262144, 1048576 })
        paths.Add(FileNode(Write($"boundary/{boundary}.txt", new string('a', boundary - 4) + "unique-boundary-needle" + new string('b', 100))));
    var large = Write("boundary/large.log", "");
    await using (var stream = File.OpenWrite(large))
    {
        var buffer = Encoding.UTF8.GetBytes(new string('a', 1024 * 1024));
        for (int i = 0; i < 32; i++) await stream.WriteAsync(buffer);
        await stream.WriteAsync(Encoding.UTF8.GetBytes("unique-boundary-needle"));
    }
    paths.Add(FileNode(large));
    var report = await search.SearchAsync(paths, new SearchOptions("unique-boundary-needle", SearchMode.Contents));
    Assert(report.Hits.Count == paths.Count, "Boundary or large-file match missed");
    Assert(report.Hits.All(hit => hit.Preview.Length < 2000), "Preview must remain bounded");
});

await Check("many absent terms keep streaming allocations bounded", async () =>
{
    string path = Write("boundary/many-terms.txt", new string('q', 4 * 1024 * 1024));
    string query = string.Join(" OR ", Enumerable.Range(0, QueryExpression.MaximumTerms)
        .Select(index => $"absent-token-{index:D2}"));
    GC.Collect();
    GC.WaitForPendingFinalizers();
    long before = GC.GetTotalAllocatedBytes(precise: true);
    var report = await search.SearchAsync([FileNode(path)], new SearchOptions(query, SearchMode.Contents));
    long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
    Assert(report.Hits.Count == 0, "Absent-term allocation fixture unexpectedly matched");
    Assert(allocated < 128L * 1024 * 1024,
        $"Streaming matcher allocated {allocated:N0} bytes for a 4 MiB file and 64 absent terms");
});

await Check("Office XML content, split formatting runs and malformed document isolation", async () =>
{
    ScanNode Office(string name, string entryName, string xml)
    {
        var path = Path.Combine(fixture, name);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry(entryName).Open())) writer.Write(xml);
        return FileNode(path);
    }
    var documents = new[]
    {
        Office("sample.docx", "word/document.xml", "<document><p><r><t>Office</t></r><r><t>Needle</t></r></p></document>"),
        Office("sample.xlsx", "xl/sharedStrings.xml", "<sst><si><t>OfficeNeedle</t></si></sst>"),
        Office("sample.pptx", "ppt/slides/slide1.xml", "<slide><t>OfficeNeedle</t></slide>"),
        Office("unsafe.docx", "word/document.xml", "<!DOCTYPE d [<!ENTITY e SYSTEM 'file:///C:/Windows/win.ini'>]><d>&e;</d>"),
        FileNode(Write("broken.docx", "not a zip archive"))
    };
    var report = await search.SearchAsync(documents, new SearchOptions("OfficeNeedle", SearchMode.Contents));
    Assert(report.Hits.Count == 3 && report.Skipped >= 2, "Office matches or invalid document isolation failed");
});

await Check("binary files, reparse/cloud placeholders and ancestor attributes are excluded", async () =>
{
    var binary = Path.Combine(fixture, "binary.bin");
    File.WriteAllBytes(binary, [0, 1, 0, 2, 0, 3, 65, 66, 67]);
    Assert((await search.SearchAsync([FileNode(binary)], new SearchOptions("ABC", SearchMode.Contents))).Hits.Count == 0, "Binary content treated as text");
    foreach (var attributes in new[] { FileAttributes.System, FileAttributes.ReparsePoint, FileAttributes.Offline, (FileAttributes)0x00400000, (FileAttributes)0x00040000 })
        Assert(SearchPolicy.IsExcluded(new ScanNode { Name = "a.txt", FullPath = @"C:\Users\me\a.txt", Attributes = attributes }), $"Unsafe content attribute allowed: {attributes}");
    var parent = new ScanNode { FullPath = @"C:\private", IsDirectory = true, Attributes = FileAttributes.System };
    Assert(SearchPolicy.IsExcluded(new ScanNode { FullPath = @"C:\private\a.txt", Name = "a.txt", Parent = parent }), "Ancestor system attribute ignored");
});

await Check("result limit, no-content filename search and search cancellation", async () =>
{
    var virtualNodes = Enumerable.Range(0, 60).Select(index => new ScanNode
    { Name = $"match-{index}.txt", FullPath = Path.Combine(fixture, "not-on-disk", $"match-{index}.txt") }).ToArray();
    var report = await search.SearchAsync(virtualNodes, new SearchOptions("match", SearchMode.FileName, MaxResults: 7));
    Assert(report.Hits.Count == 7 && report.LimitReached, "Result limit not enforced/reported");
    Assert((await search.SearchAsync(virtualNodes, new SearchOptions("match", SearchMode.NameOrContents))).Hits.Count == 60, "Name match should not need file content access");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try { await search.SearchAsync(virtualNodes, new SearchOptions("match"), cancellationToken: cancellation.Token); throw new Exception("Search cancellation ignored"); }
    catch (OperationCanceledException) { }
});

await Check("a whole drive whose root is Hidden/System stays searchable by name and content", async () =>
{
    var driveRoot = new ScanNode { Name = @"Z:\", FullPath = @"Z:\", IsDirectory = true, Attributes = FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System };
    var project = new ScanNode { Name = "projects", FullPath = @"Z:\projects", IsDirectory = true, Attributes = FileAttributes.Directory, Parent = driveRoot };
    var exe = new ScanNode { Name = "Receive.exe", FullPath = @"Z:\projects\Receive.exe", Parent = project, Attributes = FileAttributes.Archive };
    var recycle = new ScanNode { Name = "$Recycle.Bin", FullPath = @"Z:\$Recycle.Bin", IsDirectory = true, Attributes = FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System, Parent = driveRoot };
    var deleted = new ScanNode { Name = "$RReceive.exe", FullPath = @"Z:\$Recycle.Bin\$RReceive.exe", Parent = recycle };
    Assert(!SearchPolicy.IsExcluded(exe) && !SearchPolicy.IsHiddenFromNameSearch(exe), "Volume root attributes hid an ordinary file");
    Assert(SearchPolicy.IsHiddenFromNameSearch(deleted), "Recycle bin internals must stay out of name search");
    var report = await search.SearchAsync([driveRoot, project, exe, recycle, deleted], new SearchOptions("Receive", SearchMode.FileName));
    Assert(report.Hits.Count == 1 && report.Hits[0].Node == exe && report.Excluded == 2, "Whole-drive name search missed the file");
    var programFiles = new ScanNode { Name = "Program Files", FullPath = @"Z:\Program Files", IsDirectory = true, Parent = driveRoot };
    var installed = new ScanNode { Name = "Receive.exe", FullPath = @"Z:\Program Files\Receive\Receive.exe", Parent = programFiles };
    Assert(!SearchPolicy.IsHiddenFromNameSearch(installed) && SearchPolicy.IsExcluded(installed),
        "Program Files must be findable by name while its contents stay unread");
});

await Check("names rank exact matches first and include folders", async () =>
{
    var root = new ScanNode { Name = "root", FullPath = @"D:\work", IsDirectory = true };
    ScanNode Folder(string name, ScanNode parent) => new() { Name = name, FullPath = parent.FullPath + "\\" + name, IsDirectory = true, Parent = parent };
    ScanNode Item(string name, ScanNode parent) => new() { Name = name, FullPath = parent.FullPath + "\\" + name, Parent = parent };
    var receiveFolder = Folder("Receive", root);
    var bin = Folder("bin", receiveFolder);
    var release = Folder("Release", bin);
    var exeFolder = Folder("exe", receiveFolder);
    var buildCopy = Item("Receive.exe", release);
    var shipped = Item("Receive.exe", exeFolder);
    var notes = Item("received-notes.txt", root);
    var inside = Item("autoreceive.log", root);
    var other = Item("report.txt", root);
    ScanNode[] all = [root, receiveFolder, bin, release, exeFolder, buildCopy, shipped, notes, inside, other];

    var exact = await search.SearchAsync(all, new SearchOptions("receive", SearchMode.FileName));
    var order = exact.Hits.Select(hit => hit.Node).ToArray();
    Assert(order.Length == 5 && order[0] == receiveFolder && order[1] == shipped && order[2] == buildCopy &&
           order[3] == notes && order[4] == inside, "Name results are not ranked by match quality and location: " +
           string.Join(", ", order.Select(node => node.FullPath)));

    var wildcard = await search.SearchAsync(all, new SearchOptions("rec*ve", SearchMode.FileName));
    Assert(wildcard.Hits.Select(hit => hit.Node).ToHashSet().SetEquals(new[] { receiveFolder, shipped, buildCopy }), "Wildcard did not match whole names/stems");
    var star = await search.SearchAsync(all, new SearchOptions("*.exe", SearchMode.FileName));
    Assert(star.Hits.Count == 2, "*.exe wildcard failed");
});

await Check("conditions: ext, size, date, type, path, NOT, | and separate condition text", async () =>
{
    var root = new ScanNode { Name = "data", FullPath = @"D:\data", IsDirectory = true };
    DateTime now = DateTime.UtcNow;
    ScanNode Item(string name, long size, DateTime modified, bool folder = false, string folderName = "docs") => new()
    {
        Name = name, FullPath = $@"D:\data\{folderName}\{name}", Size = size, LastWriteTimeUtc = modified, IsDirectory = folder, Parent = root
    };
    var bigPdf = Item("plan-final.pdf", 30L * 1024 * 1024, now.AddDays(-2));
    var smallPdf = Item("plan-draft.pdf", 200 * 1024, now.AddDays(-40));
    var sheet = Item("plan.xlsx", 2L * 1024 * 1024, now.AddDays(-400), folderName: "archive");
    var folder = Item("plans", 0, now.AddDays(-1), folder: true);
    var empty = Item("empty.txt", 0, now);
    ScanNode[] all = [bigPdf, smallPdf, sheet, folder, empty];
    async Task<string[]> Names(string query, string? conditions = null) =>
        (await search.SearchAsync(all, new SearchOptions(query, SearchMode.FileName, Conditions: conditions))).Hits.Select(hit => hit.Node.Name).Order().ToArray();

    Assert((await Names("plan ext:pdf")).SequenceEqual(new[] { "plan-draft.pdf", "plan-final.pdf" }), "ext: failed");
    Assert((await Names("plan size:>10mb")).SequenceEqual(new[] { "plan-final.pdf" }), "size:> failed");
    Assert((await Names("plan 크기:1mb..10mb")).SequenceEqual(new[] { "plan.xlsx" }), "Korean size range failed");
    Assert((await Names("plan date:7d")).SequenceEqual(new[] { "plan-final.pdf", "plans" }), "date: within days failed");
    Assert((await Names("plan date:>1y")).SequenceEqual(new[] { "plan.xlsx" }), "date: older than failed");
    Assert((await Names("plan type:folder")).SequenceEqual(new[] { "plans" }), "type:folder failed");
    Assert((await Names("plan -draft ext:pdf")).SequenceEqual(new[] { "plan-final.pdf" }), "Leading - did not exclude");
    Assert((await Names("plan NOT pdf")).SequenceEqual(new[] { "plan.xlsx", "plans" }), "NOT did not exclude");
    Assert((await Names("final | xlsx")).SequenceEqual(new[] { "plan-final.pdf", "plan.xlsx" }), "| did not work as OR");
    Assert((await Names("plan path:archive")).SequenceEqual(new[] { "plan.xlsx" }), "path: failed");
    Assert((await Names("", "size:=0 type:file")).SequenceEqual(new[] { "empty.txt" }), "Conditions alone did not search");
    Assert((await Names("plan", "ext:pdf;xlsx size:<1mb")).SequenceEqual(new[] { "plan-draft.pdf" }), "Separate condition text was not combined with AND");
    Assert((await Names("plan OR empty", "type:file date:today")).SequenceEqual(new[] { "empty.txt" }), "Conditions did not wrap an OR query");
});

await Check("CP949 text, UTF-16 without BOM, HWPX and OpenDocument contents", async () =>
{
    var cp949 = Encoding.GetEncoding(949);
    var ansi = FileNode(Write("legacy/ansi.txt", "옛날 메모장 문서 안시본문검색", cp949));
    var utf16Path = Path.Combine(fixture, "legacy", "utf16-nobom.txt");
    File.WriteAllBytes(utf16Path, new UnicodeEncoding(false, false).GetBytes("utf16 without bom 안시본문검색 marker text"));
    var nodes = new List<ScanNode> { ansi, FileNode(utf16Path) };
    ScanNode Zip(string name, string entryName, string xml)
    {
        var path = Path.Combine(fixture, "legacy", name);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry(entryName).Open())) writer.Write(xml);
        return FileNode(path);
    }
    nodes.Add(Zip("문서.hwpx", "Contents/section0.xml", "<hs:sec xmlns:hs='h' xmlns:hp='p'><hp:p><hp:run><hp:t>한글 안시본문검색</hp:t></hp:run></hp:p></hs:sec>"));
    nodes.Add(Zip("문서.odt", "content.xml", "<office:document-content xmlns:office='o' xmlns:text='t'><text:p>안시본문검색 odt</text:p></office:document-content>"));
    nodes.Add(Zip("macro.docm", "word/document.xml", "<document><p><r><t>안시본문검색</t></r></p></document>"));
    var report = await search.SearchAsync(nodes, new SearchOptions("안시본문검색", SearchMode.Contents));
    Assert(report.Hits.Count == 5, $"Expected 5 encoded/zip document hits, got {report.Hits.Count}: " + string.Join(", ", report.Hits.Select(hit => hit.Node.Name)));
});

await Check("HWP 5, Word/Excel/PowerPoint 97-2003 compound documents", async () =>
{
    var paths = new List<string>
    {
        CompoundFixtures.Hwp(Path.Combine(fixture, "compound", "보고서.hwp"), "한글 본문 HwpNeedle 입니다", compress: false),
        CompoundFixtures.Hwp(Path.Combine(fixture, "compound", "압축.hwp"), "압축된 본문 HwpNeedle 내용", compress: true),
        CompoundFixtures.Doc(Path.Combine(fixture, "compound", "문서.doc"), "워드 본문 HwpNeedle", "ascii piece text"),
        CompoundFixtures.Xls(Path.Combine(fixture, "compound", "시트.xls"), "HwpNeedle cell", "엑셀 연속문자열 HwpNeedle"),
        CompoundFixtures.Ppt(Path.Combine(fixture, "compound", "발표.ppt"), "파워포인트 HwpNeedle", "bytes atom")
    };
    var nodes = paths.Select(path => FileNode(path)).ToArray();
    var report = await search.SearchAsync(nodes, new SearchOptions("HwpNeedle", SearchMode.Contents));
    Assert(report.Hits.Count == 5 && report.Skipped == 0, $"Compound documents: {report.Hits.Count} hits, {report.Skipped} skipped: " +
        string.Join(", ", report.Hits.Select(hit => hit.Node.Name)));
    Assert(report.Hits.First(hit => hit.Node.Name == "보고서.hwp").Preview.Contains("한글 본문"), "HWP preview text was not decoded");
    var split = await search.SearchAsync([nodes[3]], new SearchOptions("\"연속문자열 HwpNeedle\"", SearchMode.Contents));
    Assert(split.Hits.Count == 1, "An SST string split across CONTINUE records was not rejoined");
    var ascii = await search.SearchAsync([nodes[2]], new SearchOptions("\"ascii piece text\"", SearchMode.Contents));
    Assert(ascii.Hits.Count == 1, "Word 8-bit pieces were not decoded");
});

await Check("PDF text: simple fonts, Flate streams with ToUnicode CID fonts, encryption", async () =>
{
    var simple = Path.Combine(fixture, "pdf", "simple.pdf");
    var cid = Path.Combine(fixture, "pdf", "한글.pdf");
    var encrypted = Path.Combine(fixture, "pdf", "locked.pdf");
    Directory.CreateDirectory(Path.GetDirectoryName(simple)!);
    File.WriteAllBytes(simple, PdfFixtures.Simple("PdfNeedle simple text"));
    File.WriteAllBytes(cid, PdfFixtures.IdentityWithToUnicode());
    File.WriteAllBytes(encrypted, PdfFixtures.Simple("PdfNeedle locked", encrypted: true));
    var report = await search.SearchAsync([FileNode(simple), FileNode(encrypted)], new SearchOptions("PdfNeedle", SearchMode.Contents));
    Assert(report.Hits.Count == 1 && report.Hits[0].Node.FullPath == simple && report.Skipped == 1, "Simple/encrypted PDF handling failed");
    var korean = await search.SearchAsync([FileNode(cid)], new SearchOptions("보고서", SearchMode.Contents));
    Assert(korean.Hits.Count == 1, "Identity-H PDF text was not mapped through ToUnicode");
    var spaced = await search.SearchAsync([FileNode(cid)], new SearchOptions("\"보고서 요약\"", SearchMode.Contents));
    Assert(spaced.Hits.Count == 1, "TJ word spacing was not turned into a space");
    var pieces = Path.Combine(fixture, "pdf", "pieces.pdf");
    File.WriteAllBytes(pieces, PdfFixtures.SeparateTextObjects());
    var glued = await search.SearchAsync([FileNode(pieces)], new SearchOptions("\"2023년 12월호 next line\"", SearchMode.Contents));
    Assert(glued.Hits.Count == 1, "Text drawn as separate touching objects must read as one word, gaps and new lines as one space");
});

await Check("name hits stream first and content hits follow in NameOrContents mode", async () =>
{
    var named = Write("stream/StreamNeedle-name.txt", "nothing here");
    var body = Write("stream/other.txt", "the StreamNeedle is in the body");
    var batches = new List<IReadOnlyList<SearchHit>>();
    var report = await search.SearchAsync([FileNode(body), FileNode(named)], new SearchOptions("StreamNeedle"),
        hitProgress: new InlineProgress<IReadOnlyList<SearchHit>>(batch => { lock (batches) batches.Add(batch); }));
    Assert(report.Hits.Count == 2 && report.Hits[0].MatchKind == "Name" && report.Hits[1].MatchKind == "Contents",
        "Name hits must come before content hits");
    Assert(batches.Sum(batch => batch.Count) == 2 && batches[0][0].MatchKind == "Name", "Hits were not streamed in order");
});

await Check("duplicate finder hashes only eligible same-size candidates and freezes paths", async () =>
{
    string first = Write("duplicates/first-name.bin", "same duplicate payload");
    string second = Write("duplicates/other-name.bin", "same duplicate payload");
    string sameSizeDifferent = Write("duplicates/same-size.bin", "different payload.....");
    Assert(new FileInfo(first).Length == new FileInfo(sameSizeDifferent).Length,
        "Duplicate same-size fixture is not the intended length");
    string unique = Write("duplicates/unique.bin", "a size no other candidate has");
    string empty = Write("duplicates/empty.bin", "");
    string protectedFile = Write("duplicates/system.bin", "same duplicate payload");
    File.SetAttributes(protectedFile, File.GetAttributes(protectedFile) | FileAttributes.System);

    var firstNode = FileNode(first);
    firstNode.Parent = new ScanNode
    {
        FullPath = Path.GetPathRoot(first)!,
        Name = Path.GetPathRoot(first)!,
        IsDirectory = true,
        Attributes = FileAttributes.Directory | FileAttributes.System | FileAttributes.Hidden
    };
    IEnumerable<ScanNode> Nodes()
    {
        yield return firstNode;
        // The finder must have copied the first path before asking for the next item.
        firstNode.FullPath = Path.Combine(fixture, "mutated-after-snapshot.bin");
        firstNode.Size = long.MaxValue;
        yield return FileNode(second);
        yield return FileNode(second);
        yield return FileNode(sameSizeDifferent);
        yield return FileNode(unique);
        yield return FileNode(empty);
        yield return FileNode(protectedFile);
    }
    DuplicateScanResult result = await new DuplicateFinder().FindAsync(Nodes());

    Assert(result.Groups.Count == 1, "Exactly one duplicate group should be found");
    DuplicateGroup group = result.Groups[0];
    Assert(group.Files.Select(file => file.FullPath).Order(StringComparer.OrdinalIgnoreCase)
        .SequenceEqual(new[] { first, second }.Order(StringComparer.OrdinalIgnoreCase)),
        "Different names with equal content were not grouped or the frozen path changed");
    Assert(group.ReclaimableBytes == new FileInfo(first).Length, "Reclaimable byte count is wrong");
    Assert(group.Files.All(file => file.Size > 0 && file.ContentHash.Length == 64 && file.Identity.Contains(':')),
        "Duplicate snapshot metadata is incomplete");
    Assert(result.SkippedFiles >= 2, "Empty and protected files were not excluded");
});

await Check("duplicate finder excludes reparse points and hard links", async () =>
{
    string original = Write("duplicate-links/original.bin", "linked duplicate payload");
    string hardLink = Path.Combine(fixture, "duplicate-links", "hard-link.bin");
    bool hardLinkCreated = NativeCheckHelpers.TryCreateHardLink(hardLink, original);
    if (hardLinkCreated)
    {
        var result = await new DuplicateFinder().FindAsync([FileNode(original), FileNode(hardLink)]);
        Assert(result.Groups.Count == 0 && result.SkippedFiles == 2,
            "Files with multiple hard links must be excluded from duplicate deletion");
    }

    string target = Write("duplicate-links/target.bin", "reparse duplicate payload");
    string symbolic = Path.Combine(fixture, "duplicate-links", "symbolic.bin");
    try
    {
        File.CreateSymbolicLink(symbolic, target);
        var result = await new DuplicateFinder().FindAsync([FileNode(target), FileNode(symbolic)]);
        Assert(result.Groups.Count == 0 && result.SkippedFiles >= 1,
            "A reparse-point file escaped duplicate filtering");
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
    {
        // Symlink creation can be unavailable without Windows developer mode.
    }
});

await Check("duplicate deletion validates selection and uses only the recycler", async () =>
{
    string keeper = Write("duplicate-delete/keeper.bin", "delete guard payload");
    string selected = Write("duplicate-delete/selected.bin", "delete guard payload");
    DuplicateGroup group = (await new DuplicateFinder().FindAsync([FileNode(keeper), FileNode(selected)])).Groups.Single();
    var recycler = new FakeRecycler();
    var service = new DuplicateDeletionService(recycler);
    DuplicateDeleteResult result = await service.DeleteAsync([group], [selected]);

    Assert(result.DeletedPaths.SequenceEqual([selected]) && result.Failures.Count == 0,
        "A verified selected duplicate was not reported as recycled");
    Assert(recycler.Paths.SequenceEqual([selected]) && recycler.ValidationCalls == 1,
        "Deletion bypassed the recycler or its immediate validation callback");
    Assert(File.Exists(selected), "The Core service directly deleted a file instead of delegating to the recycler");

    await AssertArgumentRejected(() => service.DeleteAsync([group], [keeper, selected]),
        "Selecting every copy in a group was accepted");
    await AssertArgumentRejected(() => service.DeleteAsync([group], new List<string> { selected, selected }),
        "A duplicate selected path was accepted");
    await AssertArgumentRejected(() => service.DeleteAsync([group], [Path.Combine(fixture, "unknown.bin")]),
        "A path outside the duplicate results was accepted");
});

await Check("duplicate deletion rejects changed targets and missing verified keepers", async () =>
{
    string keeper = Write("duplicate-changes/keeper.bin", "0123456789ABCDEF");
    string selected = Write("duplicate-changes/selected.bin", "0123456789ABCDEF");
    DuplicateGroup group = (await new DuplicateFinder().FindAsync([FileNode(keeper), FileNode(selected)])).Groups.Single();

    File.WriteAllText(selected, "FEDCBA9876543210");
    File.SetLastWriteTimeUtc(selected, group.Files.Single(file => file.FullPath == selected).LastWriteTimeUtc);
    var changedRecycler = new FakeRecycler();
    DuplicateDeleteResult changed = await new DuplicateDeletionService(changedRecycler).DeleteAsync([group], [selected]);
    Assert(changed.DeletedPaths.Count == 0 && changed.Failures.ContainsKey(selected) && changedRecycler.Paths.Count == 0,
        "A same-size, same-timestamp content change passed final SHA verification");

    string keeper2 = Write("duplicate-keeper-failure/keeper.bin", "keeper verification payload");
    string selected2 = Write("duplicate-keeper-failure/selected.bin", "keeper verification payload");
    DuplicateGroup group2 = (await new DuplicateFinder().FindAsync([FileNode(keeper2), FileNode(selected2)])).Groups.Single();
    File.WriteAllText(keeper2, "changed keeper content!!!!!");
    var keeperRecycler = new FakeRecycler();
    DuplicateDeleteResult noKeeper = await new DuplicateDeletionService(keeperRecycler).DeleteAsync([group2], [selected2]);
    Assert(noKeeper.DeletedPaths.Count == 0 && noKeeper.Failures.ContainsKey(selected2) && keeperRecycler.Paths.Count == 0,
        "Deletion continued without a verified matching keeper");
});

await Check("duplicate operations honor cancellation and return partial deletion results", async () =>
{
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        try
        {
            await new DuplicateFinder().FindAsync([], cancellationToken: cancelled.Token);
            throw new Exception("Pre-cancelled duplicate scan was accepted");
        }
        catch (OperationCanceledException) { }
    }

    string inFlight1 = Write("duplicate-cancel/inflight-1.bin", "in-flight duplicate payload");
    string inFlight2 = Write("duplicate-cancel/inflight-2.bin", "in-flight duplicate payload");
    using (var inFlightCancellation = new CancellationTokenSource())
    {
        var cancelProgress = new InlineProgress<DuplicateProgress>(_ => inFlightCancellation.Cancel());
        try
        {
            await new DuplicateFinder().FindAsync(
                [FileNode(inFlight1), FileNode(inFlight2)], cancelProgress, inFlightCancellation.Token);
            throw new Exception("In-flight duplicate scan cancellation was ignored");
        }
        catch (OperationCanceledException) { }
    }

    string keeper = Write("duplicate-cancel/keeper.bin", "cancellation duplicate payload");
    string selected1 = Write("duplicate-cancel/selected-1.bin", "cancellation duplicate payload");
    string selected2 = Write("duplicate-cancel/selected-2.bin", "cancellation duplicate payload");
    DuplicateGroup group = (await new DuplicateFinder().FindAsync(
        [FileNode(keeper), FileNode(selected1), FileNode(selected2)])).Groups.Single();
    using var cancellation = new CancellationTokenSource();
    var recycler = new FakeRecycler(() => cancellation.Cancel());
    DuplicateDeleteResult partial = await new DuplicateDeletionService(recycler).DeleteAsync(
        [group], [selected1, selected2], cancellationToken: cancellation.Token);
    Assert(partial.DeletedPaths.Count == 1 && recycler.Paths.Count == 1,
        "Cancellation lost the successful partial deletion result or processed another file");
});

await Check("duplicate deletion preserves prior success when cancelled during the next keeper hash", async () =>
{
    string keeper1 = Write("duplicate-keeper-cancel/group-1-keeper.bin", "first group duplicate payload");
    string selected1 = Write("duplicate-keeper-cancel/group-1-selected.bin", "first group duplicate payload");
    string keeper2 = Write("duplicate-keeper-cancel/group-2-keeper.bin", "second group different duplicate payload");
    string selected2 = Write("duplicate-keeper-cancel/group-2-selected.bin", "second group different duplicate payload");
    DuplicateGroup group1 = (await new DuplicateFinder().FindAsync([FileNode(keeper1), FileNode(selected1)])).Groups.Single();
    DuplicateGroup group2 = (await new DuplicateFinder().FindAsync([FileNode(keeper2), FileNode(selected2)])).Groups.Single();

    using var cancellation = new CancellationTokenSource();
    var progress = new InlineProgress<DuplicateProgress>(update =>
    {
        if (update.Stage == "보관본 검증" &&
            update.CurrentPath.Equals(keeper2, StringComparison.OrdinalIgnoreCase))
            cancellation.Cancel();
    });
    var recycler = new FakeRecycler();
    DuplicateDeleteResult partial = await new DuplicateDeletionService(recycler).DeleteAsync(
        [group1, group2], [selected1, selected2], progress, cancellation.Token);
    Assert(partial.DeletedPaths.SequenceEqual([selected1]) && recycler.Paths.SequenceEqual([selected1]),
        "Keeper-hash cancellation discarded prior success or recycled the second group");
});

await Check("duplicate deletion releases a failed keeper and falls back to another copy", async () =>
{
    string badKeeper = Write("duplicate-keeper-fallback/a-bad.bin", "fallback duplicate payload");
    string goodKeeper = Write("duplicate-keeper-fallback/b-good.bin", "fallback duplicate payload");
    string selected = Write("duplicate-keeper-fallback/c-selected.bin", "fallback duplicate payload");
    DuplicateGroup group = (await new DuplicateFinder().FindAsync(
        [FileNode(badKeeper), FileNode(goodKeeper), FileNode(selected)])).Groups.Single();
    File.WriteAllText(badKeeper, "changed fallback payload!");

    var recycler = new FakeRecycler();
    DuplicateDeleteResult result = await new DuplicateDeletionService(recycler).DeleteAsync([group], [selected]);
    Assert(result.DeletedPaths.SequenceEqual([selected]) && result.Failures.Count == 0,
        "A valid fallback keeper was not used after the first keeper changed");
    using FileStream exclusiveWrite = File.Open(badKeeper, FileMode.Open, FileAccess.Write, FileShare.None);
    Assert(exclusiveWrite.CanWrite, "The rejected keeper handle remained open");
});

if (args.Contains("--benchmark"))
{
    await Check("100,000-file disk scan and repeated filename search benchmark", async () =>
    {
        var bench = Path.Combine(fixture, "benchmark");
        var payload = Encoding.UTF8.GetBytes("DiskScan benchmark content needle 0123456789\n");
        await Parallel.ForEachAsync(Enumerable.Range(0, 100), new ParallelOptions { MaxDegreeOfParallelism = 8 }, (group, _) =>
        {
            var directory = Path.Combine(bench, group.ToString("D3"));
            Directory.CreateDirectory(directory);
            for (int file = 0; file < 1000; file++) File.WriteAllBytes(Path.Combine(directory, $"sample-{file:D4}.txt"), payload);
            return ValueTask.CompletedTask;
        });
        var timer = Stopwatch.StartNew();
        var scan = await scanner.ScanAsync(bench);
        var scanMs = timer.Elapsed.TotalMilliseconds;
        Assert(scan.Files.Count == 100000 && scan.Root.Size == (long)payload.Length * 100000, "Benchmark count/size mismatch");
        timer.Restart();
        var report = await search.SearchAsync(scan.Files, new SearchOptions("sample-0999", SearchMode.FileName));
        var searchMs = timer.Elapsed.TotalMilliseconds;
        Assert(report.Hits.Count == 100, "Benchmark name results incorrect");
        timer.Restart();
        var contents = await search.SearchAsync(scan.Files.Take(10000), new SearchOptions("benchmark content", SearchMode.Contents, MaxResults: 20000));
        var contentMs = timer.Elapsed.TotalMilliseconds;
        Assert(contents.Hits.Count == 10000, "Benchmark content results incorrect");
        outcomes.Add(new { benchmark = true, files = 100000, bytes = scan.Root.Size, scanMilliseconds = scanMs,
            nameSearchMilliseconds = searchMs, contentSearchFiles = 10000, contentSearchMilliseconds = contentMs,
            scanMode = scan.Summary.Mode, peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 });
        Console.WriteLine($"BENCHMARK files=100000 mode={scan.Summary.Mode} scan={scanMs:F1}ms name-search={searchMs:F1}ms content-search(10000)={contentMs:F1}ms");
    });
}

var artifactRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts"));
Directory.CreateDirectory(artifactRoot);
File.WriteAllText(Path.Combine(artifactRoot, args.Contains("--benchmark") ? "benchmark-checks.json" : "core-checks.json"), JsonSerializer.Serialize(new
{ timestamp = DateTimeOffset.Now, fixture, failed, results = outcomes }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Checks: {outcomes.Count} records, {failed} failed. Fixtures: {fixture}");
return failed == 0 ? 0 : 1;

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

internal sealed class FakeRecycler(Action? afterRecycle = null) : IFileRecycler
{
    internal List<string> Paths { get; } = [];
    internal int ValidationCalls { get; private set; }

    public Task RecycleAsync(string path, Action validateImmediatelyBeforeRecycle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        validateImmediatelyBeforeRecycle();
        ValidationCalls++;
        Paths.Add(path);
        afterRecycle?.Invoke();
        return Task.CompletedTask;
    }
}

internal static class NativeCheckHelpers
{
    internal static bool TryCreateHardLink(string linkPath, string existingPath) =>
        CreateHardLink(linkPath, existingPath, IntPtr.Zero);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}

internal static class CompoundFixtures
{
    // A minimal OLE compound file (v3, 512-byte sectors) with a mini stream for small
    // streams, laid out by the [MS-CFB] rules so the reader is checked on spec input.
    internal static string Write(string path, IReadOnlyList<(string Storage, string Name, byte[] Data)> streams)
    {
        const int sector = 512, mini = 64, cutoff = 4096;
        const uint end = 0xFFFFFFFE, free = 0xFFFFFFFF, fatMark = 0xFFFFFFFD, none = 0xFFFFFFFF;
        var storages = streams.Select(item => item.Storage).Where(name => name.Length > 0).Distinct().ToList();
        var names = new List<(string Name, byte Type, int Parent)> { ("Root Entry", 5, -1) };
        foreach (var storage in storages) names.Add((storage, 1, 0));
        foreach (var stream in streams) names.Add((stream.Name, 2, stream.Storage.Length == 0 ? 0 : 1 + storages.IndexOf(stream.Storage)));

        var miniStream = new MemoryStream();
        var miniFat = new List<uint>();
        var streamStart = new uint[streams.Count];
        var regular = new List<(int Index, byte[] Data)>();
        for (int index = 0; index < streams.Count; index++)
        {
            var data = streams[index].Data;
            if (data.Length < cutoff)
            {
                int first = miniFat.Count;
                int count = Math.Max(1, (data.Length + mini - 1) / mini);
                for (int i = 0; i < count; i++) miniFat.Add(i == count - 1 ? end : (uint)(first + i + 1));
                miniStream.Write(data);
                miniStream.Write(new byte[count * mini - data.Length]);
                streamStart[index] = (uint)first;
            }
            else regular.Add((index, data));
        }
        int Sectors(long bytes) => (int)((bytes + sector - 1) / sector);
        int directorySectors = Sectors(names.Count * 128L);
        int miniFatSectors = miniFat.Count == 0 ? 0 : Sectors(miniFat.Count * 4L);
        int miniStreamSectors = Sectors(miniStream.Length);
        int dataSectors = regular.Sum(item => Sectors(item.Data.Length));
        int fatSectors = 1;
        while (fatSectors * 128 < fatSectors + directorySectors + miniFatSectors + miniStreamSectors + dataSectors) fatSectors++;
        int total = fatSectors + directorySectors + miniFatSectors + miniStreamSectors + dataSectors;
        var fat = Enumerable.Repeat(free, fatSectors * 128).ToArray();
        int next = 0;
        for (int i = 0; i < fatSectors; i++) fat[next++] = fatMark;
        uint Chain(int count)
        {
            if (count == 0) return end;
            int first = next;
            for (int i = 0; i < count; i++, next++) fat[next] = i == count - 1 ? end : (uint)(next + 1);
            return (uint)first;
        }
        uint directoryStart = Chain(directorySectors);
        uint miniFatStart = Chain(miniFatSectors);
        uint miniStreamStart = Chain(miniStreamSectors);
        foreach (var (index, data) in regular) streamStart[index] = Chain(Sectors(data.Length));

        var file = new byte[(total + 1) * sector];
        var header = file.AsSpan(0, sector);
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(header);
        BitConverter.TryWriteBytes(header[0x18..], (ushort)0x3E);
        BitConverter.TryWriteBytes(header[0x1A..], (ushort)3);
        BitConverter.TryWriteBytes(header[0x1C..], (ushort)0xFFFE);
        BitConverter.TryWriteBytes(header[0x1E..], (ushort)9);
        BitConverter.TryWriteBytes(header[0x20..], (ushort)6);
        BitConverter.TryWriteBytes(header[0x2C..], (uint)fatSectors);
        BitConverter.TryWriteBytes(header[0x30..], directoryStart);
        BitConverter.TryWriteBytes(header[0x38..], (uint)cutoff);
        BitConverter.TryWriteBytes(header[0x3C..], miniFatStart);
        BitConverter.TryWriteBytes(header[0x40..], (uint)miniFatSectors);
        BitConverter.TryWriteBytes(header[0x44..], end);
        for (int i = 0; i < 109; i++) BitConverter.TryWriteBytes(header[(0x4C + i * 4)..], i < fatSectors ? (uint)i : free);
        Span<byte> Sector(uint number) => file.AsSpan((int)(number + 1) * sector, sector);
        for (int i = 0; i < fatSectors; i++)
            for (int j = 0; j < 128; j++) BitConverter.TryWriteBytes(Sector((uint)i)[(j * 4)..], fat[i * 128 + j]);

        void WriteChain(uint start, byte[] data)
        {
            uint current = start;
            for (int offset = 0; offset < data.Length; offset += sector, current = fat[current])
                data.AsSpan(offset, Math.Min(sector, data.Length - offset)).CopyTo(Sector(current));
        }
        var miniFatBytes = new byte[miniFatSectors * sector];
        for (int i = 0; i < miniFatSectors * 128; i++) BitConverter.TryWriteBytes(miniFatBytes.AsSpan(i * 4), i < miniFat.Count ? miniFat[i] : free);
        if (miniFatSectors > 0) WriteChain(miniFatStart, miniFatBytes);
        if (miniStreamSectors > 0) WriteChain(miniStreamStart, miniStream.ToArray());
        foreach (var (index, data) in regular) WriteChain(streamStart[index], data);

        var directory = new byte[directorySectors * sector];
        for (int i = names.Count; i < directorySectors * 4; i++)
        {
            var empty = directory.AsSpan(i * 128, 128);
            BitConverter.TryWriteBytes(empty[0x44..], none); BitConverter.TryWriteBytes(empty[0x48..], none); BitConverter.TryWriteBytes(empty[0x4C..], none);
        }
        for (int i = 0; i < names.Count; i++)
        {
            var entry = directory.AsSpan(i * 128, 128);
            var nameBytes = Encoding.Unicode.GetBytes(names[i].Name);
            nameBytes.CopyTo(entry);
            BitConverter.TryWriteBytes(entry[0x40..], (ushort)(nameBytes.Length + 2));
            entry[0x42] = names[i].Type;
            entry[0x43] = 1;
            // Children of one storage form a right-linked list; the parent points at the first.
            var siblings = Enumerable.Range(1, names.Count - 1).Where(j => names[j].Parent == names[i].Parent).ToList();
            int position = i == 0 ? -1 : siblings.IndexOf(i);
            BitConverter.TryWriteBytes(entry[0x44..], none);
            BitConverter.TryWriteBytes(entry[0x48..], position >= 0 && position + 1 < siblings.Count ? (uint)siblings[position + 1] : none);
            var children = Enumerable.Range(1, names.Count - 1).Where(j => names[j].Parent == i).ToList();
            BitConverter.TryWriteBytes(entry[0x4C..], children.Count > 0 ? (uint)children[0] : none);
            if (i == 0)
            {
                BitConverter.TryWriteBytes(entry[0x74..], miniStreamSectors > 0 ? miniStreamStart : end);
                BitConverter.TryWriteBytes(entry[0x78..], (uint)miniStream.Length);
            }
            else if (names[i].Type == 2)
            {
                int stream = i - 1 - storages.Count;
                BitConverter.TryWriteBytes(entry[0x74..], streamStart[stream]);
                BitConverter.TryWriteBytes(entry[0x78..], (uint)streams[stream].Data.Length);
            }
        }
        WriteChain(directoryStart, directory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, file);
        return path;
    }

    internal static string Hwp(string path, string paragraph, bool compress)
    {
        var header = new byte[256];
        Encoding.ASCII.GetBytes("HWP Document File").CopyTo(header, 0);
        BitConverter.TryWriteBytes(header.AsSpan(32), 0x05010000u);
        BitConverter.TryWriteBytes(header.AsSpan(36), compress ? 1u : 0u);
        var records = new MemoryStream();
        void Record(int tag, byte[] data)
        {
            records.Write(BitConverter.GetBytes((uint)(tag | (data.Length << 20))));
            records.Write(data);
        }
        Record(66, new byte[22]);
        // An extended control (8 code units) before the text, then the paragraph end.
        var text = new List<char> { (char)11, 'x', 'x', 'x', 'x', 'x', 'x', (char)11 };
        text.AddRange(paragraph);
        text.Add('\r');
        Record(67, Encoding.Unicode.GetBytes(text.ToArray()));
        byte[] section = records.ToArray();
        if (compress)
        {
            var packed = new MemoryStream();
            using (var deflate = new DeflateStream(packed, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(section);
            section = packed.ToArray();
        }
        return Write(path, [("", "FileHeader", header), ("BodyText", "Section0", section)]);
    }

    internal static string Doc(string path, string unicodeText, string asciiText)
    {
        var word = new byte[4096];
        BitConverter.TryWriteBytes(word.AsSpan(0), (ushort)0xA5EC);
        BitConverter.TryWriteBytes(word.AsSpan(2), (ushort)0x00C1);
        BitConverter.TryWriteBytes(word.AsSpan(0x0A), (ushort)0x0200);
        BitConverter.TryWriteBytes(word.AsSpan(32), (ushort)14);
        BitConverter.TryWriteBytes(word.AsSpan(62), (ushort)22);
        BitConverter.TryWriteBytes(word.AsSpan(152), (ushort)93);
        string first = unicodeText + "\r", second = asciiText + "\r";
        Encoding.Unicode.GetBytes(first).CopyTo(word, 1024);
        Encoding.ASCII.GetBytes(second).CopyTo(word, 2048);
        var plc = new MemoryStream();
        foreach (int cp in new[] { 0, first.Length, first.Length + second.Length }) plc.Write(BitConverter.GetBytes(cp));
        plc.Write(new byte[2]); plc.Write(BitConverter.GetBytes(1024u)); plc.Write(new byte[2]);
        plc.Write(new byte[2]); plc.Write(BitConverter.GetBytes((2048u * 2) | 0x40000000u)); plc.Write(new byte[2]);
        var table = new MemoryStream();
        table.WriteByte(0x02);
        table.Write(BitConverter.GetBytes((uint)plc.Length));
        table.Write(plc.ToArray());
        BitConverter.TryWriteBytes(word.AsSpan(154 + 33 * 8), 0u);
        BitConverter.TryWriteBytes(word.AsSpan(154 + 33 * 8 + 4), (uint)table.Length);
        return Write(path, [("", "WordDocument", word), ("", "1Table", table.ToArray())]);
    }

    internal static string Xls(string path, string ascii, string wide)
    {
        var book = new MemoryStream();
        void Record(int type, byte[] data) { book.Write(BitConverter.GetBytes((ushort)type)); book.Write(BitConverter.GetBytes((ushort)data.Length)); book.Write(data); }
        Record(0x0809, new byte[16]);
        var sst = new MemoryStream();
        sst.Write(BitConverter.GetBytes(2u)); sst.Write(BitConverter.GetBytes(2u));
        sst.Write(BitConverter.GetBytes((ushort)ascii.Length)); sst.WriteByte(0); sst.Write(Encoding.ASCII.GetBytes(ascii));
        sst.Write(BitConverter.GetBytes((ushort)wide.Length)); sst.WriteByte(1);
        const int split = 4;
        sst.Write(Encoding.Unicode.GetBytes(wide[..split]));
        Record(0x00FC, sst.ToArray());
        var rest = new MemoryStream();
        rest.WriteByte(1);
        rest.Write(Encoding.Unicode.GetBytes(wide[split..]));
        Record(0x003C, rest.ToArray());
        Record(0x000A, []);
        return Write(path, [("", "Workbook", book.ToArray())]);
    }

    internal static string Ppt(string path, string chars, string bytes)
    {
        var atoms = new MemoryStream();
        void Atom(int type, byte[] data) { atoms.Write(BitConverter.GetBytes((ushort)0)); atoms.Write(BitConverter.GetBytes((ushort)type)); atoms.Write(BitConverter.GetBytes((uint)data.Length)); atoms.Write(data); }
        Atom(0x0FA0, Encoding.Unicode.GetBytes(chars));
        Atom(0x0FA8, Encoding.ASCII.GetBytes(bytes));
        var document = new MemoryStream();
        document.Write(BitConverter.GetBytes((ushort)0x000F)); document.Write(BitConverter.GetBytes((ushort)0x03E8)); document.Write(BitConverter.GetBytes((uint)atoms.Length));
        document.Write(atoms.ToArray());
        return Write(path, [("", "PowerPoint Document", document.ToArray())]);
    }
}

internal static class PdfFixtures
{
    private static byte[] Assemble(IEnumerable<byte[]> objects, string trailer)
    {
        var output = new MemoryStream();
        output.Write(Encoding.ASCII.GetBytes("%PDF-1.5\n"));
        int number = 1;
        foreach (var body in objects)
        {
            output.Write(Encoding.ASCII.GetBytes($"{number++} 0 obj\n"));
            output.Write(body);
            output.Write(Encoding.ASCII.GetBytes("\nendobj\n"));
        }
        output.Write(Encoding.ASCII.GetBytes($"trailer\n{trailer}\n%%EOF\n"));
        return output.ToArray();
    }

    private static byte[] Text(string value) => Encoding.ASCII.GetBytes(value);

    private static byte[] Stream(string dictionary, byte[] data)
    {
        var output = new MemoryStream();
        output.Write(Text($"<< {dictionary} /Length {data.Length} >>\nstream\n"));
        output.Write(data);
        output.Write(Text("\nendstream"));
        return output.ToArray();
    }

    internal static byte[] Simple(string text, bool encrypted = false) => Assemble(
    [
        Text("<< /Type /Catalog /Pages 2 0 R >>"),
        Text("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        Text("<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>"),
        Text("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        Stream("", Text($"BT /F1 12 Tf 72 712 Td ({text}) Tj ET"))
    ], encrypted ? "<< /Root 1 0 R /Encrypt << /Filter /Standard /V 1 >> >>" : "<< /Root 1 0 R >>");

    // "2023" and "년" drawn as separate text objects that touch (Helvetica: 0.5 em per
    // character by default), then a real gap, then a new line.
    internal static byte[] SeparateTextObjects() => Assemble(
    [
        Text("<< /Type /Catalog /Pages 2 0 R >>"),
        Text("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        Text("<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 4 0 R /F2 5 0 R >> >> /Contents 6 0 R >>"),
        Text("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        Text("<< /Type /Font /Subtype /Type0 /BaseFont /Gothic /Encoding /Identity-H /ToUnicode 7 0 R /DescendantFonts [8 0 R] >>"),
        Stream("", Text("q 1 0 0 1 0 0 cm BT /F1 10 Tf 1 0 0 1 100 700 Tm (2023) Tj ET Q " +
                        "q 1 0 0 1 0 0 cm BT /F2 10 Tf 1 0 0 1 120 700.2 Tm <0001> Tj ET Q " +
                        "BT /F1 10 Tf 1 0 0 1 135 700 Tm (12) Tj ET BT /F2 10 Tf 145 700 Td <0002> Tj ET " +
                        "BT /F1 10 Tf 72 680 Td (next) Tj 2 0 Td ( line) Tj ET")),
        Stream("", Text("1 begincodespacerange <0000> <FFFF> endcodespacerange 2 beginbfchar <0001> <B144> <0002> <C6D4D638> endbfchar")),
        Text("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Gothic /DW 1000 >>")
    ], "<< /Root 1 0 R >>");

    internal static byte[] IdentityWithToUnicode()
    {
        byte[] Deflate(string value)
        {
            var packed = new MemoryStream();
            using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(Encoding.ASCII.GetBytes(value));
            return packed.ToArray();
        }
        const string cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n1 begincodespacerange <0000> <FFFF> endcodespacerange\n" +
            "3 beginbfchar <0001> <BCF4> <0002> <ACE0> <0003> <C11C> endbfchar\n1 beginbfrange <0010> <0011> [<C694> <C57D>] endbfrange\n" +
            "endcmap CMapName currentdict /CMap defineresource pop end end";
        // 보고서 split over a TJ kerning gap and a Tj, then a word gap, then 요약 from a bfrange.
        const string content = "BT /F1 11 Tf 1 0 0 1 72 700 Tm [<0001> -20 <0002>] TJ <0003> Tj [ -400 <00100011> ] TJ ET";
        return Assemble(
        [
            Text("<< /Type /Catalog /Pages 2 0 R >>"),
            Text("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Text("<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>"),
            Text("<< /Type /Font /Subtype /Type0 /BaseFont /Malgun /Encoding /Identity-H /ToUnicode 6 0 R >>"),
            Stream("/Filter /FlateDecode", Deflate(content)),
            Stream("/Filter /FlateDecode", Deflate(cmap))
        ], "<< /Root 1 0 R >>");
    }
}
