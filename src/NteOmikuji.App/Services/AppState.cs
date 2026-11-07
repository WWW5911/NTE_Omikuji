using System.Runtime.Versioning;
using NteOmikuji.Core;
using NteOmikuji.Core.Capture;
using NteOmikuji.Core.Export;
using NteOmikuji.Core.Import;
using NteOmikuji.Core.Mapping;
using NteOmikuji.Core.Protocol;
using NteOmikuji.Core.Session;

namespace NteOmikuji.App.Services;

public sealed record EventUsage(int Rows, string? LastDate);

public enum AppStatus
{
    Idle,
    Capturing,
    Stopping,
    Importing,
}

/// <summary>App-wide state. Packets are read on a background thread; the page observes <see cref="Changed"/>.</summary>
[SupportedOSPlatform("windows")]
public sealed class AppState : IDisposable
{
    private const int MaxActivityLines = 200;
    private const string MissedFirstPage = "DID_NOT_START_AT_PAGE_1";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan InboundGracePeriod = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MappingReloadDelay = TimeSpan.FromMilliseconds(600);

    private readonly object _gate = new();
    private readonly object _dataGate = new();
    private readonly string _baseFolder;
    private readonly List<string> _activity = [];
    private readonly MappingStore _mappings;
    private readonly HistoryLibrary _library;
    private readonly AppSettings _settings;
    private readonly Dictionary<HistoryKind, PoolHistory> _partial = [];
    private readonly Dictionary<HistoryKind, IReadOnlyList<CaptureWarning>> _warnings = [];
    private readonly FileSystemWatcher? _mappingWatcher;
    private readonly Timer _mappingTimer;

    private RewardCatalog _catalog = RewardCatalog.Bundled.Value;
    private CancellationTokenSource? _cancellation;
    private Task? _captureTask;
    private HistorySession? _session;
    private string? _account;
    private string? _sessionAccount;
    private string? _typedUid;
    private DateTime _sessionStamp = DateTime.Now;
    private volatile bool _dirty;
    private DateTime _lastRefresh;

    public AppState()
    {
        _baseFolder = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        _mappings = new MappingStore(Path.Combine(_baseFolder, "mappings"));
        _library = new HistoryLibrary(Path.Combine(_baseFolder, "user_data"));
        _settings = AppSettings.Load(_library.Folder);
        // This app's own saves change the file too, and those are already applied.
        _mappingTimer = new Timer(_ =>
        {
            if (_mappings.HasExternalChanges())
            {
                ReloadMappings();
            }
        });
        try
        {
            _mappings.EnsureSeeded();
            LoadMappings();
            _mappingWatcher = new FileSystemWatcher(_mappings.Folder, "*.csv")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _mappingWatcher.Changed += OnMappingFileChanged;
            _mappingWatcher.Created += OnMappingFileChanged;
            _mappingWatcher.Renamed += OnMappingFileChanged;
            foreach (var issue in _library.Load(_catalog))
            {
                Log(issue);
            }
            _account = _library.Accounts.FirstOrDefault();
            RefreshView();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Error = $"無法使用程式資料夾，紀錄不會被儲存。請把程式移到可寫入的位置：{exception.Message}";
        }
    }

    public event Action? Changed;

    public AppStatus Status { get; private set; }
    public string? Error { get; private set; }
    public string? Notice { get; private set; }
    public string? CaptureName { get; private set; }
    public string? FallbackReason { get; private set; }
    public IReadOnlyList<PoolHistory> Histories { get; private set; } = [];
    public IReadOnlyList<AchievementRecord> Achievements { get; private set; } = [];
    public IReadOnlyList<string> MappingIssues { get; private set; } = [];
    public int MappedRewardCount => _catalog.RewardCount;
    public RewardTable Rewards { get; private set; } = RewardTable.Empty;
    public IReadOnlyDictionary<string, int> SeenRewards { get; private set; } = new Dictionary<string, int>();
    public EventTable Events { get; private set; } = EventTable.Empty;
    public IReadOnlyList<ScheduledEvent> Schedule { get; private set; } = [];
    public IReadOnlyDictionary<string, EventUsage> SeenEvents { get; private set; } = new Dictionary<string, EventUsage>();

