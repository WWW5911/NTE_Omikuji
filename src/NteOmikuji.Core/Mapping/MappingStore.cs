using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NteOmikuji.Core.Protocol;

namespace NteOmikuji.Core.Mapping;

/// <param name="Catalog">Null when a file could not be used, so the caller keeps its previous mappings.</param>
public sealed record MappingLoadResult(RewardCatalog? Catalog, RewardTable Rewards, IReadOnlyList<string> Issues);

/// <summary>Owns the user-editable mapping CSVs in a folder beside the app.</summary>
public sealed class MappingStore(string folder)
{
    public const string RewardsFile = "rewards.csv";
    public const string AchievementsFile = "achievements.csv";

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
        foreach (var name in new[] { RewardsFile, AchievementsFile })
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
            var catalog = RewardCatalog.FromTables(rewards, ReadTable(AchievementsFile), issues);
            _lastLoaded = Fingerprint();
            return new MappingLoadResult(catalog, ToRewardTable(rewards), issues);
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

    private static CsvTable ReadSeedRewards()
    {
        using var seed = RewardCatalog.OpenSeed(RewardsFile);
        using var buffer = new MemoryStream();
        seed.CopyTo(buffer);
        return CsvTable.Parse(CsvTable.DecodeText(buffer.ToArray()));
    }

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

    private string Fingerprint() =>
        Convert.ToHexStringLower(SHA256.HashData(ReadShared(Path.Combine(Folder, RewardsFile)).Concat(ReadShared(Path.Combine(Folder, AchievementsFile))).ToArray()));

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
