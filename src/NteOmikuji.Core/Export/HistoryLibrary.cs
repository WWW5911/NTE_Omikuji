using NteOmikuji.Core.Import;
using NteOmikuji.Core.Mapping;
using NteOmikuji.Core.Protocol;

namespace NteOmikuji.Core.Export;

/// <summary>
/// The cumulative per-account history in user_data. The game only shows recent
/// pulls, so rows merge by their stable UID and older pulls are never lost.
/// </summary>
public sealed class HistoryLibrary(string folder)
{
    private const string AchievementsLabel = "Achievements";
    private const string PartialMarker = "_partial_";
    private const string SettingsFile = "settings.json";

    private sealed class Account(string uid)
    {
        public string Uid { get; } = uid;
        public string? ServerId { get; set; }
        public string Source { get; set; } = "live_capture";
        public string? CaptureSource { get; set; }
        public Dictionary<HistoryKind, List<HistoryRow>> Rows { get; } = [];
        public List<AchievementRecord> Achievements { get; set; } = [];
        public DateTime LastChanged { get; set; }
    }

    private readonly Dictionary<string, Account> _accounts = [];
    private readonly Dictionary<string, string> _writtenText = [];
    private readonly HashSet<string> _unreadable = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(string Uid, string Label)> _dirty = [];
    private readonly HashSet<string> _backedUp = new(StringComparer.OrdinalIgnoreCase);

    public const int BackupsPerFile = 10;

    public string Folder { get; } = folder;

    public string BackupFolder => Path.Combine(Folder, "backups");

    public IReadOnlyList<string> Accounts =>
        _accounts.Values.OrderByDescending(account => account.LastChanged).Select(account => account.Uid).ToList();