    /// <summary>Events in the history with no row in events.csv, so nothing marks what is limited on them.</summary>
    public IReadOnlyList<string> UnsetEvents =>
        SeenEvents.Keys.Where(id => !Events.Entries.Any(entry => entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).Order(StringComparer.Ordinal).ToList();
    public bool CanUndoMapping => _mappings.CanUndo;
    public int MappingWriteFailures { get; private set; }
    public string DisplayLanguage => _settings.DisplayLanguage;
    public DateTime? LastSaved { get; private set; }
    public long PacketCount => _session?.PacketCount ?? 0;
    public long InboundCount => _session?.InboundCount ?? 0;
    public long OutboundCount => _session?.OutboundCount ?? 0;
    public string DataFolder => _library.Folder;
    public string MappingFolder => _mappings.Folder;
    public string BackupFolder => _library.BackupFolder;
    public string ExportsFolder => Path.Combine(_baseFolder, "exports");
    public string? Account => _account;
    public IReadOnlyList<string> Accounts => _library.Accounts.Where(uid => uid.Length > 0).ToList();
    public string? ServerId => _session?.ServerId ?? _library.ServerOf(_account);

    public IReadOnlyList<string> Activity
    {
        get { lock (_gate) { return _activity.ToList(); } }
    }

    public void Start(string localIp, CaptureBackendKind backend, bool recordPackets = false)
    {
        if (Status != AppStatus.Idle)
        {
            return;
        }
        BeginSession();
        IPacketCapture capture;
        try
        {
            capture = CaptureFactory.Open(localIp, backend);
        }
        catch (CaptureRequiresElevationException exception) when (!Elevation.IsElevated)
        {
            if (Elevation.RelaunchForCapture(localIp, backend, recordPackets))
            {
                MainThread.BeginInvokeOnMainThread(() => Application.Current?.Quit());
                return;
            }
            Notice = $"{exception.Message}已取消授權；不提權時仍可匯入檔案。";
            NotifyChanged();
            return;
        }
        catch (CaptureUnavailableException exception)
        {
            Fail(exception.Message);
            return;
        }

        var session = new HistorySession(localIp, _catalog);
        _session = session;
        _sessionAccount = _account;
        CaptureName = capture.Name;
        FallbackReason = capture.FallbackReason;
        Status = AppStatus.Capturing;
        Log($"開始擷取：{capture.Name}（{capture.Detail}）");
        if (recordPackets)
        {
            OpenDebugLog(session, capture.Name);
        }

        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        _captureTask = Task.Factory.StartNew(() => ReadPackets(capture, session, token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        NotifyChanged();
    }

    public async Task StopAsync()
    {
        if (Status != AppStatus.Capturing || _captureTask is null)
        {
            return;
        }
        Status = AppStatus.Stopping;
        NotifyChanged();
        _cancellation?.Cancel();
        await _captureTask;
        await Task.Run(() =>
        {
            _session?.Finish();
            Ingest();
        });
        Status = AppStatus.Idle;
        Log($"已停止，共處理 {PacketCount:N0} 個封包。");
        NotifyChanged();
    }

    /// <summary>Accepts .flows captures, exported JSON, and mapping CSV or JSON, told apart by content.</summary>
    public async Task ImportAsync(IReadOnlyList<(string Name, byte[] Bytes)> files)
    {
        if (Status != AppStatus.Idle)
        {
            return;
        }
        Error = null;
        Notice = null;
        Status = AppStatus.Importing;
        NotifyChanged();
        try
        {
            await Task.Run(() =>
            {
                foreach (var (name, bytes) in files)
                {
                    try
                    {
                        Log($"已匯入 {name}：{ImportFile(name, bytes)}");
                    }
                    catch (Exception exception) when (exception is InvalidDataException or FormatException or OverflowException or IOException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
                    {
                        Error = $"無法匯入 {name}：{exception.Message}";
                        Log(Error);
                    }
                }
                Persist();
            });
        }
        finally
        {
            Status = AppStatus.Idle;
            NotifyChanged();
        }
    }

    public void SelectAccount(string? typed)
    {
        lock (_dataGate)
        {
            _typedUid = string.IsNullOrWhiteSpace(typed) ? null : typed.Trim();
            _account = _typedUid ?? _session?.UserUid ?? _account;
        }
        Ingest();
        NotifyChanged();
    }

    public void SetServer(string? serverId)
    {
        lock (_dataGate)
        {
            _library.SetServer(_account, string.IsNullOrWhiteSpace(serverId) ? null : serverId);
        }
        Persist();
        NotifyChanged();
    }

    public string ExportJson(PoolHistory history)
    {
        lock (_dataGate)
        {
            return HistoryJsonWriter.ToJson(HistoryJsonWriter.BuildHistory(history, _library.ContextFor(_account)));
        }
    }

    public void ReloadMappings()
    {
        ApplyMappings();
        Log($"已重新載入對應檔：{_catalog.RewardCount} 筆獎勵。");
        NotifyChanged();
    }

    /// <summary>Writes history for spreadsheets. Returns the file, or null when it could not be written.</summary>
    public string? ExportCsv(IReadOnlyList<PoolHistory> histories, string label)
    {
        try
        {
            Directory.CreateDirectory(ExportsFolder);
            var path = Path.Combine(ExportsFolder, $"NTE_{label}_{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            AtomicFile.WriteAllText(path, HistoryCsv.ToText(histories, DisplayName, EventName), withBom: true);
            Log($"已匯出 CSV：{path}");
            NotifyChanged();
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Error = $"無法匯出 CSV：{exception.Message}";
            NotifyChanged();
            return null;
        }
    }

    /// <summary>Saves a share image. Returns the file, or null when it could not be written.</summary>
    public string? SaveShareImage(byte[] image, string label)
    {
        try
        {
            Directory.CreateDirectory(ExportsFolder);
            var path = Path.Combine(ExportsFolder, $"NTE_share_{label}_{DateTime.Now:yyyyMMdd-HHmmss}.png");
            File.WriteAllBytes(path, image);
            Log($"已儲存分享圖：{path}");
            NotifyChanged();
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Error = $"無法儲存分享圖：{exception.Message}";
            NotifyChanged();
            return null;
        }
    }

    public string DisplayName(HistoryRow row)
    {
        var name = _catalog.Find(row.RewardId)?.Localized(_settings.DisplayLanguage);
        return string.IsNullOrEmpty(name) ? row.RewardId : name;
    }

    /// <summary>A reward's name in the display language, or null when the mapping does not have it.</summary>
    public string? RewardName(string id)
    {
        var name = _catalog.Find(id)?.Localized(_settings.DisplayLanguage);
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>An event is named after the first reward it features.</summary>
    public string? EventName(string id)
    {
        var featured = Events.Entries.FirstOrDefault(entry => entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Featured.FirstOrDefault();
        return featured is null ? null : RewardName(featured) ?? featured;
    }

    public void SaveEvent(string id, string? featured) => ChangeMappings(() => _mappings.SaveEvent(id, EventEntry.ParseFeatured(featured)));

    public void DeleteEvent(string id) => ChangeMappings(() => _mappings.DeleteEvent(id));

    public bool IsLocaleVisible(string locale) => _settings.VisibleLocales.Contains(locale);

    public void SetLocaleVisible(string locale, bool visible)
    {
        if (visible)
        {
            _settings.VisibleLocales.Add(locale);
        }
        else
        {
            _settings.VisibleLocales.Remove(locale);
        }
        SaveSettings();
        NotifyChanged();
    }

    public void SetDisplayLanguage(string locale)
    {
        _settings.DisplayLanguage = locale;
        SaveSettings();
        NotifyChanged();
    }

    public void SaveReward(RewardEntry entry) => ChangeMappings(() => _mappings.SaveReward(entry));

    public void DeleteReward(string id) => ChangeMappings(() => _mappings.DeleteReward(id));

    public void UndoMapping() => ChangeMappings(() => _mappings.Undo());

    public void FillMappingsFromBundled() => ChangeMappings(() => Log($"已從內建資料補齊 {_mappings.FillFromBundled()} 個欄位或項目。"));

    /// <summary>Returns true when the language now exists, and shows it.</summary>
    public bool AddLocale(string locale)
    {
        var code = locale.Trim().ToLowerInvariant();
        var added = ChangeMappings(() => _mappings.AddLocale(code));
        if (added)
        {
            SetLocaleVisible(code, true);
        }
        return added;
    }

    /// <summary>Applies an edit to the CSV, then to the screen and saved history. Excel locking the file is the usual failure.</summary>
    private bool ChangeMappings(Action change)
    {
        try
        {
            lock (_dataGate)
            {
                change();
            }
            ApplyMappings();
            Error = null;
            NotifyChanged();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Error = $"無法寫入對應檔，可能正被 Excel 開啟。關閉後再試一次。（{exception.Message}）";
        }
        catch (InvalidDataException exception)
        {
            Error = exception.Message;
        }
        // Rebuilds the rows so inputs stop showing text that was not saved.
        MappingWriteFailures++;
        NotifyChanged();
        return false;
    }

    private void ApplyMappings()
    {
        LoadMappings();
        lock (_dataGate)
        {
            _library.ApplyCatalog(_catalog);
        }
        Ingest();
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Save(_library.Folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Error = $"無法儲存設定：{exception.Message}";
        }
    }

    public void Dispose()
    {
        _cancellation?.Cancel();
        _captureTask?.Wait(TimeSpan.FromSeconds(2));
        _cancellation?.Dispose();
        _mappingWatcher?.Dispose();
        _mappingTimer.Dispose();
    }

    private string ImportFile(string name, byte[] bytes)
    {
        switch (ImportedFiles.Classify(name, bytes))
        {
            case ImportKind.Flows:
                var flow = FlowCaptureFile.Import(bytes, _catalog);
                _session = flow.Session;
                _sessionAccount = _account;
                _partial.Clear();
                _warnings.Clear();
                CaptureName = "mitmproxy_flows";
                Ingest("packet_capture");
                return $"{flow.CandidatePairs} 個頁面";
            case ImportKind.HistoryExport:
                var history = ImportedFiles.ReadHistory(bytes);
                lock (_dataGate)
                {
                    var uid = history.UserUid ?? _account;
                    _library.Merge(uid, history.Pool.Kind, history.Rows, history.ServerId, history.Source, history.CaptureSource, _catalog);
                    ShowImportedAccount(uid);
                    CaptureName = history.CaptureSource ?? "json";
                    RefreshView();
                }
                return $"{history.Pool.Name} {history.Rows.Count} 筆";
            case ImportKind.GachaExporterRecords:
                var records = ImportedFiles.ReadGachaExporter(bytes);
                lock (_dataGate)
                {
                    var parts = new List<string>();
                    var skipped = 0;
                    foreach (var (kind, rows) in records.Rows)
                    {
                        var (added, duplicates) = _library.Fill(_account, kind, rows, _catalog);
                        skipped += duplicates;
                        if (added > 0)
                        {
                            parts.Add($"{HistoryCsv.PoolName(kind)} {added} 筆");
                        }
                    }
                    ShowImportedAccount(_account);
                    RefreshView();
                    var summary = parts.Count == 0 ? "沒有缺少的紀錄" : $"補上 {string.Join("、", parts)}";
                    Notice = $"{summary}；{skipped} 筆的時間已有紀錄，保留原資料{(records.Ignored > 0 ? $"；{records.Ignored} 筆無法辨識，已略過" : "")}";
                    return Notice;
                }
            case ImportKind.AchievementExport:
                var achievements = ImportedFiles.ReadAchievements(bytes);
                lock (_dataGate)
                {
                    var uid = achievements.UserUid ?? _account;
                    _library.Merge(uid, achievements.Records, achievements.ServerId, achievements.Source, achievements.CaptureSource);
                    ShowImportedAccount(uid);
                    RefreshView();
                }
                return $"成就 {achievements.Records.Count} 筆";
            case ImportKind.AchievementMapping:
                var achievementRows = _mappings.Import(true, ImportedFiles.ReadMapping(name, bytes, true));
                ReloadMappings();
                return $"成就對應 {achievementRows} 筆";
            default:
                var rewardRows = _mappings.Import(false, ImportedFiles.ReadMapping(name, bytes, false));
                ReloadMappings();
                return $"獎勵對應 {rewardRows} 筆";
        }
    }

    /// <summary>
    /// An import can belong to another account. The earlier session's pulls are
    /// already saved, so end it; otherwise a later refresh would write them here too.
    /// </summary>
    private void ShowImportedAccount(string? uid)
    {
        if (uid != _account)
        {
            _session = null;
            _typedUid = null;
            _partial.Clear();
            _warnings.Clear();
            _account = uid;
        }
    }

    private void OnMappingFileChanged(object sender, FileSystemEventArgs change)
    {
        // Editors raise several events per save, so wait for them to settle.
        if (change.Name is MappingStore.RewardsFile or MappingStore.AchievementsFile or MappingStore.EventsFile or MappingStore.ScheduleFile)
        {
            _mappingTimer.Change(MappingReloadDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void LoadMappings()
    {
        var result = _mappings.Load();
        MappingIssues = result.Issues;
        if (result.Catalog is { } catalog)
        {
            _catalog = catalog;
            Rewards = result.Rewards;
            Events = result.Events ?? EventTable.Empty;
            Schedule = result.Schedule ?? [];
            if (_session is { } session)
            {
                session.Catalog = catalog;
            }
        }
    }

    private void ReadPackets(IPacketCapture capture, HistorySession session, CancellationToken token)
    {
        var startedAt = DateTime.UtcNow;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (capture.Read() is { } packet)
                {
                    foreach (var page in session.Process(packet))
                    {
                        Log($"{PoolInfo.For(page.Kind).Name}：已擷取第 {page.Page} 頁");
                        _dirty = true;
                    }
                }
                if (DateTime.UtcNow - _lastRefresh >= RefreshInterval)
                {
                    _lastRefresh = DateTime.UtcNow;
                    if (Notice is null && session.OutboundCount > 0 && session.InboundCount == 0 && _lastRefresh - startedAt > InboundGracePeriod)
                    {
                        Notice = "只看得到送出的封包，收不到回應，可能是防火牆或防毒軟體阻擋了擷取。請允許此程式後再試一次。";
                    }
                    if (_dirty)
                    {
                        _dirty = false;
                        Ingest();
                    }
                    NotifyChanged();
                }
            }
        }
        catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException)
        {
            Fail($"擷取中斷：{exception.Message}");
            Status = AppStatus.Stopping;
        }
        finally
        {
            capture.Dispose();
            CloseDebugLog(session);
        }
    }

    private void OpenDebugLog(HistorySession session, string captureName)
    {
        try
        {
            session.DebugLog = PacketDebugLog.Create(Path.Combine(_baseFolder, PacketDebugLog.FolderName), captureName);
            Log($"除錯封包寫入：{session.DebugLog.Path}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Notice = $"無法建立除錯封包紀錄，這次擷取不會記錄封包：{exception.Message}";
        }
    }

    private void CloseDebugLog(HistorySession session)
    {
        if (session.DebugLog is not { } log)
        {
            return;
        }
        log.Dispose();
        Log(log.Failure is { } failure ? $"除錯封包紀錄中斷，已存 {log.EntryCount:N0} 個：{failure}"
            : log.Truncated ? $"除錯封包紀錄已達大小上限，只存了前 {log.EntryCount:N0} 個：{log.Path}"
            : $"除錯封包紀錄完成，共 {log.EntryCount:N0} 個：{log.Path}");
    }

    /// <summary>Moves what the current session decoded into the library and saves it.</summary>
    private void Ingest(string source = "live_capture")
    {
        lock (_dataGate)
        {
            if (_session is { } session)
            {
                _account = _typedUid ?? session.UserUid ?? _sessionAccount;
                var captureName = CaptureName;
                foreach (var history in session.BuildHistories())
                {
                    _warnings[history.Pool.Kind] = history.Warnings;
                    if (history.Warnings.Any(warning => warning.Code == MissedFirstPage))
                    {
                        _partial[history.Pool.Kind] = history;
                        TrySave(() => _library.SavePartial(_account, history, _library.ContextFor(_account) with { Source = source, CaptureSource = captureName }, _sessionStamp));
                    }
                    else
                    {
                        _partial.Remove(history.Pool.Kind);
                        _library.Merge(_account, history.Pool.Kind, history.Rows, session.ServerId, source, captureName, _catalog);
                    }
                }
                if (session.Achievements.Count > 0)
                {
                    _library.Merge(_account, session.Achievements, session.ServerId, source, captureName);
                }
            }
            RefreshView();
        }
        Persist();
    }

    private void Persist()
    {
        lock (_dataGate)
        {
            TrySave(() =>
            {
                if (_library.Save(_catalog) > 0)
                {
                    LastSaved = DateTime.Now;
                }
            });
        }
    }

    private void TrySave(Action save)
    {
        try
        {
            save();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Error = $"無法寫入 {_library.Folder}：{exception.Message}";
        }
    }

    private void RefreshView()
    {
        var saved = _library.Histories(_account);
        var kinds = saved.Select(history => history.Pool.Kind).Concat(_partial.Keys).Distinct().Order();
        Histories = kinds.Select(kind =>
        {
            var rows = saved.FirstOrDefault(history => history.Pool.Kind == kind)?.Rows ?? [];
            if (_partial.TryGetValue(kind, out var partial))
            {
                var known = rows.Select(row => row.Uid).ToHashSet();
                rows = partial.Rows.Where(row => !known.Contains(row.Uid)).Concat(rows)
                    .OrderByDescending(row => row.TimestampDecoded, StringComparer.Ordinal).ToList();
            }
            return new PoolHistory(PoolInfo.For(kind), rows, _warnings.GetValueOrDefault(kind) ?? [], []);
        }).ToList();
        Achievements = _library.AchievementsOf(_account);
        SeenRewards = Histories.SelectMany(history => history.Rows).Where(row => row.RewardId.Length > 0)
            .GroupBy(row => row.RewardId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        // A Mystery Box pool is not an event with a featured reward to set.
        SeenEvents = Histories.Where(history => history.Pool.Kind != HistoryKind.MysteryBox)
            .SelectMany(history => history.Rows).Where(row => !string.IsNullOrEmpty(row.EventId))
            .GroupBy(row => row.EventId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => new EventUsage(group.Count(), group.Max(row => row.TimestampDecoded)), StringComparer.OrdinalIgnoreCase);
    }

    private void BeginSession()
    {
        lock (_dataGate)
        {
            Error = null;
            Notice = null;
            CaptureName = null;
            FallbackReason = null;
            _session = null;
            _partial.Clear();
            _warnings.Clear();
            _sessionStamp = DateTime.Now;
            RefreshView();
        }
    }

    private void Fail(string message)
    {
        Error = message;
        Log(message);
        NotifyChanged();
    }

    private void Log(string message)
    {
        lock (_gate)
        {
            _activity.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
            if (_activity.Count > MaxActivityLines)
            {
                _activity.RemoveAt(_activity.Count - 1);
            }
        }
    }

    private void NotifyChanged() => Changed?.Invoke();
}
