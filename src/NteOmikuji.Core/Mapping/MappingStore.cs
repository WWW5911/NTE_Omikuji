using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NteOmikuji.Core.Protocol;

namespace NteOmikuji.Core.Mapping;

/// <param name="Catalog">Null when a file could not be used, so the caller keeps its previous mappings.</param>
public sealed record MappingLoadResult(RewardCatalog? Catalog, RewardTable Rewards, IReadOnlyList<string> Issues, EventTable? Events = null, IReadOnlyList<ScheduledEvent>? Schedule = null);

/// <summary>Owns the user-editable mapping CSVs in a folder beside the app.</summary>
public sealed class MappingStore(string folder)
{
    public const string RewardsFile = "rewards.csv";
    public const string AchievementsFile = "achievements.csv";
    public const string EventsFile = "events.csv";

    /// <summary>What the event table was called in earlier versions.</summary>
    private const string FormerEventsFile = "banners.csv";
    public const string ScheduleFile = "schedule.csv";

    /// <summary>Where the last downloaded copies are kept, to tell what changed upstream from what was edited here.</summary>
    private const string UpstreamFolder = "upstream";

    public static readonly IReadOnlyList<string> UpdatableFiles = [RewardsFile, AchievementsFile, EventsFile, ScheduleFile];

    /// <summary>The single-language rewards.csv from version 0.1, which holds no user edits if it still matches.</summary>
    private const string UneditedLegacySeedHash = "03d148c13979026e08eab92fe5390812e5b139be301d4d002945415e713c05d8";
    private const int MaxUndoSteps = 50;

    private static readonly string[] AchievementColumns = ["id", "name", "description", "quality", "target", "rewards"];

    private readonly Stack<(string Path, byte[] Bytes)> _undo = new();
    private string _lastLoaded = "";

    public string Folder { get; } = folder;

    public bool CanUndo => _undo.Count > 0;

    /// <summary>Creates missing files and upgrades an old rewards.csv. Existing values are never replaced.</summary>
    public void EnsureSeeded()
    {
        Directory.CreateDirectory(Folder);
        var formerEvents = Path.Combine(Folder, FormerEventsFile);
        var events = Path.Combine(Folder, EventsFile);
        if (File.Exists(formerEvents) && !File.Exists(events))
        {
            File.Move(formerEvents, events);
        }
        foreach (var name in new[] { RewardsFile, AchievementsFile, EventsFile, ScheduleFile })
        {
            var path = Path.Combine(Folder, name);
            if (!File.Exists(path))
            {
                using var seed = RewardCatalog.OpenSeed(name);
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                seed.CopyTo(file);
            }
        }
        UpgradeLegacyRewards();
    }

    public MappingLoadResult Load()
    {
        var issues = new List<string>();
        try
        {
            var rewards = ReadTable(RewardsFile);
            var events = ReadEvents();
            var catalog = RewardCatalog.FromTables(rewards, ReadTable(AchievementsFile), issues, events);
            var schedule = EventSchedule.Parse(ReadSchedule(), ScheduleFile, issues);
            _lastLoaded = Fingerprint();
            return new MappingLoadResult(catalog, ToRewardTable(rewards), issues, ToEventTable(events), schedule);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            issues.Add($"對應檔未套用，繼續使用上一版：{exception.Message}");
            return new MappingLoadResult(null, RewardTable.Empty, issues);
        }
    }