    /// <summary>Reads earlier exports. Returns one message per file that could not be read.</summary>
    public IReadOnlyList<string> Load(RewardCatalog catalog)
    {
        var issues = new List<string>();
        Directory.CreateDirectory(Folder);
        foreach (var path in Directory.EnumerateFiles(Folder, "*.json").Where(path => !Path.GetFileName(path).Contains(PartialMarker) && Path.GetFileName(path) != SettingsFile))
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var changed = File.GetLastWriteTimeUtc(path);
                switch (ExportDocuments.Classify(path, bytes))
                {
                    case ImportKind.HistoryExport:
                        var history = ExportDocuments.ReadHistory(bytes);
                        Merge(history.UserUid, history.Banner.Kind, history.Rows, history.ServerId, history.Source, history.CaptureSource, catalog, markDirty: false);
                        Find(history.UserUid).LastChanged = Later(Find(history.UserUid).LastChanged, changed);
                        break;
                    case ImportKind.AchievementExport:
                        var achievements = ExportDocuments.ReadAchievements(bytes);
                        Merge(achievements.UserUid, achievements.Records, achievements.ServerId, achievements.Source, achievements.CaptureSource, markDirty: false);
                        Find(achievements.UserUid).LastChanged = Later(Find(achievements.UserUid).LastChanged, changed);
                        break;
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException or IOException)
            {
                _unreadable.Add(path);
                issues.Add($"{Path.GetFileName(path)} 無法讀取：{exception.Message}");
            }
        }
        return issues;
    }

    public void Merge(string? uid, HistoryKind kind, IReadOnlyList<HistoryRow> rows, string? serverId, string? source, string? captureSource, RewardCatalog catalog, bool markDirty = true)
    {
        var account = Find(uid);
        Remember(account, serverId, source, captureSource);
        var existing = account.Rows.GetValueOrDefault(kind) ?? [];
        var incoming = rows.Select(row => row.Uid).ToHashSet();
        var incomingPerSecond = rows.GroupBy(row => row.TimestampDecoded).ToDictionary(group => group.Key, group => group.Count());
        var existingPerSecond = existing.GroupBy(row => row.TimestampDecoded).ToDictionary(group => group.Key, group => group.Count());
        // Regrouping a batch split across pages changes its UIDs, so rows from a second the
        // new data fully covers are replaced. Fewer new rows may be a cut-off scan, so those stay.
        bool IsReplaced(HistoryRow row) =>
            incoming.Contains(row.Uid)
            || (row.TimestampDecoded.Length > 0 && incomingPerSecond.GetValueOrDefault(row.TimestampDecoded) >= existingPerSecond[row.TimestampDecoded]);
        // New rows come first so a stable sort keeps the decoder's order within the same second.
        account.Rows[kind] = rows.Concat(existing.Where(row => !IsReplaced(row)))
            .OrderByDescending(row => row.TimestampDecoded, StringComparer.Ordinal)
            .ToList();
        foreach (var row in account.Rows[kind])
        {
            Apply(row, kind, catalog);
        }
        if (markDirty)
        {
            account.LastChanged = DateTime.UtcNow;
            _dirty.Add((account.Uid, BannerInfo.For(kind).FilePrefix));
        }
    }

    /// <summary>
    /// Adds rows from another tool only at times saved history has none, so what this app captured stays as
    /// it was. A later capture of such a time replaces them through <see cref="Merge(string?, HistoryKind, IReadOnlyList{HistoryRow}, string?, string?, string?, RewardCatalog, bool)"/>.
    /// </summary>
    public (int Added, int Skipped) Fill(string? uid, HistoryKind kind, IReadOnlyList<HistoryRow> rows, RewardCatalog catalog)
    {
        var account = Find(uid);
        var existing = account.Rows.GetValueOrDefault(kind) ?? [];
        var known = existing.Select(row => row.TimestampDecoded).ToHashSet(StringComparer.Ordinal);
        var added = rows.Where(row => !known.Contains(row.TimestampDecoded)).ToList();
        if (added.Count == 0)
        {
            return (0, rows.Count);
        }
        account.Rows[kind] = existing.Concat(added).OrderByDescending(row => row.TimestampDecoded, StringComparer.Ordinal).ToList();
        foreach (var row in added)
        {
            Apply(row, kind, catalog);
        }
        account.LastChanged = DateTime.UtcNow;
        _dirty.Add((account.Uid, BannerInfo.For(kind).FilePrefix));
        return (added.Count, rows.Count - added.Count);
    }

    public void Merge(string? uid, IReadOnlyList<AchievementRecord> records, string? serverId, string? source, string? captureSource, bool markDirty = true)
    {
        var account = Find(uid);
        Remember(account, serverId, source, captureSource);
        var incoming = records.Select(record => record.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        account.Achievements = [.. records, .. account.Achievements.Where(record => !incoming.Contains(record.Id))];
        if (markDirty)
        {
            account.LastChanged = DateTime.UtcNow;
            _dirty.Add((account.Uid, AchievementsLabel));
        }
    }

    /// <summary>Re-resolves every saved row, so a mapping fix also corrects history already on disk.</summary>
    public void ApplyCatalog(RewardCatalog catalog)
    {
        foreach (var account in _accounts.Values)
        {
            foreach (var (kind, rows) in account.Rows)
            {
                rows.ForEach(row => Apply(row, kind, catalog));
                _dirty.Add((account.Uid, BannerInfo.For(kind).FilePrefix));
            }
            if (account.Achievements.Count > 0)
            {
                _dirty.Add((account.Uid, AchievementsLabel));
            }
        }
    }

    public void SetServer(string? uid, string? serverId)
    {
        var account = Find(uid);
        if (account.ServerId == serverId)
        {
            return;
        }
        account.ServerId = serverId;
        foreach (var kind in account.Rows.Keys)
        {
            _dirty.Add((account.Uid, BannerInfo.For(kind).FilePrefix));
        }
        if (account.Achievements.Count > 0)
        {
            _dirty.Add((account.Uid, AchievementsLabel));
        }
    }

    public string? ServerOf(string? uid) => _accounts.GetValueOrDefault(Key(uid))?.ServerId;

    public IReadOnlyList<BannerHistory> Histories(string? uid)
    {
        var account = _accounts.GetValueOrDefault(Key(uid));
        return account is null
            ? []
            : Enum.GetValues<HistoryKind>().Where(account.Rows.ContainsKey)
                .Select(kind => new BannerHistory(BannerInfo.For(kind), account.Rows[kind], [], []))
                .ToList();
    }

    public IReadOnlyList<AchievementRecord> AchievementsOf(string? uid) => _accounts.GetValueOrDefault(Key(uid))?.Achievements ?? [];

    public ExportContext ContextFor(string? uid)
    {
        var account = _accounts.GetValueOrDefault(Key(uid));
        return new ExportContext(account?.Source ?? "live_capture", account?.CaptureSource, Key(uid), account?.ServerId);
    }

    /// <summary>Writes changed files. Returns how many were written.</summary>
    public int Save(RewardCatalog catalog)
    {
        Directory.CreateDirectory(Folder);
        var written = 0;
        foreach (var (uid, label) in _dirty.ToList())
        {
            var account = _accounts[uid];
            var document = label == AchievementsLabel
                ? ExportWriter.BuildAchievements(account.Achievements, catalog, ContextFor(uid))
                : ExportWriter.BuildHistory(
                    Histories(uid).Single(history => history.Banner.FilePrefix == label),
                    ContextFor(uid));
            if (WriteIfChanged(Path.Combine(Folder, ExportWriter.LibraryFileName(uid, label)), ExportWriter.ToJson(document), backUp: true))
            {
                written++;
            }
            _dirty.Remove((uid, label));
        }
        return written;
    }

    /// <summary>
    /// Saves a capture that missed page 1 on its own. Its timestamp ordinals may not
    /// match the real group, so merging could collide with the wrong saved pull.
    /// </summary>
    public string SavePartial(string? uid, BannerHistory history, ExportContext context, DateTime stamp)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, ExportWriter.FileName(uid, history.Banner.FilePrefix + PartialMarker.TrimEnd('_'), stamp));
        WriteIfChanged(path, ExportWriter.ToJson(ExportWriter.BuildHistory(history, context)));
        return path;
    }

    /// <summary>
    /// Copies a file aside before this session first changes it, so one bad capture or
    /// mapping edit can be undone. A failed copy stops the save rather than risking the only copy.
    /// </summary>
    private void BackUp(string path)
    {
        if (!_backedUp.Add(path) || !File.Exists(path))
        {
            return;
        }
        Directory.CreateDirectory(BackupFolder);
        var name = Path.GetFileNameWithoutExtension(path);
        File.Copy(path, Path.Combine(BackupFolder, $"{name}.{DateTime.Now:yyyyMMdd-HHmmss}.json"), overwrite: true);
        foreach (var old in Directory.EnumerateFiles(BackupFolder, $"{name}.*.json").Order(StringComparer.Ordinal).SkipLast(BackupsPerFile))
        {
            File.Delete(old);
        }
    }

    private bool WriteIfChanged(string path, string text, bool backUp = false)
    {
        if (_writtenText.GetValueOrDefault(path) == text)
        {
            return false;
        }
        if (backUp && !_unreadable.Contains(path))
        {
            // Loading can re-save a file unchanged, which needs neither a write nor a backup.
            if (File.Exists(path) && File.ReadAllText(path) == text)
            {
                _writtenText[path] = text;
                return false;
            }
            BackUp(path);
        }
        if (_unreadable.Remove(path) && File.Exists(path))
        {
            // Keep a file we could not parse rather than replacing what may be hand-edited data.
            File.Move(path, path + ".unreadable", overwrite: true);
        }
        AtomicFile.WriteAllText(path, text);
        _writtenText[path] = text;
        return true;
    }

    private static void Apply(HistoryRow row, HistoryKind kind, RewardCatalog catalog)
    {
        var isArc = kind == HistoryKind.ArcMiracleBox;
        var meta = catalog.Find(row.RewardId);
        row.IsMappedReward = meta is not null;
        row.Focus = catalog.FocusOf(row.RewardId, row.BannerId);
        row.RewardId = meta?.Id ?? row.RewardId;
        row.RewardName = meta?.Name ?? (isArc ? "UNKNOWN" : "");
        row.RewardRank = meta?.Rank ?? (isArc ? "" : null);
        row.RewardType = meta?.Type ?? (isArc ? "arc" : RewardCatalog.InferType(row.RewardId));
    }

    private static void Remember(Account account, string? serverId, string? source, string? captureSource)
    {
        account.ServerId = string.IsNullOrWhiteSpace(serverId) ? account.ServerId : serverId;
        account.Source = string.IsNullOrWhiteSpace(source) ? account.Source : source;
        account.CaptureSource = string.IsNullOrWhiteSpace(captureSource) ? account.CaptureSource : captureSource;
    }

    private static DateTime Later(DateTime a, DateTime b) => a > b ? a : b;

    private static string Key(string? uid) => string.IsNullOrWhiteSpace(uid) ? "" : uid.Trim();

    private Account Find(string? uid)
    {
        var key = Key(uid);
        if (!_accounts.TryGetValue(key, out var account))
        {
            account = new Account(key);
            _accounts[key] = account;
        }
        return account;
    }
}
