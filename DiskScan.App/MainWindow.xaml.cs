using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DiskScan.Core;
using Microsoft.Win32;

namespace DiskScan.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly DiskScanner _scanner = new();
    private readonly ContentSearchService _searchService = new();
    private readonly DuplicateFinder _duplicateFinder;
    private readonly DuplicateDeletionService _duplicateDeletionService;
    private readonly Func<int, long, bool>? _duplicateConfirmation;
    private readonly Stack<ScanNode> _navigation = new();
    private readonly WindowBounds _windowBounds;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _duplicateCancellation;
    private int _scanGeneration;
    private int _searchGeneration;
    private int _viewGeneration;
    private int _metricGeneration;
    private int _duplicateGeneration;
    private bool _duplicateBusy;
    private bool _duplicatePipelineActive;
    private bool _duplicateSelectionWarning;
    private bool _duplicateLayoutActive;
    private GridLength _savedContentRowHeight;
    private GridLength _savedMapRowHeight;
    private double _savedMapRowMinHeight;
    private GridLength _savedResultsColumnWidth;
    private GridLength _savedExtensionColumnWidth;
    private bool _useAllocated;
    private bool _settingMetric;
    private ScanNode? _currentViewNode;
    private ScanNode? _selectedNode;
    private IReadOnlyList<FileRow> _largeFiles = [];
    private IReadOnlyList<ExtensionRow> _extensions = [];
    private ObservableCollection<SearchResultRow> _searchResults = [];
    private DateTime _scanCompletedUtc;
    private long _lastImeSearchTicks;
    private const int MaxSearchResults = 10000;
    // A file list older than this is read again before searching, so new files are found.
    private static readonly TimeSpan ScanFreshFor = TimeSpan.FromMinutes(5);
    // File lists read for "모든 드라이브": kept while fresh so the next search starts at once,
    // and reads still running when a newer search replaces the old one, so it can reuse them.
    private readonly Dictionary<string, (ScanResult Scan, DateTime ListedUtc)> _driveLists = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DriveListRead> _driveListReads = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _driveListCancellation = new();
    private DispatcherTimer? _driveListExpiry;
    private IReadOnlyList<DuplicateFileRow> _duplicateRows = [];
    private IReadOnlyList<DuplicateGroup> _duplicateGroups = [];
    private IReadOnlyList<string> _duplicateFailures = [];
    private string _logicalSizeText = "—", _allocatedSizeText = "—", _fileCountText = "—", _elapsedText = "—", _scanModeText = "준비";

    public MainWindow() : this(new WindowsFileRecycler(), null) { }

    public MainWindow(IFileRecycler duplicateRecycler, Func<int, long, bool>? duplicateConfirmation = null)
    {
        ArgumentNullException.ThrowIfNull(duplicateRecycler);
        _duplicateFinder = new DuplicateFinder();
        _duplicateDeletionService = new DuplicateDeletionService(duplicateRecycler);
        _duplicateConfirmation = duplicateConfirmation;
        SearchModes = [new("파일 이름", SearchMode.FileName), new("파일 내용", SearchMode.Contents), new("이름 또는 내용", SearchMode.NameOrContents)];
        SearchScopes = [new("선택한 위치", false), new("모든 드라이브", true)];
        KindChoices =
        [
            new("전체", ""), new("폴더", "type:folder"),
            new("문서", "ext:pdf;hwp;hwpx;doc;docx;xls;xlsx;xlsm;ppt;pptx;txt;md;csv"),
            new("PDF", "ext:pdf"), new("한글", "ext:hwp;hwpx"), new("워드", "ext:doc;docx"), new("엑셀", "ext:xls;xlsx;xlsm;csv"),
            new("파워포인트", "ext:ppt;pptx"), new("이미지", "ext:jpg;jpeg;png;gif;bmp;webp;heic;tif;tiff"),
            new("동영상", "ext:mp4;mkv;avi;mov;wmv;webm"), new("음악", "ext:mp3;wav;flac;m4a;aac;ogg"),
            new("압축 파일", "ext:zip;7z;rar;tar;gz"), new("실행 파일", "ext:exe;msi")
        ];
        SizeChoices = [new("전체", ""), new("1MB 이상", "size:>=1mb"), new("10MB 이상", "size:>=10mb"), new("100MB 이상", "size:>=100mb"), new("1GB 이상", "size:>=1gb"), new("1MB 미만", "size:<1mb")];
        DateChoices = [new("전체", ""), new("오늘", "date:today"), new("7일 이내", "date:7d"), new("30일 이내", "date:30d"), new("1년 이내", "date:1y"), new("1년 넘음", "date:>1y")];
        MetricChoices = [new("논리 크기 기준", false), new("할당 크기 기준", true)];
        Resources.MergedDictionaries.Add(CreateAppResources());
        InitializeComponent();
        _windowBounds = WindowBounds.Attach(this);
        DataContext = this;
        LoadDrives();
        Loaded += MainWindow_Loaded;
        PreviewKeyDown += Window_PreviewKeyDown;
        Closing += MainWindow_Closing;
        Closing += (_, _) => { _scanCancellation?.Cancel(); _searchCancellation?.Cancel(); _driveListCancellation.Cancel(); _duplicateCancellation?.Cancel(); Interlocked.Increment(ref _duplicateGeneration); };
        StateChanged += (_, _) => MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    internal static ResourceDictionary CreateAppResources() => new()
    {
        Source = new Uri("/DiskScan;component/AppResources.xaml", UriKind.Relative)
    };

    public ObservableCollection<FolderRow> FolderRows { get; } = [];
    public IReadOnlyList<FileRow> LargeFiles { get => _largeFiles; private set => Set(ref _largeFiles, value); }
    public IReadOnlyList<ExtensionRow> Extensions { get => _extensions; private set => Set(ref _extensions, value); }
    public ObservableCollection<SearchResultRow> SearchResults { get => _searchResults; private set => Set(ref _searchResults, value); }
    public IReadOnlyList<DuplicateFileRow> DuplicateRows { get => _duplicateRows; private set => Set(ref _duplicateRows, value); }
    public IReadOnlyList<string> DuplicateFailures { get => _duplicateFailures; private set => Set(ref _duplicateFailures, value); }
    public IReadOnlyList<SearchModeChoice> SearchModes { get; }
    public IReadOnlyList<SearchScopeChoice> SearchScopes { get; }
    /// <summary>What "모든 드라이브" searches: the local fixed and removable drives (not network drives or discs).</summary>
    public Func<IReadOnlyList<string>> SearchDriveRoots { get; set; } = LocalDriveRoots;
    public IReadOnlyList<ConditionChoice> KindChoices { get; }
    public IReadOnlyList<ConditionChoice> SizeChoices { get; }
    public IReadOnlyList<ConditionChoice> DateChoices { get; }
    public IReadOnlyList<MetricBasisChoice> MetricChoices { get; }
    public ScanResult? CurrentScan { get; private set; }
    public string LogicalSizeText { get => _logicalSizeText; private set => Set(ref _logicalSizeText, value); }
    public string AllocatedSizeText { get => _allocatedSizeText; private set => Set(ref _allocatedSizeText, value); }
    public string FileCountText { get => _fileCountText; private set => Set(ref _fileCountText, value); }
    public string ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }
    public string ScanModeText { get => _scanModeText; private set => Set(ref _scanModeText, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task ScanPathAsync(string path)
    {
        if (_optimizationBusy) return;
        if (string.IsNullOrWhiteSpace(path)) { ShowMessage("분석할 드라이브나 폴더를 선택하세요.", false); return; }
        string fullPath;
        try { fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))); }
        catch (Exception ex) { ShowMessage($"올바른 경로가 아닙니다: {ex.Message}", false); return; }
        if (!Directory.Exists(fullPath)) { ShowMessage("선택한 폴더가 없거나 접근할 수 없습니다.", false); return; }

        var generation = Interlocked.Increment(ref _scanGeneration);
        Interlocked.Increment(ref _metricGeneration);
        CancelDuplicateWork(clearResults: true);
        var previousMetrics = (LogicalSizeText, AllocatedSizeText, FileCountText, ElapsedText, ScanModeText);
        _scanCancellation?.Cancel();
        _searchCancellation?.Cancel();
        _scanCancellation?.Dispose();
        _scanCancellation = new();
        var token = _scanCancellation.Token;

        SetScanBusy(true);
        HideMessage();
        PathBox.Text = fullPath;
        StatusText.Text = $"{fullPath} 분석 준비 중…";
        var acceptingProgress = true;
        var progress = new Progress<ScanProgress>(p =>
        {
            if (!acceptingProgress || generation != _scanGeneration) return;
            ScanModeText = NormalizeMode(p.Mode);
            StatusText.Text = $"파일 {p.Files:N0}개 · 폴더 {p.Directories:N0}개 · {Format.Bytes(p.Bytes)} · {CompactPath(p.CurrentPath)}";
            FileCountText = p.Files.ToString("N0");
            LogicalSizeText = Format.Bytes(p.Bytes);
            ElapsedText = Format.Duration(p.Elapsed);
        });

        try
        {
            var result = await _scanner.ScanAsync(fullPath, progress, token);
            if (generation != _scanGeneration || token.IsCancellationRequested) return;

            var useAllocated = !result.Summary.AllocationIsEstimated && result.Summary.Mode.Contains("MFT", StringComparison.OrdinalIgnoreCase);
            var prepared = await Task.Run(() => PrepareScan(result, useAllocated), token);
            if (generation != _scanGeneration || token.IsCancellationRequested) return;
            acceptingProgress = false;
            ApplyScan(result, prepared, useAllocated);
            ILuncher.AppKit.AppUsage.Count("scan", result.Summary.Files);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (generation == _scanGeneration)
            {
                (LogicalSizeText, AllocatedSizeText, FileCountText, ElapsedText, ScanModeText) = previousMetrics;
                StatusText.Text = CurrentScan is null ? "분석을 취소했습니다. 위치를 선택해 다시 시도하세요." : "분석을 취소했습니다. 이전 결과는 그대로 유지됩니다.";
            }
        }
        catch (Exception ex)
        {
            if (generation == _scanGeneration)
            {
                (LogicalSizeText, AllocatedSizeText, FileCountText, ElapsedText, ScanModeText) = previousMetrics;
                ShowMessage($"분석하지 못했습니다: {ex.Message}", false);
                StatusText.Text = CurrentScan is null ? "불러온 분석 결과가 없습니다." : "분석에 실패했습니다. 이전 결과는 그대로 유지됩니다.";
            }
        }
        finally
        {
            acceptingProgress = false;
            if (generation == _scanGeneration) SetScanBusy(false);
        }
    }

    public async Task SearchAsync(string query, SearchMode mode)
    {
        if (_optimizationBusy) return;
        MainTabs.SelectedIndex = 2;
        if (!ScanButton.IsEnabled) { SearchStatusText.Text = "디스크 분석이 끝난 뒤 검색할 수 있습니다."; return; }
        query = (query ?? "").Trim();
        var conditions = BuildConditions();
        if (query.Length == 0 && conditions.Length == 0) { SearchStatusText.Text = "검색어를 입력하거나 옵션에서 조건을 고르세요."; SearchBox.Focus(); return; }
        try { QueryExpression.Parse(query, conditions); }
        catch (ArgumentException error)
        {
            SearchStatusText.Text = error.Message;
            SearchStatusText.ToolTip = error.Message;
            SearchBox.Focus();
            return;
        }
        SearchStatusText.ClearValue(ToolTipProperty);
        if (SearchScopeBox.SelectedItem is SearchScopeChoice { AllDrives: true })
        {
            await SearchAllDrivesAsync(query, mode, conditions);
            return;
        }
        string requestedPath;
        try { requestedPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(PathBox.Text.Trim().Trim('"'))); }
        catch { SearchStatusText.Text = "위에서 검색할 드라이브나 폴더를 선택하세요."; return; }

        // A folder inside the current analysis is searched from it directly. A missing
        // or old file list is read again first, so files created since then are found.
        var scope = FindScope(CurrentScan, requestedPath);
        if (scope is null || DateTime.UtcNow - _scanCompletedUtc > ScanFreshFor)
        {
            var previousScan = CurrentScan;
            var scanPath = scope is null ? requestedPath : CurrentScan!.Root.FullPath;
            SearchStatusText.Text = scope is null ? "검색할 위치의 파일 목록을 준비합니다…" : "최신 파일 목록을 다시 읽습니다…";
            await ScanPathAsync(scanPath);
            PathBox.Text = requestedPath;
            scope = ReferenceEquals(CurrentScan, previousScan) ? null : FindScope(CurrentScan, requestedPath);
            if (scope is null)
            {
                SearchStatusText.Text = "파일 목록을 준비하지 못했거나 취소했습니다. 위치를 확인하고 다시 검색하세요.";
                return;
            }
            MainTabs.SelectedIndex = 2;
        }
        var scan = CurrentScan!;

        var generation = Interlocked.Increment(ref _searchGeneration);
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new();
        var token = _searchCancellation.Token;
        SearchButton.IsEnabled = false; SearchCancelButton.IsEnabled = true;
        FindDuplicatesButton.IsEnabled = false;
        BusyProgress.Visibility = Visibility.Visible;
        var conditionNote = conditions.Length > 0 ? "조건 적용 · " : "";
        SearchStatusText.Text = $"{conditionNote}{scope.FullPath} 검색 중…";

        // Hits arrive in batches from worker threads: names first, then contents as files are read.
        var rows = new ObservableCollection<SearchResultRow>();
        SearchResults = rows;
        var pending = new ConcurrentQueue<IReadOnlyList<SearchHit>>();
        void Drain()
        {
            if (generation != _searchGeneration) return;
            var batch = new List<SearchResultRow>();
            while (pending.TryDequeue(out var hits)) foreach (var hit in hits) batch.Add(new SearchResultRow(hit));
            if (batch.Count == 0) return;
            if (rows.Count == 0) { rows = new ObservableCollection<SearchResultRow>(batch); SearchResults = rows; }
            else foreach (var row in batch) rows.Add(row);
        }
        var drainTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => Drain(), Dispatcher);
        var acceptingProgress = true;
        var progress = new Progress<SearchProgress>(p =>
        {
            if (!acceptingProgress || generation != _searchGeneration) return;
            SearchStatusText.Text = p.Total > 0
                ? $"{conditionNote}결과 {p.Matches:N0}개 · 파일 내용 {p.Examined:N0} / {p.Total:N0} 확인 중…"
                : $"{conditionNote}결과 {p.Matches:N0}개";
        });

        try
        {
            drainTimer.Start();
            var nodes = await Task.Run(() => Descendants(scope), token);
            var options = new SearchOptions(query, mode, MatchCaseBox.IsChecked == true, MaxSearchResults, conditions);
            var report = await _searchService.SearchAsync(nodes, options, progress, token, new QueueProgress(pending));
            if (generation != _searchGeneration || token.IsCancellationRequested || !ReferenceEquals(scan, CurrentScan)) return;
            Drain();
            acceptingProgress = false;
            ILuncher.AppKit.AppUsage.Count("search");
            var parts = new List<string> { $"결과 {report.Hits.Count:N0}개" };
            if (report.LimitReached) parts.Add($"최대 {MaxSearchResults:N0}개까지 표시");
            if (report.ContentFiles > 0) parts.Add($"파일 내용 {report.ContentFiles:N0}개 확인");
            parts.Add(Format.Duration(report.Elapsed));
            if (DateTime.UtcNow - _scanCompletedUtc > TimeSpan.FromMinutes(1)) parts.Add($"{_scanCompletedUtc.ToLocalTime():HH:mm} 파일 목록 기준");
            SearchStatusText.Text = conditionNote + string.Join(" · ", parts);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (generation == _searchGeneration)
            {
                Drain();
                SearchStatusText.Text = $"검색을 취소했습니다 · 그때까지 찾은 결과 {SearchResults.Count:N0}개";
            }
        }
        catch (Exception ex)
        {
            if (generation == _searchGeneration) SearchStatusText.Text = $"검색하지 못했습니다: {ex.Message}";
        }
        finally
        {
            drainTimer.Stop();
            acceptingProgress = false;
            if (generation == _searchGeneration)
            {
                SearchButton.IsEnabled = ScanButton.IsEnabled; SearchCancelButton.IsEnabled = false;
                FindDuplicatesButton.IsEnabled = ScanButton.IsEnabled && !_duplicatePipelineActive;
                if (_scanCancellation is null || _scanCancellation.IsCancellationRequested || ScanButton.IsEnabled) BusyProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>
    /// Searches every drive. Each drive is searched as soon as its file list is ready (a fresh
    /// list is reused), so results from quick drives appear while slower ones are still read.
    /// The analysis on screen is left as it is.
    /// </summary>
    private async Task SearchAllDrivesAsync(string query, SearchMode mode, string conditions)
    {
        var drives = SearchDriveRoots().Select(FolderKey).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(root => new DriveSearch(root)).ToArray();
        if (drives.Length == 0) { SearchStatusText.Text = "검색할 드라이브가 없습니다."; return; }

        var generation = Interlocked.Increment(ref _searchGeneration);
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new();
        var token = _searchCancellation.Token;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        SearchButton.IsEnabled = false; SearchCancelButton.IsEnabled = true;
        FindDuplicatesButton.IsEnabled = false;
        BusyProgress.Visibility = Visibility.Visible;
        var conditionNote = conditions.Length > 0 ? "조건 적용 · " : "";
        var started = Stopwatch.StartNew();
        var options = new SearchOptions(query, mode, MatchCaseBox.IsChecked == true, MaxSearchResults, conditions);

        var rows = new ObservableCollection<SearchResultRow>();
        SearchResults = rows;
        var pending = new ConcurrentQueue<IReadOnlyList<SearchHit>>();
        var limitReached = false;
        var acceptingProgress = true;
        void Drain()
        {
            if (generation != _searchGeneration) return;
            var batch = new List<SearchResultRow>();
            while (!limitReached && pending.TryDequeue(out var hits))
                foreach (var hit in hits)
                {
                    if (rows.Count + batch.Count >= MaxSearchResults) { limitReached = true; break; }
                    batch.Add(new SearchResultRow(hit));
                }
            if (limitReached) stop.Cancel();
            if (batch.Count == 0) return;
            if (rows.Count == 0) { rows = new ObservableCollection<SearchResultRow>(batch); SearchResults = rows; }
            else foreach (var row in batch) rows.Add(row);
        }
        void ShowProgress()
        {
            if (!acceptingProgress || generation != _searchGeneration) return;
            var parts = new List<string> { $"결과 {rows.Count:N0}개", $"드라이브 {drives.Count(drive => drive.Done)}/{drives.Length} 완료" };
            var listing = drives.Where(drive => drive.Read is not null).ToArray();
            if (listing.Length > 0)
                parts.Add($"{string.Join(" ", listing.Select(drive => drive.Label))} 파일 목록 읽는 중 {listing.Sum(drive => drive.Read?.Files ?? 0):N0}개");
            var total = drives.Sum(drive => drive.Total);
            if (total > 0) parts.Add($"파일 내용 {drives.Sum(drive => drive.Examined):N0} / {total:N0} 확인 중");
            SearchStatusText.Text = conditionNote + string.Join(" · ", parts) + "…";
        }
        var drainTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => { Drain(); ShowProgress(); }, Dispatcher);

        async Task SearchDrive(DriveSearch drive)
        {
            try
            {
                var scan = await DriveListAsync(drive, stop.Token);
                var nodes = await Task.Run(() => Descendants(scan.Root), stop.Token);
                var progress = new Progress<SearchProgress>(p => { drive.Examined = p.Examined; drive.Total = p.Total; });
                var report = await _searchService.SearchAsync(nodes, options, progress, stop.Token, new QueueProgress(pending));
                drive.ContentFiles = report.ContentFiles;
                drive.LimitReached = report.LimitReached;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { drive.ContentFiles = drive.Examined; }
            catch (Exception error) { drive.Error = error.Message; }
            finally { drive.Done = true; }
        }

        try
        {
            drainTimer.Start();
            ShowProgress();
            await Task.WhenAll(drives.Select(SearchDrive));
            token.ThrowIfCancellationRequested();
            if (generation != _searchGeneration) return;
            Drain();
            acceptingProgress = false;
            ILuncher.AppKit.AppUsage.Count("search");
            var searched = drives.Where(drive => drive.Error is null).ToArray();
            var failed = drives.Where(drive => drive.Error is not null).ToArray();
            var parts = new List<string> { $"결과 {rows.Count:N0}개" };
            if (limitReached || drives.Any(drive => drive.LimitReached)) parts.Add($"최대 {MaxSearchResults:N0}개까지 표시");
            if (searched.Length > 0) parts.Add("드라이브 " + string.Join(" ", searched.Select(drive => drive.Label)));
            var contentFiles = drives.Sum(drive => drive.ContentFiles);
            if (contentFiles > 0) parts.Add($"파일 내용 {contentFiles:N0}개 확인");
            parts.Add(Format.Duration(started.Elapsed));
            if (failed.Length > 0) parts.Add($"{string.Join(" ", failed.Select(drive => drive.Label))} 읽지 못함");
            var listed = searched.Where(drive => drive.ListedUtc != default).Select(drive => drive.ListedUtc).ToArray();
            if (listed.Length > 0 && DateTime.UtcNow - listed.Min() > TimeSpan.FromMinutes(1)) parts.Add($"{listed.Min().ToLocalTime():HH:mm} 파일 목록 기준");
            SearchStatusText.Text = conditionNote + string.Join(" · ", parts);
            if (failed.Length > 0) SearchStatusText.ToolTip = string.Join(Environment.NewLine, failed.Select(drive => $"{drive.Label} {drive.Error}"));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (generation == _searchGeneration)
            {
                Drain();
                SearchStatusText.Text = $"검색을 취소했습니다 · 그때까지 찾은 결과 {SearchResults.Count:N0}개";
            }
        }
        catch (Exception ex)
        {
            if (generation == _searchGeneration) SearchStatusText.Text = $"검색하지 못했습니다: {ex.Message}";
        }
        finally
        {
            drainTimer.Stop();
            acceptingProgress = false;
            if (generation == _searchGeneration)
            {
                SearchButton.IsEnabled = ScanButton.IsEnabled; SearchCancelButton.IsEnabled = false;
                FindDuplicatesButton.IsEnabled = ScanButton.IsEnabled && !_duplicatePipelineActive;
                if (_scanCancellation is null || _scanCancellation.IsCancellationRequested || ScanButton.IsEnabled) BusyProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>
    /// A fresh file list for one drive: the analysis on screen, a kept list, or a new read.
    /// A new read goes on when a newer search replaces this one, so that search reuses it.
    /// </summary>
    private async Task<ScanResult> DriveListAsync(DriveSearch drive, CancellationToken token)
    {
        if (CurrentScan is { } current && SameFolder(current.Root.FullPath, drive.Root) && DateTime.UtcNow - _scanCompletedUtc <= ScanFreshFor)
        {
            drive.ListedUtc = _scanCompletedUtc;
            return current;
        }
        if (_driveLists.TryGetValue(drive.Root, out var kept) && DateTime.UtcNow - kept.ListedUtc <= ScanFreshFor)
        {
            drive.ListedUtc = kept.ListedUtc;
            return kept.Scan;
        }
        if (!_driveListReads.TryGetValue(drive.Root, out var read))
        {
            _driveLists.Remove(drive.Root); // let the old list go before reading a new one
            read = new DriveListRead();
            _driveListReads[drive.Root] = read;
            read.Task = ReadDriveListAsync(drive.Root, read, _driveListCancellation.Token);
        }
        drive.Read = read;
        try
        {
            var scan = await read.Task.WaitAsync(token);
            drive.ListedUtc = DateTime.UtcNow;
            return scan;
        }
        finally { drive.Read = null; }
    }

    private async Task<ScanResult> ReadDriveListAsync(string root, DriveListRead read, CancellationToken token)
    {
        try
        {
            var scan = await new DiskScanner().ScanAsync(root, new Progress<ScanProgress>(p => read.Files = p.Files), token);
            _driveLists[root] = (scan, DateTime.UtcNow);
            _driveListExpiry ??= new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => ExpireDriveLists(), Dispatcher);
            _driveListExpiry.Start();
            return scan;
        }
        finally
        {
            if (_driveListReads.TryGetValue(root, out var current) && ReferenceEquals(current, read)) _driveListReads.Remove(root);
        }
    }

    /// <summary>Old lists would be read again before the next search anyway, so their memory is returned.</summary>
    private void ExpireDriveLists()
    {
        foreach (var root in _driveLists.Where(pair => DateTime.UtcNow - pair.Value.ListedUtc > ScanFreshFor).Select(pair => pair.Key).ToArray())
            _driveLists.Remove(root);
        if (_driveLists.Count == 0) _driveListExpiry?.Stop();
    }

    private void CancelDriveListReads()
    {
        if (_driveListReads.Count == 0) return;
        _driveListCancellation.Cancel();
        _driveListCancellation = new();
    }

    private static IReadOnlyList<string> LocalDriveRoots()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(drive => drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady)
                .Select(drive => drive.RootDirectory.FullName).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private string IdleSearchStatus()
    {
        if (SearchScopeBox?.SelectedItem is not SearchScopeChoice { AllDrives: true }) return "선택한 위치의 이름과 내용을 검색합니다";
        var labels = SearchDriveRoots().Select(root => DriveLabel(FolderKey(root)));
        return $"모든 드라이브({string.Join(" ", labels)})의 이름과 내용을 검색합니다";
    }

    private void SearchScope_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SearchStatusText is null || SearchCancelButton is null || SearchCancelButton.IsEnabled) return;
        SearchStatusText.ClearValue(ToolTipProperty);
        SearchStatusText.Text = IdleSearchStatus();
    }

    private static string FolderKey(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool SameFolder(string first, string second) => string.Equals(FolderKey(first), FolderKey(second), StringComparison.OrdinalIgnoreCase);
    private static string DriveLabel(string root) => root.Length <= 3 ? root.TrimEnd('\\') : Path.GetFileName(root);

    private sealed class DriveListRead
    {
        public Task<ScanResult> Task { get; set; } = null!;
        public long Files;
    }

    /// <summary>One drive of a "모든 드라이브" search, updated on the UI thread.</summary>
    private sealed class DriveSearch(string root)
    {
        public string Root { get; } = root;
        public string Label { get; } = DriveLabel(root);
        public DriveListRead? Read { get; set; }
        public DateTime ListedUtc { get; set; }
        public long Examined { get; set; }
        public long Total { get; set; }
        public long ContentFiles { get; set; }
        public bool LimitReached { get; set; }
        public bool Done { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>The scanned folder for a path inside the current analysis, or null.</summary>
    private static ScanNode? FindScope(ScanResult? scan, string path)
    {
        if (scan is null) return null;
        var root = Path.TrimEndingDirectorySeparator(scan.Root.FullPath);
        var target = Path.TrimEndingDirectorySeparator(path);
        if (target.Equals(root, StringComparison.OrdinalIgnoreCase)) return scan.Root;
        var prefix = root.EndsWith('\\') ? root : root + "\\";
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var node = scan.Root;
        foreach (var segment in target[prefix.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node.Children.FirstOrDefault(child => child.IsDirectory && child.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (node is null) return null;
        }
        return node;
    }

    /// <summary>Every file and folder below the scope (not the scope itself).</summary>
    private static List<ScanNode> Descendants(ScanNode scope)
    {
        var result = new List<ScanNode>(Math.Max(16, (int)Math.Min(scope.FileCount * 2, 4_000_000)));
        var stack = new Stack<ScanNode>();
        stack.Push(scope);
        while (stack.Count > 0)
        {
            foreach (var child in stack.Pop().Children)
            {
                result.Add(child);
                if (child.IsDirectory) stack.Push(child);
            }
        }
        return result;
    }

    private string BuildConditions()
    {
        var values = new[] { KindBox?.SelectedItem, SizeBox?.SelectedItem, DateBox?.SelectedItem }
            .OfType<ConditionChoice>().Select(choice => choice.Value).Where(value => value.Length > 0);
        return string.Join(" ", values);
    }

    private void Condition_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ConditionDot is null || ClearConditionsButton is null) return;
        var active = BuildConditions().Length > 0;
        ConditionDot.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        ClearConditionsButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        OptionsToggle.ToolTip = active ? "검색 조건 적용 중 · 옵션" : "검색 및 크기 기준 옵션";
    }

    private void ClearConditions_Click(object sender, RoutedEventArgs e)
    {
        KindBox.SelectedIndex = 0;
        SizeBox.SelectedIndex = 0;
        DateBox.SelectedIndex = 0;
    }

    private sealed class QueueProgress(ConcurrentQueue<IReadOnlyList<SearchHit>> queue) : IProgress<IReadOnlyList<SearchHit>>
    {
        public void Report(IReadOnlyList<SearchHit> value) => queue.Enqueue(value);
    }

    private void ApplyScan(ScanResult scan, PreparedScan prepared, bool useAllocated)
    {
        CurrentScan = scan;
        _driveLists.Remove(FolderKey(scan.Root.FullPath)); // the new analysis is the fresher list
        _useAllocated = useAllocated;
        _settingMetric = true;
        MetricBox.SelectedItem = MetricChoices[useAllocated ? 1 : 0];
        MetricBox.ToolTip = scan.Summary.AllocationIsEstimated
            ? "현재 폴더 스캔의 할당 크기는 클러스터 단위 추정값입니다. 표와 디스크 맵의 크기 기준을 선택합니다."
            : "표와 디스크 맵의 면적 및 정렬 기준";
        _settingMetric = false;
        LogicalSizeText = Format.Bytes(scan.Root.Size);
        AllocatedSizeText = Format.Bytes(scan.Root.AllocatedSize);
        FileCountText = scan.Summary.Files.ToString("N0");
        ElapsedText = Format.Duration(scan.Summary.Elapsed);
        ScanModeText = NormalizeMode(scan.Summary.Mode);
        LargeFiles = prepared.LargeFiles;
        Extensions = prepared.Extensions;
        SearchResults = [];
        _scanCompletedUtc = DateTime.UtcNow;
        SearchStatusText.Text = IdleSearchStatus();
        _navigation.Clear();
        ShowFolder(scan.Root, false);

        StatusText.Text = $"분석 완료 · 폴더 {scan.Summary.Directories:N0}개 · 파일 {scan.Summary.Files:N0}개";
        HideMessage();
    }

    private static long Metric(ScanNode node, bool allocated) => allocated ? node.AllocatedSize : node.Size;

    private static PreparedScan PrepareScan(ScanResult scan, bool useAllocated)
    {
        var files = scan.Files.OrderByDescending(f => Metric(f, useAllocated)).Take(10000).Select(f => new FileRow(f)).ToArray();
        var total = Math.Max(1L, scan.Files.Sum(f => Math.Max(0, Metric(f, useAllocated))));
        var extensions = scan.Files.GroupBy(f => string.IsNullOrEmpty(f.Extension) ? "(확장자 없음)" : f.Extension)
            .Select(g => new ExtensionRow { Extension = g.Key, Count = g.LongCount(), Size = g.Sum(f => Math.Max(0, Metric(f, useAllocated))), Percent = 100d * g.Sum(f => Math.Max(0, Metric(f, useAllocated))) / total })
            .OrderByDescending(x => x.Size).ThenBy(x => x.Extension).ToArray();
        return new(files, extensions);
    }

    private void ShowFolder(ScanNode node, bool rememberCurrent = true)
    {
        if (!node.IsDirectory) node = node.Parent ?? CurrentScan?.Root ?? node;
        if (rememberCurrent && _currentViewNode is not null && !ReferenceEquals(_currentViewNode, node)) _navigation.Push(_currentViewNode);
        _currentViewNode = node;
        BackButton.IsEnabled = _navigation.Count > 0;
        FolderRows.Clear();
        var rootRow = new FolderRow(node, 0) { IsExpanded = true };
        FolderRows.Add(rootRow);
        foreach (var child in SortedDirectories(node)) FolderRows.Add(new(child, 1));
        FolderGrid.SelectedItem = rootRow;
        UpdateBreadcrumbs(node);
        UpdateTreemap(node);
        _ = UpdateExtensionScopeAsync(node);
    }

    private async Task UpdateExtensionScopeAsync(ScanNode node)
    {
        var generation = Interlocked.Increment(ref _viewGeneration);
        bool useAllocated = _useAllocated;
        var rows = await Task.Run(() =>
        {
            var files = DescendantFiles(node).ToArray();
            var total = Math.Max(1L, files.Sum(f => Math.Max(0, Metric(f, useAllocated))));
            return files.GroupBy(f => string.IsNullOrEmpty(f.Extension) ? "(확장자 없음)" : f.Extension)
                .Select(g => new ExtensionRow { Extension = g.Key, Count = g.LongCount(), Size = g.Sum(f => Math.Max(0, Metric(f, useAllocated))), Percent = 100d * g.Sum(f => Math.Max(0, Metric(f, useAllocated))) / total })
                .OrderByDescending(x => x.Size).ThenBy(x => x.Extension).ToArray();
        });
        if (generation != _viewGeneration || useAllocated != _useAllocated || !ReferenceEquals(node, _currentViewNode)) return;
        Extensions = rows;
        ExtensionScopeText.Text = (ReferenceEquals(node, CurrentScan?.Root) ? "전체 파일" : node.Name) + (_useAllocated ? CurrentScan?.Summary.AllocationIsEstimated == true ? " · 할당(추정)" : " · 할당" : " · 논리");
    }

    private static IEnumerable<ScanNode> DescendantFiles(ScanNode node)
    {
        var stack = new Stack<ScanNode>(); stack.Push(node);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var child in current.Children)
                if (child.IsDirectory) stack.Push(child); else yield return child;
        }
    }

    private void UpdateTreemap(ScanNode node)
    {
        Treemap.SetRoot(node, _useAllocated, _selectedNode);
        var basis = _useAllocated ? CurrentScan?.Summary.AllocationIsEstimated == true ? "할당 크기(추정)" : "할당 크기" : "논리 크기";
        TreemapScopeText.Text = $"{node.Name} · {basis} · 하위 파일 {node.FileCount:N0}개 · 최대 1,500개";
    }

    private void UpdateBreadcrumbs(ScanNode node)
    {
        BreadcrumbPanel.Children.Clear();
        var chain = new Stack<ScanNode>();
        for (var current = node; current is not null; current = current.Parent) chain.Push(current);
        while (chain.Count > 0)
        {
            var crumb = chain.Pop();
            var button = new Button { Content = string.IsNullOrWhiteSpace(crumb.Name) ? crumb.FullPath : crumb.Name, Tag = crumb, Style = (Style)FindResource("ActionButton"), Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), Width = double.NaN, Height = 30, Padding = new Thickness(8, 0, 8, 0) };
            button.Click += (_, _) => ShowFolder((ScanNode)button.Tag);
            BreadcrumbPanel.Children.Add(button);
            if (chain.Count > 0) BreadcrumbPanel.Children.Add(new TextBlock { Text = "›", Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"), VerticalAlignment = VerticalAlignment.Center });
        }
    }

    private IEnumerable<ScanNode> SortedDirectories(ScanNode node) => node.Children.Where(x => x.IsDirectory).OrderByDescending(NodeMetric).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase);
    private long NodeMetric(ScanNode node) => _useAllocated ? node.AllocatedSize : node.Size;

    private void FolderExpander_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not FolderRow row) return;
        var index = FolderRows.IndexOf(row); if (index < 0) return;
        if (row.IsExpanded)
        {
            row.IsExpanded = false;
            while (index + 1 < FolderRows.Count && FolderRows[index + 1].Level > row.Level) FolderRows.RemoveAt(index + 1);
        }
        else
        {
            row.IsExpanded = true;
            foreach (var child in SortedDirectories(row.Node).Reverse()) FolderRows.Insert(index + 1, new(child, row.Level + 1));
        }
        e.Handled = true;
    }

    private void SelectNode(ScanNode? node)
    {
        _selectedNode = node; Treemap.SelectNode(node);
        SelectionText.Text = node is null ? "" : $"{Format.Bytes(node.Size)} · {node.FullPath}";
    }

    private void LoadDrives()
    {
        var drives = new List<DriveItem>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                drives.Add(new($"{drive.Name.TrimEnd('\\')}  {Format.Bytes(drive.AvailableFreeSpace)} 여유", drive.RootDirectory.FullName));
        }
        catch { }
        DriveBox.ItemsSource = drives;
        if (drives.Count > 0) DriveBox.SelectedIndex = 0;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        string? path = null; var scan = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--path", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) path = args[++i];
            else if (args[i].Equals("--scan", StringComparison.OrdinalIgnoreCase)) scan = true;
        }
        if (!string.IsNullOrWhiteSpace(path)) PathBox.Text = path;
        if (scan && !string.IsNullOrWhiteSpace(path)) await ScanPathAsync(path);
    }

    public async Task FindDuplicatesAsync()
    {
        if (_optimizationBusy || _duplicateBusy || _duplicatePipelineActive || !ScanButton.IsEnabled || !SearchButton.IsEnabled) return;
        MainTabs.SelectedItem = DuplicatesTab;
        _duplicatePipelineActive = true;
        try
        {
            string requestedPath;
            try { requestedPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(PathBox.Text.Trim().Trim('"'))); }
            catch { DuplicateStatusText.Text = "중복을 찾을 위치를 선택하세요."; return; }
            if (!Directory.Exists(requestedPath))
            {
                DuplicateStatusText.Text = "선택한 폴더가 없거나 접근할 수 없습니다.";
                return;
            }

            static bool SamePath(string first, string second) => string.Equals(Path.TrimEndingDirectorySeparator(first), Path.TrimEndingDirectorySeparator(second), StringComparison.OrdinalIgnoreCase);
            var scan = CurrentScan;
            if (scan is null || !SamePath(scan.Root.FullPath, requestedPath))
            {
                var previousScan = scan;
                DuplicateStatusText.Text = "중복 검사를 위해 선택한 위치를 먼저 분석합니다. 분석을 취소하면 중복 검사도 중단됩니다.";
                await ScanPathAsync(requestedPath);
                if (CurrentScan is not { } completedScan || ReferenceEquals(completedScan, previousScan) || !SamePath(completedScan.Root.FullPath, requestedPath))
                {
                    DuplicateStatusText.Text = "파일 목록을 준비하지 못했거나 취소했습니다. 위치를 확인하고 다시 찾으세요.";
                    return;
                }
                scan = completedScan;
            }

            var generation = Interlocked.Increment(ref _duplicateGeneration);
            _duplicateCancellation?.Cancel();
            _duplicateCancellation?.Dispose();
            _duplicateCancellation = new();
            var token = _duplicateCancellation.Token;
            SetDuplicateBusy(true);
            DuplicateProgress.Visibility = Visibility.Visible;
            DuplicateProgress.IsIndeterminate = false;
            DuplicateProgress.Maximum = Math.Max(1, scan.Files.Count);
            DuplicateProgress.Value = 0;
            DuplicateStatusText.Text = $"파일 {scan.Files.Count:N0}개의 크기와 내용을 비교할 준비 중…";
            var acceptingProgress = true;
            var progress = new Progress<DuplicateProgress>(p =>
            {
                if (!acceptingProgress || generation != _duplicateGeneration) return;
                DuplicateProgress.Maximum = Math.Max(1, p.TotalFiles);
                DuplicateProgress.Value = Math.Min(DuplicateProgress.Maximum, Math.Max(0, p.ProcessedFiles));
                DuplicateStatusText.Text = $"{p.Stage} · {p.TotalFiles:N0}개 중 {p.ProcessedFiles:N0}개 · {Format.Bytes(p.ProcessedBytes)} · {CompactPath(p.CurrentPath)}";
            });

            try
            {
                var result = await _duplicateFinder.FindAsync(scan.Files, progress, token);
                if (generation != _duplicateGeneration || token.IsCancellationRequested || !ReferenceEquals(scan, CurrentScan)) return;
                var groups = result.Groups.OrderByDescending(group => group.ReclaimableBytes).ThenBy(group => group.Id, StringComparer.Ordinal).ToArray();
                var rows = groups.SelectMany((group, index) => group.Files
                    .OrderBy(file => file.FullPath, StringComparer.CurrentCultureIgnoreCase)
                    .Select(file => new DuplicateFileRow(file, group.Id, $"그룹 {index + 1} · {group.Files.Count}개", group.Files.Count)))
                    .ToArray();
                foreach (var row in rows) row.SelectionChanged += DuplicateRow_SelectionChanged;
                _duplicateGroups = groups;
                DuplicateRows = rows;
                DuplicateFailures = [];
                _duplicateSelectionWarning = false;
                acceptingProgress = false;
                var reclaimable = groups.Sum(group => group.ReclaimableBytes);
                DuplicateSummaryText.Text = $"그룹 {groups.Length:N0}개 · 파일 {rows.Length:N0}개 · 확보 가능 {Format.Bytes(reclaimable)}";
                DuplicateStatusText.Text = $"중복 검사 완료 · 제외/읽기 실패 {result.SkippedFiles:N0}개 · {Format.Duration(result.Elapsed)} · 시스템 파일 제외 적용 · 삭제할 파일을 직접 선택하세요.";
                DuplicateStatusText.ToolTip = DuplicateStatusText.Text;
                UpdateDuplicateSelection();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (generation == _duplicateGeneration)
                    DuplicateStatusText.Text = DuplicateRows.Count == 0 ? "중복 검사를 취소했습니다." : "중복 검사를 취소했습니다. 이전 결과는 그대로 유지됩니다.";
            }
            catch (Exception ex)
            {
                if (generation == _duplicateGeneration) DuplicateStatusText.Text = $"중복 파일을 찾지 못했습니다: {ex.Message}";
            }
            finally
            {
                acceptingProgress = false;
                if (generation == _duplicateGeneration) SetDuplicateBusy(false);
            }
        }
        finally
        {
            _duplicatePipelineActive = false;
            if (!_duplicateBusy && IsInitialized) FindDuplicatesButton.IsEnabled = ScanButton.IsEnabled;
        }
    }

    public async Task DeleteSelectedDuplicatesAsync()
    {
        if (_optimizationBusy) return;
        if (_duplicateBusy) return;
        var selectedRows = DuplicateRows.Where(row => row.IsSelected).ToArray();
        if (selectedRows.Length == 0)
        {
            DuplicateStatusText.Text = "휴지통으로 이동할 파일을 먼저 선택하세요.";
            return;
        }

        var selectedPaths = selectedRows.Select(row => row.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unsafeGroups = _duplicateGroups.Where(group => group.Files.All(file => selectedPaths.Contains(file.FullPath))).ToArray();
        if (unsafeGroups.Length > 0)
        {
            _duplicateSelectionWarning = true;
            DuplicateStatusText.Text = $"그룹 {unsafeGroups.Length:N0}개에서 모든 파일이 선택되었습니다. 각 그룹에 보관할 파일을 최소 1개 남기세요.";
            return;
        }

        var selectedBytes = selectedRows.Sum(row => Math.Max(0, row.Size));
        if (!ConfirmDuplicateDeletion(selectedRows.Length, selectedBytes)) return;
        _duplicateSelectionWarning = false;

        var generation = Interlocked.Increment(ref _duplicateGeneration);
        _duplicateCancellation?.Cancel();
        _duplicateCancellation?.Dispose();
        _duplicateCancellation = new();
        var token = _duplicateCancellation.Token;
        var rescanPath = CurrentScan?.Root.FullPath;
        SetDuplicateBusy(true);
        DuplicateProgress.Visibility = Visibility.Visible;
        DuplicateProgress.IsIndeterminate = false;
        DuplicateProgress.Maximum = Math.Max(1, selectedRows.Length);
        DuplicateProgress.Value = 0;
        var acceptingProgress = true;
        var progress = new Progress<DuplicateProgress>(p =>
        {
            if (!acceptingProgress || generation != _duplicateGeneration) return;
            DuplicateProgress.Maximum = Math.Max(1, p.TotalFiles);
            DuplicateProgress.Value = Math.Min(DuplicateProgress.Maximum, Math.Max(0, p.ProcessedFiles));
            DuplicateStatusText.Text = $"{p.Stage} · {p.TotalFiles:N0}개 중 {p.ProcessedFiles:N0}개 · {CompactPath(p.CurrentPath)}";
        });
        string outcome;
        var refreshScan = false;

        try
        {
            var result = await _duplicateDeletionService.DeleteAsync(_duplicateGroups, selectedPaths, progress, token);
            if (generation != _duplicateGeneration) return;
            acceptingProgress = false;
            refreshScan = result.DeletedPaths.Count > 0;
            if (refreshScan)
            {
                var deleted = new HashSet<string>(result.DeletedPaths, StringComparer.OrdinalIgnoreCase);
                ILuncher.AppKit.AppUsage.Count("clean", DuplicateRows.Where(row => deleted.Contains(row.FullPath)).Sum(row => (double)row.Size));
            }
            SetDuplicateFailures(result.Failures);
            if (!refreshScan) foreach (var row in DuplicateRows) row.Error = result.Failures.TryGetValue(row.FullPath, out var error) ? error : "";
            outcome = DeleteOutcome(result.DeletedPaths.Count, result.Failures, token.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (generation != _duplicateGeneration) return;
            refreshScan = true;
            outcome = "휴지통 이동을 취소했습니다. 취소 전에 일부 파일이 이동되었을 수 있어 현재 위치를 다시 분석합니다.";
        }
        catch (Exception ex)
        {
            if (generation != _duplicateGeneration) return;
            outcome = $"파일을 휴지통으로 이동하지 못했습니다: {ex.Message}";
        }
        finally
        {
            acceptingProgress = false;
            if (generation == _duplicateGeneration) SetDuplicateBusy(false);
        }

        DuplicateStatusText.Text = outcome;
        DuplicateStatusText.ToolTip = outcome;
        if (refreshScan && !string.IsNullOrWhiteSpace(rescanPath))
        {
            var failures = DuplicateFailures;
            ClearDuplicateResults();
            await ScanPathAsync(rescanPath);
            MainTabs.SelectedItem = DuplicatesTab;
            DuplicateFailures = failures;
            DuplicateStatusText.Text = outcome;
            DuplicateStatusText.ToolTip = outcome;
        }
    }

    private bool ConfirmDuplicateDeletion(int count, long bytes)
    {
        if (_duplicateConfirmation is not null) return _duplicateConfirmation(count, bytes);
        return MessageBox.Show(this,
            $"선택한 파일 {count:N0}개({Format.Bytes(bytes)})를 휴지통으로 이동할까요?\n각 중복 그룹에는 보관할 파일이 최소 1개 남습니다.",
            "중복 파일을 휴지통으로 이동", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK;
    }

    private static string DeleteOutcome(int deletedCount, IReadOnlyDictionary<string, string> failures, bool cancelled)
    {
        var summary = cancelled
            ? $"취소됨 · 취소 전 휴지통으로 이동 {deletedCount:N0}개"
            : $"휴지통으로 이동 {deletedCount:N0}개";
        if (failures.Count == 0) return summary + " · 현재 위치를 다시 분석합니다.";
        return $"{summary} · 실패 {failures.Count:N0}개 · 아래 ‘실패’ 목록에서 파일별 이유를 확인하세요.";
    }

    private void SetDuplicateFailures(IReadOnlyDictionary<string, string> failures) => DuplicateFailures = failures
        .OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase)
        .Select(pair => $"{pair.Key} — {pair.Value}")
        .ToArray();

    private void CancelDuplicateWork(bool clearResults)
    {
        Interlocked.Increment(ref _duplicateGeneration);
        _duplicateCancellation?.Cancel();
        _duplicateBusy = false;
        if (clearResults) ClearDuplicateResults();
    }

    private void ClearDuplicateResults()
    {
        foreach (var row in DuplicateRows) row.SelectionChanged -= DuplicateRow_SelectionChanged;
        DuplicateRows = [];
        _duplicateGroups = [];
        DuplicateFailures = [];
        _duplicateSelectionWarning = false;
        if (IsInitialized)
        {
            DuplicateSummaryText.Text = "선택한 위치에서 중복을 찾습니다 · 시스템 파일 제외";
            DuplicateStatusText.Text = "현재 분석 결과가 바뀌었습니다. 중복 파일을 다시 찾으세요.";
            DuplicateStatusText.ToolTip = DuplicateStatusText.Text;
            UpdateDuplicateSelection();
        }
    }

    private void SetDuplicateBusy(bool busy)
    {
        _duplicateBusy = busy;
        ScanButton.IsEnabled = !busy;
        CancelButton.IsEnabled = false;
        DriveBox.IsEnabled = !busy;
        PathBox.IsEnabled = !busy;
        BrowseButton.IsEnabled = !busy;
        SearchBox.IsEnabled = !busy;
        SearchScopeBox.IsEnabled = !busy;
        SearchModeBox.IsEnabled = !busy;
        MatchCaseBox.IsEnabled = !busy;
        SearchButton.IsEnabled = !busy;
        SearchCancelButton.IsEnabled = false;
        MetricBox.IsEnabled = !busy;
        FindDuplicatesButton.IsEnabled = !busy && !_duplicatePipelineActive;
        CancelDuplicatesButton.IsEnabled = busy;
        DuplicateGrid.IsEnabled = !busy;
        OpenDuplicateFolderButton.IsEnabled = !busy && DuplicateGrid.SelectedItem is DuplicateFileRow;
        DuplicateProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateDuplicateSelection();
    }

    private void DuplicateRow_SelectionChanged(object? sender, EventArgs e)
    {
        UpdateDuplicateSelection();
        if (_duplicateSelectionWarning && !HasFullySelectedDuplicateGroup())
        {
            _duplicateSelectionWarning = false;
            DuplicateStatusText.Text = DuplicateRows.Any(row => row.IsSelected)
                ? "선택한 파일은 확인 후 휴지통으로 이동합니다. 각 그룹에는 보관할 파일이 남습니다."
                : "삭제할 파일을 직접 선택하세요. 항목은 자동으로 선택되지 않습니다.";
            DuplicateStatusText.ToolTip = DuplicateStatusText.Text;
        }
    }

    private bool HasFullySelectedDuplicateGroup()
    {
        var selected = DuplicateRows.Where(row => row.IsSelected).Select(row => row.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _duplicateGroups.Any(group => group.Files.Count > 0 && group.Files.All(file => selected.Contains(file.FullPath)));
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || !ReferenceEquals(e.Source, MainTabs)) return;
        var useExpandedLayout = ReferenceEquals(MainTabs.SelectedItem, DuplicatesTab) || ReferenceEquals(MainTabs.SelectedItem, OptimizationTab);
        if (useExpandedLayout == _duplicateLayoutActive) return;

        if (useExpandedLayout)
        {
            _savedContentRowHeight = MainContentRow.Height;
            _savedMapRowHeight = DiskMapRow.Height;
            _savedMapRowMinHeight = DiskMapRow.MinHeight;
            _savedResultsColumnWidth = ResultsColumn.Width;
            _savedExtensionColumnWidth = ExtensionColumn.Width;
            MainContentRow.Height = new GridLength(1, GridUnitType.Star);
            MapSplitterRow.Height = new GridLength(0);
            DiskMapRow.MinHeight = 0;
            DiskMapRow.Height = new GridLength(0);
            ResultsColumn.Width = new GridLength(1, GridUnitType.Star);
            ResultsSplitterColumn.Width = new GridLength(0);
            ExtensionColumn.Width = new GridLength(0);
            MapSplitter.Visibility = Visibility.Collapsed;
            DiskMapPanel.Visibility = Visibility.Collapsed;
            ResultsSplitter.Visibility = Visibility.Collapsed;
            ExtensionPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            MainContentRow.Height = _savedContentRowHeight;
            MapSplitterRow.Height = new GridLength(5);
            DiskMapRow.MinHeight = _savedMapRowMinHeight;
            DiskMapRow.Height = _savedMapRowHeight;
            ResultsColumn.Width = _savedResultsColumnWidth;
            ResultsSplitterColumn.Width = new GridLength(8);
            ExtensionColumn.Width = _savedExtensionColumnWidth;
            MapSplitter.Visibility = Visibility.Visible;
            DiskMapPanel.Visibility = Visibility.Visible;
            ResultsSplitter.Visibility = Visibility.Visible;
            ExtensionPanel.Visibility = Visibility.Visible;
        }
        _duplicateLayoutActive = useExpandedLayout;
    }

    private void UpdateDuplicateSelection()
    {
        if (!IsInitialized) return;
        var selected = DuplicateRows.Where(row => row.IsSelected).ToArray();
        var actionsAvailable = !_duplicateBusy && ScanButton.IsEnabled;
        DuplicateSelectionText.Text = $"선택 {selected.Length:N0}개 · {Format.Bytes(selected.Sum(row => Math.Max(0, row.Size)))}";
        ClearDuplicateSelectionButton.IsEnabled = actionsAvailable && selected.Length > 0;
        DeleteDuplicatesButton.IsEnabled = actionsAvailable && selected.Length > 0;
    }

    private void SetScanBusy(bool busy)
    {
        ScanButton.IsEnabled = !busy; CancelButton.IsEnabled = busy; DriveBox.IsEnabled = !busy; PathBox.IsEnabled = !busy; BrowseButton.IsEnabled = !busy;
        SearchBox.IsEnabled = !busy; SearchScopeBox.IsEnabled = !busy; SearchModeBox.IsEnabled = !busy; MatchCaseBox.IsEnabled = !busy; SearchButton.IsEnabled = !busy; SearchCancelButton.IsEnabled = false; MetricBox.IsEnabled = !busy;
        FindDuplicatesButton.IsEnabled = !busy && !_duplicatePipelineActive; CancelDuplicatesButton.IsEnabled = busy && _duplicatePipelineActive;
        DuplicateGrid.IsEnabled = !busy;
        OpenDuplicateFolderButton.IsEnabled = !busy && DuplicateGrid.SelectedItem is DuplicateFileRow;
        UpdateDuplicateSelection();
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ShowMessage(string message, bool _) { MessageText.Text = message; MessageBar.Visibility = Visibility.Visible; }
    private void HideMessage() { MessageBar.Visibility = Visibility.Collapsed; }
    private static string NormalizeMode(string? mode) => string.IsNullOrWhiteSpace(mode) ? "폴더 스캔" : mode.Contains("MFT", StringComparison.OrdinalIgnoreCase) ? "MFT" : "폴더 스캔";
    private static string CompactPath(string path) => path.Length <= 75 ? path : "…" + path[^74..];

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanPathAsync(PathBox.Text);
    private void Cancel_Click(object sender, RoutedEventArgs e) => _scanCancellation?.Cancel();
    private SearchMode CurrentSearchMode => SearchModeBox.SelectedItem is SearchModeChoice choice ? choice.Mode : SearchMode.NameOrContents;
    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync(SearchBox.Text, CurrentSearchMode);
    private void ExtensionGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var extension = (ExtensionGrid.SelectedItem as ExtensionRow)?.Extension;
        Treemap.HighlightExtension(extension);
        ClearTypeHighlightButton.Visibility = extension is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private void ClearTypeHighlight_Click(object sender, RoutedEventArgs e) => ExtensionGrid.SelectedItem = null;
    private void SearchCancel_Click(object sender, RoutedEventArgs e) { _searchCancellation?.Cancel(); CancelDriveListReads(); }
    private async void FindDuplicates_Click(object sender, RoutedEventArgs e) => await FindDuplicatesAsync();
    private void CancelDuplicates_Click(object sender, RoutedEventArgs e) => CancelDuplicatePipeline();
    private void CancelDuplicatePipeline()
    {
        _duplicateCancellation?.Cancel();
        if (_duplicatePipelineActive) _scanCancellation?.Cancel();
    }
    private async void DeleteDuplicates_Click(object sender, RoutedEventArgs e) => await DeleteSelectedDuplicatesAsync();
    private void ClearDuplicateSelection_Click(object sender, RoutedEventArgs e) { foreach (var row in DuplicateRows) row.IsSelected = false; }
    private void DuplicateGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OpenDuplicateFolderButton.IsEnabled = !_duplicateBusy && DuplicateGrid.SelectedItem is DuplicateFileRow;
        if (DuplicateGrid.SelectedItem is DuplicateFileRow row) SelectNode(row.Node);
    }
    private void OpenDuplicateFolder_Click(object sender, RoutedEventArgs e) { if (DuplicateGrid.SelectedItem is DuplicateFileRow row) OpenPath(row.Node); }
    private async void PathBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await ScanPathAsync(PathBox.Text); } }
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        // With Korean input the first Enter also finishes the syllable being typed; it
        // arrives as ImeProcessed. Search once the finished text is in the box.
        if (e.Key == Key.ImeProcessed && e.ImeProcessedKey == Key.Enter)
        {
            _lastImeSearchTicks = Environment.TickCount64;
            await Dispatcher.Yield(DispatcherPriority.Background);
            await SearchAsync(SearchBox.Text, CurrentSearchMode);
            return;
        }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (Environment.TickCount64 - _lastImeSearchTicks < 400) return;
        await SearchAsync(SearchBox.Text, CurrentSearchMode);
    }
    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OptionsToggle.IsChecked = false;
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            OptionsToggle.IsChecked = false;
            if (CancelButton.IsEnabled) _scanCancellation?.Cancel();
            if (SearchCancelButton.IsEnabled) { _searchCancellation?.Cancel(); CancelDriveListReads(); }
            if (CancelDuplicatesButton.IsEnabled) CancelDuplicatePipeline();
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && ScanButton.IsEnabled)
        {
            e.Handled = true;
            await ScanPathAsync(PathBox.Text);
        }
    }
    private void DriveBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (DriveBox.SelectedItem is DriveItem d && ScanButton.IsEnabled) PathBox.Text = d.Path; }
    private async void MetricBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_optimizationBusy || _settingMetric || CurrentScan is not { } scan || MetricBox.SelectedItem is not MetricBasisChoice choice || choice.UseAllocated == _useAllocated) return;
        _useAllocated = choice.UseAllocated;
        var generation = Interlocked.Increment(ref _metricGeneration);
        MetricBox.IsEnabled = false;
        try
        {
            var prepared = await Task.Run(() => PrepareScan(scan, _useAllocated));
            if (generation != _metricGeneration || !ReferenceEquals(scan, CurrentScan)) return;
            LargeFiles = prepared.LargeFiles;
            var view = _currentViewNode ?? scan.Root;
            if (ReferenceEquals(view, scan.Root)) Extensions = prepared.Extensions;
            ShowFolder(view, false);
            StatusText.Text = $"크기 기준을 {(_useAllocated ? scan.Summary.AllocationIsEstimated ? "할당 크기(추정)" : "할당 크기" : "논리 크기")}로 변경했습니다.";
        }
        catch (Exception ex) { ShowMessage($"크기 기준을 변경하지 못했습니다: {ex.Message}", false); }
        finally { if (generation == _metricGeneration) MetricBox.IsEnabled = ScanButton.IsEnabled; }
    }
    private void Browse_Click(object sender, RoutedEventArgs e) { var dialog = new OpenFolderDialog { Title = "분석할 폴더 선택", InitialDirectory = Directory.Exists(PathBox.Text) ? PathBox.Text : null }; if (dialog.ShowDialog(this) == true) PathBox.Text = dialog.FolderName; }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Back_Click(object sender, RoutedEventArgs e) { if (_navigation.TryPop(out var node)) ShowFolder(node, false); }
    private void FolderGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (FolderGrid.SelectedItem is FolderRow row) SelectNode(row.Node); }
    private void FileGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (LargeFilesGrid.SelectedItem is FileRow row) SelectNode(row.Node); }
    private void SearchGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (SearchResultsGrid.SelectedItem is SearchResultRow row) SelectNode(row.Node); }
    private void FolderGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (FolderGrid.SelectedItem is FolderRow row) ShowFolder(row.Node); }
    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if ((sender as DataGrid)?.SelectedItem is IPathItem item) OpenPath(item.Node); }
    private void Treemap_NodeSelected(object? sender, ScanNodeEventArgs e) => SelectNode(e.Node);
    private void Treemap_FolderInvoked(object? sender, ScanNodeEventArgs e) => ShowFolder(e.Node);

    private void OpenLocation_Click(object sender, RoutedEventArgs e) { if (ContextItem(sender) is { } item) OpenPath(item.Node); }
    private void SelectInExplorer_Click(object sender, RoutedEventArgs e) { if (ContextItem(sender) is { } item) SelectInExplorer(item.Node); }
    private void CopyPath_Click(object sender, RoutedEventArgs e) { if (ContextItem(sender) is { } item) try { Clipboard.SetText(item.Node.FullPath); StatusText.Text = "경로를 클립보드에 복사했습니다."; } catch (Exception ex) { StatusText.Text = $"경로를 복사하지 못했습니다: {ex.Message}"; } }
    private static IPathItem? ContextItem(object sender) => (sender as MenuItem)?.DataContext as IPathItem;
    private void OpenPath(ScanNode node)
    {
        try { var target = node.IsDirectory ? node.FullPath : Path.GetDirectoryName(node.FullPath); if (!string.IsNullOrWhiteSpace(target) && Directory.Exists(target)) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); else StatusText.Text = "선택한 위치를 찾을 수 없습니다."; }
        catch (Exception ex) { ShowMessage($"위치를 열지 못했습니다: {ex.Message}", false); }
    }
    private void SelectInExplorer(ScanNode node)
    {
        try { if (node.IsDirectory) Process.Start(new ProcessStartInfo(node.FullPath) { UseShellExecute = true }); else Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = $"/select,\"{node.FullPath}\"" }); }
        catch (Exception ex) { ShowMessage($"탐색기에서 선택하지 못했습니다: {ex.Message}", false); }
    }
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value; PropertyChanged?.Invoke(this, new(name));
    }

    private sealed record PreparedScan(IReadOnlyList<FileRow> LargeFiles, IReadOnlyList<ExtensionRow> Extensions);
}