    /// <summary>False when the files still match what was last loaded, such as after this app's own save.</summary>
    public bool HasExternalChanges()
    {
        try
        {
            return Fingerprint() != _lastLoaded;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Writes one reward. A blank name, rank, or flag clears that cell; other columns are kept.</summary>
    public void SaveReward(RewardEntry entry)
    {
        var id = entry.Id.Trim();
        if (id.Length == 0)
        {
            throw new InvalidDataException("ID 不能是空白。");
        }
        var table = ReadRewards();
        foreach (var column in entry.Names.Keys.Select(RewardSchema.ColumnFor).Where(column => !table.Has(column)))
        {
            table.Headers.Add(column);
        }
        if (!table.Has(RewardSchema.FocusColumn))
        {
            table.Headers.Add(RewardSchema.FocusColumn);
        }
        var row = table.Rows.FirstOrDefault(row => row.Get("id").Equals(id, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            row = new CsvRow(0, new() { ["id"] = id });
            table.Rows.Add(row);
        }
        row.Values["type"] = entry.Type.Trim();
        row.Values["rank"] = entry.Rank.Trim();
        row.Values[RewardSchema.FocusColumn] = entry.FocusOverride is { } focus ? RewardSchema.FocusCode(focus) : "";
        foreach (var locale in RewardSchema.Locales(table))
        {
            row.Values[RewardSchema.ColumnFor(locale)] = entry.Names.GetValueOrDefault(locale, "").Trim();
        }
        WriteRewards(table);
    }

    /// <summary>Writes one event's featured rewards, keeping any other columns.</summary>
    public void SaveEvent(string id, IEnumerable<string> featured)
    {
        var key = id.Trim();
        if (key.Length == 0)
        {
            throw new InvalidDataException("活動 ID 不能是空白。");
        }
        var table = ReadEvents();
        var row = table.Rows.FirstOrDefault(row => row.Get("id").Equals(key, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            row = new CsvRow(0, new() { ["id"] = key });
            table.Rows.Add(row);
        }
        row.Values[EventEntry.FeaturedColumn] = EventEntry.JoinFeatured(featured);
        Write(EventsFile, table);
    }

    public void DeleteEvent(string id)
    {
        var table = ReadEvents();
        if (table.Rows.RemoveAll(row => row.Get("id").Equals(id, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            Write(EventsFile, table);
        }
    }

    public void DeleteReward(string id)
    {
        var table = ReadRewards();
        if (table.Rows.RemoveAll(row => row.Get("id").Equals(id, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            WriteRewards(table);
        }
    }

    /// <summary>Adds an empty language column.</summary>
    public void AddLocale(string locale)
    {
        var code = locale.Trim().ToLowerInvariant();
        if (!RewardSchema.IsValidLocale(code))
        {
            throw new InvalidDataException($"「{locale}」不是語言代碼，例如 ja、pt-br。");
        }
        var table = ReadRewards();
        if (!table.Has(RewardSchema.ColumnFor(code)))
        {
            table.Headers.Add(RewardSchema.ColumnFor(code));
            WriteRewards(table);
        }
    }

    /// <summary>Adds rows and fills blank cells from the bundled data, leaving every filled cell alone.</summary>
    public int FillFromBundled()
    {
        var table = ReadRewards();
        var changed = MergeBlanks(table, ReadSeedRewards());
        if (changed > 0)
        {
            WriteRewards(table);
        }
        var events = ReadEvents();
        var changedEvents = MergeBlanks(events, ReadSeed(EventsFile));
        if (changedEvents > 0)
        {
            Write(EventsFile, events);
        }
        var schedule = ReadSchedule();
        var changedSchedule = MergeBlanks(schedule, ReadSeed(ScheduleFile));
        if (changedSchedule > 0)
        {
            Write(ScheduleFile, schedule);
        }
        return changed + changedEvents + changedSchedule;
    }

    /// <summary>
    /// Takes what changed upstream since the last update, so a corrected date or rank arrives while anything edited
    /// here stays. Every download is checked before any file is written. Returns how many rows and cells changed.
    /// </summary>
    public int ApplyUpdate(IReadOnlyDictionary<string, byte[]> downloads)
    {
        var incoming = downloads.ToDictionary(file => file.Key, file => ParseDownload(file.Key, file.Value));
        var changed = 0;
        foreach (var (name, upstream) in incoming)
        {
            var table = ReadUpdatable(name);
            var lastTaken = ReadLastTaken(name);
            var baseline = lastTaken ?? ReadSeed(name);
            if (name == RewardsFile)
            {
                RewardSchema.Normalize(upstream);
                RewardSchema.Normalize(baseline);
            }
            var cells = MergeUpstream(table, upstream, baseline, lastTaken is not null);
            if (cells > 0)
            {
                Write(name, table);
                changed += cells;
            }
            Directory.CreateDirectory(Path.Combine(Folder, UpstreamFolder));
            AtomicFile.WriteAllText(Path.Combine(Folder, UpstreamFolder, name), upstream.ToText(), withBom: true);
        }
        return changed;
    }

    private static CsvTable ParseDownload(string name, byte[] bytes)
    {
        var table = CsvTable.Parse(CsvTable.DecodeText(bytes));
        if (!table.Has("id") || table.Rows.Count == 0)
        {
            throw new InvalidDataException($"下載的 {name} 不是對應檔，沒有套用任何更新。");
        }
        return table;
    }

    private CsvTable ReadUpdatable(string name) => name switch
    {
        RewardsFile => ReadRewards(),
        EventsFile => ReadEvents(),
        ScheduleFile => ReadSchedule(),
        _ => ReadTable(name),
    };

    private CsvTable? ReadLastTaken(string name)
    {
        var path = Path.Combine(Folder, UpstreamFolder, name);
        try
        {
            return File.Exists(path) ? CsvTable.Parse(CsvTable.DecodeText(ReadShared(path))) : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static int MergeUpstream(CsvTable table, CsvTable upstream, CsvTable baseline, bool baselineWasTaken)
    {
        foreach (var header in upstream.Headers.Where(header => !table.Has(header)))
        {
            table.Headers.Add(header);
        }
        var existing = new Dictionary<string, CsvRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in table.Rows)
        {
            existing[row.Get("id")] = row;
        }
        var known = new Dictionary<string, CsvRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in baseline.Rows)
        {
            known[row.Get("id")] = row;
        }
        var changed = 0;
        foreach (var row in upstream.Rows.Where(row => row.Get("id").Length > 0))
        {
            var was = known.GetValueOrDefault(row.Get("id"));
            if (!existing.TryGetValue(row.Get("id"), out var current))
            {
                // A row the last update brought and this folder no longer has was deleted here.
                if (was is null || !baselineWasTaken)
                {
                    table.Rows.Add(new CsvRow(0, new(row.Values)));
                    changed++;
                }
                continue;
            }
            foreach (var (column, value) in row.Values.Where(cell => cell.Key != "id"))
            {
                var next = value.Trim();
                var now = current.Get(column);
                // With no earlier download a blank may just be missing. After one, the player cleared it.
                // A blank category follows the rank and ID, so a cleared one always stays cleared.
                var fillsBlank = !baselineWasTaken && now.Length == 0 && column != RewardSchema.FocusColumn;
                if (next != now && (now == (was?.Get(column) ?? "") || fillsBlank))
                {
                    current.Values[column] = next;
                    changed++;
                }
            }
        }
        return changed;
    }

    public bool Undo()
    {
        if (!_undo.TryPop(out var step))
        {
            return false;
        }
        var temporary = $"{step.Path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, step.Bytes);
            File.Move(temporary, step.Path, overwrite: true);
        }
        catch
        {
            _undo.Push(step);
            throw;
        }
        finally
        {
            File.Delete(temporary);
        }
        return true;
    }

    /// <summary>Adds or updates rows from a CSV or JSON mapping file, keeping every other row and column.</summary>
    public int Import(bool isAchievements, CsvTable incoming)
    {
        var name = isAchievements ? AchievementsFile : RewardsFile;
        var table = ReadTable(name);
        if (!isAchievements)
        {
            RewardSchema.Normalize(table);
            RewardSchema.Normalize(incoming);
        }
        foreach (var header in incoming.Headers.Where(header => !table.Has(header)))
        {
            table.Headers.Add(header);
        }
        var existing = new Dictionary<string, CsvRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in table.Rows)
        {
            existing[row.Get("id")] = row;
        }
        var changed = 0;
        foreach (var row in incoming.Rows.Where(row => row.Get("id").Length > 0))
        {
            if (existing.TryGetValue(row.Get("id"), out var current))
            {
                foreach (var (column, value) in row.Values)
                {
                    current.Values[column] = value;
                }
            }
            else
            {
                existing[row.Get("id")] = row;
                table.Rows.Add(row);
            }
            changed++;
        }
        Write(name, table);
        return changed;
    }

    public static CsvTable TableFromJson(JsonElement root, bool isAchievements)
    {
        var rows = new List<Dictionary<string, string>>();
        foreach (var entry in root.EnumerateObject())
        {
            var hasNames = entry.Value.ValueKind == JsonValueKind.Object && entry.Value.TryGetProperty("names", out var localized) && localized.ValueKind == JsonValueKind.Object;
            if (entry.Value.ValueKind != JsonValueKind.Object || (!entry.Value.TryGetProperty("name", out _) && !hasNames))
            {
                throw new InvalidDataException($"項目 {entry.Name} 缺少 name 欄位。");
            }
            var values = new Dictionary<string, string> { ["id"] = entry.Name };
            if (entry.Value.TryGetProperty("name", out _))
            {
                values["name"] = Text(entry.Value, "name");
            }
            if (isAchievements)
            {
                values["description"] = Text(entry.Value, "description");
                values["quality"] = Text(entry.Value, "quality");
                values["target"] = entry.Value.TryGetProperty("target", out var target) ? target.GetRawText() : "0";
                values["rewards"] = entry.Value.TryGetProperty("rewards", out var rewards) && rewards.ValueKind == JsonValueKind.Array
                    ? string.Join(';', rewards.EnumerateArray().Select(reward => $"{reward.GetProperty("item_id").GetString()}:{reward.GetProperty("amount").GetRawText()}"))
                    : "";
            }
            else
            {
                var type = Text(entry.Value, "type");
                values["type"] = type.Length > 0 ? type : RewardCatalog.InferType(entry.Name);
                values["rank"] = Text(entry.Value, "rank");
                if (entry.Value.TryGetProperty(RewardSchema.FocusColumn, out var focus) && focus.ValueKind == JsonValueKind.String)
                {
                    values[RewardSchema.FocusColumn] = focus.GetString()!;
                }
                if (hasNames)
                {
                    foreach (var name in entry.Value.GetProperty("names").EnumerateObject().Where(name => name.Value.ValueKind == JsonValueKind.String))
                    {
                        var locale = name.Name.ToLowerInvariant();
                        if (!RewardSchema.IsValidLocale(locale))
                        {
                            throw new InvalidDataException($"項目 {entry.Name} 的語言代碼「{name.Name}」無效。");
                        }
                        values[RewardSchema.ColumnFor(locale)] = name.Value.GetString()!;
                    }
                }
            }
            rows.Add(values);
        }

        var headers = isAchievements
            ? AchievementColumns.ToList()
            : new[] { "id", "type", "rank" }.Concat(rows.SelectMany(row => row.Keys).Distinct().Where(key => key is not ("id" or "type" or "rank"))).ToList();
        var table = new CsvTable(headers);
        table.Rows.AddRange(rows.Select(values => new CsvRow(0, values)));
        return table;
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    private static RewardTable ToRewardTable(CsvTable rewards)
    {
        var locales = RewardSchema.Locales(rewards);
        return new RewardTable(locales, rewards.Rows.Where(row => row.Get("id").Length > 0).Select(row => RewardSchema.ToEntry(row, locales, out _)).ToList());
    }

    private static EventTable ToEventTable(CsvTable events) =>
        new(events.Rows.Where(row => row.Get("id").Length > 0)
            .Select(row => new EventEntry(row.Get("id"), EventEntry.ParseFeatured(row.Get(EventEntry.FeaturedColumn))))
            .ToList());

    private static int MergeBlanks(CsvTable table, CsvTable seed)
    {
        foreach (var header in seed.Headers.Where(header => !table.Has(header)))
        {
            table.Headers.Add(header);
        }
        var existing = new Dictionary<string, CsvRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in table.Rows)
        {
            existing[row.Get("id")] = row;
        }
        var changed = 0;
        foreach (var row in seed.Rows)
        {
            if (!existing.TryGetValue(row.Get("id"), out var current))
            {
                table.Rows.Add(new CsvRow(0, new(row.Values)));
                changed++;
                continue;
            }
            // A blank category follows the rank and ID, so filling it would only freeze today's answer.
            foreach (var (column, value) in row.Values.Where(cell => cell.Key != RewardSchema.FocusColumn && cell.Value.Length > 0))
            {
                if (current.Get(column).Length == 0)
                {
                    current.Values[column] = value;
                    changed++;
                }
            }
        }
        return changed;
    }

    private void UpgradeLegacyRewards()
    {
        var path = Path.Combine(Folder, RewardsFile);
        try
        {
            var bytes = ReadShared(path);
            var table = CsvTable.Parse(CsvTable.DecodeText(bytes));
            if (!RewardSchema.IsLegacy(table))
            {
                return;
            }
            var backup = path + (RewardSchema.HasLegacyNames(table) ? ".v1.bak" : ".v2.bak");
            if (!File.Exists(backup))
            {
                File.WriteAllBytes(backup, bytes);
            }
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) == UneditedLegacySeedHash)
            {
                table = ReadSeedRewards();
            }
            else
            {
                RewardSchema.Normalize(table);
                MergeBlanks(table, ReadSeedRewards());
            }
            AtomicFile.WriteAllText(path, table.ToText(), withBom: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Locked by Excel or malformed: Load reads the old layout and reports problems itself.
        }
    }

    private static CsvTable ReadSeedRewards() => ReadSeed(RewardsFile);

    private static CsvTable ReadSeed(string name)
    {
        using var seed = RewardCatalog.OpenSeed(name);
        using var buffer = new MemoryStream();
        seed.CopyTo(buffer);
        return CsvTable.Parse(CsvTable.DecodeText(buffer.ToArray()));
    }

    private CsvTable ReadEvents()
    {
        var table = File.Exists(Path.Combine(Folder, EventsFile)) ? ReadTable(EventsFile) : new CsvTable(["id"]);
        if (!table.Has(EventEntry.FeaturedColumn))
        {
            table.Headers.Add(EventEntry.FeaturedColumn);
        }
        return table;
    }

    private CsvTable ReadSchedule() =>
        File.Exists(Path.Combine(Folder, ScheduleFile)) ? ReadTable(ScheduleFile) : new CsvTable(["id"]);

    private CsvTable ReadRewards()
    {
        var table = ReadTable(RewardsFile);
        RewardSchema.Normalize(table);
        return table;
    }

    private void WriteRewards(CsvTable table) => Write(RewardsFile, table);

    private void Write(string name, CsvTable table)
    {
        var path = Path.Combine(Folder, name);
        var before = ReadShared(path);
        AtomicFile.WriteAllText(path, table.ToText(), withBom: true);
        _undo.Push((path, before));
        if (_undo.Count > MaxUndoSteps)
        {
            var kept = _undo.Take(MaxUndoSteps).Reverse().ToList();
            _undo.Clear();
            kept.ForEach(_undo.Push);
        }
    }

    private string Fingerprint()
    {
        byte[] Optional(string name) => File.Exists(Path.Combine(Folder, name)) ? ReadShared(Path.Combine(Folder, name)) : [];
        return Convert.ToHexStringLower(SHA256.HashData([.. ReadShared(Path.Combine(Folder, RewardsFile)), .. ReadShared(Path.Combine(Folder, AchievementsFile)), .. Optional(EventsFile), .. Optional(ScheduleFile)]));
    }

    private CsvTable ReadTable(string name)
    {
        try
        {
            return CsvTable.Parse(CsvTable.DecodeText(ReadShared(Path.Combine(Folder, name))));
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException($"{name}：{exception.Message}", exception);
        }
    }

    // Excel holds its file open while editing, so reads must allow other writers.
    private static byte[] ReadShared(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        file.CopyTo(buffer);
        return buffer.ToArray();
    }
}

public static class AtomicFile
{
    /// <summary>Replaces a file in one step so a crash or full disk cannot leave it half written.</summary>
    public static void WriteAllText(string path, string text, bool withBom = false)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(withBom));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
