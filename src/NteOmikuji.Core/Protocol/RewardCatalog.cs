using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using NteOmikuji.Core.Mapping;

namespace NteOmikuji.Core.Protocol;

public sealed record RewardMeta(string Id, string Type, string? Rank, RewardFocus Focus, IReadOnlyDictionary<string, string> Names)
{
    /// <summary>The name written to exports. Trackers expect English, so it wins whenever present.</summary>
    public string Name => Localized(RewardSchema.English);

    /// <summary>Falls back to English, then Chinese, then any language, so a gap never hides a reward.</summary>
    public string Localized(string locale) =>
        Names.GetValueOrDefault(locale)
        ?? Names.GetValueOrDefault(RewardSchema.English)
        ?? Names.GetValueOrDefault(RewardSchema.Chinese)
        ?? Names.Values.FirstOrDefault()
        ?? "";
}

public sealed record AchievementMeta(string Name, string Description, string Quality, JsonElement Target, JsonElement Rewards);

/// <summary>
/// Optional display metadata. A reward missing from the catalog still decodes
/// and exports under the text the game sent; the catalog only adds a name.
/// </summary>
public sealed class RewardCatalog
{
    private readonly Dictionary<string, RewardMeta> _rewards = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AchievementMeta> _achievements = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _featured = new(StringComparer.OrdinalIgnoreCase);

    public static RewardCatalog Empty { get; } = new();

    public static Lazy<RewardCatalog> Bundled { get; } = new(LoadBundled);

    public int RewardCount => _rewards.Count;
    public int AchievementCount => _achievements.Count;

    public RewardMeta? Find(string rewardId) => _rewards.GetValueOrDefault(rewardId);

    /// <summary>A reward with no row is still sorted by its ID, so a new outfit counts before anyone maps it.</summary>
    public RewardFocus FocusOf(string rewardId) =>
        Find(rewardId)?.Focus ?? RewardSchema.InferFocus(rewardId, "", InferType(rewardId));

    /// <summary>
    /// The reward's category on one event. An event with a featured list decides what is
    /// limited there: its featured reward is limited however it is mapped, and a reward
    /// marked limited elsewhere is an ordinary S. Other events follow the mapping.
    /// </summary>
    public RewardFocus FocusOf(string rewardId, string? bannerId)
    {
        var focus = FocusOf(rewardId);
        if (bannerId is null || !_featured.TryGetValue(bannerId, out var featured) || featured.Count == 0)
        {
            return focus;
        }
        if (featured.Contains(rewardId))
        {
            var type = Find(rewardId)?.Type ?? InferType(rewardId);
            return type.Equals("arc", StringComparison.OrdinalIgnoreCase) ? RewardFocus.SLimitedArc : RewardFocus.SLimitedCharacter;
        }
        return focus switch
        {
            RewardFocus.SLimitedArc => RewardFocus.SArc,
            RewardFocus.SLimitedCharacter => RewardFocus.SCharacter,
            _ => focus,
        };
    }

    public AchievementMeta? FindAchievement(string achievementId) => _achievements.GetValueOrDefault(achievementId);

    public static string InferType(string rewardId)
    {
        if (rewardId.Length == 0)
        {
            return "";
        }
        if (rewardId.StartsWith("fork_", StringComparison.OrdinalIgnoreCase))
        {
            return "arc";
        }
        if (rewardId.All(char.IsDigit))
        {
            return "character";
        }
        return rewardId.StartsWith("fashion_", StringComparison.OrdinalIgnoreCase) ? "cosmetic" : "item";
    }

    public static Stream OpenSeed(string fileName) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream($"mappings.{fileName}")
        ?? throw new InvalidOperationException($"missing bundled mapping {fileName}");

    /// <summary>Builds a catalog, reporting bad rows in <paramref name="issues"/> instead of failing on them.</summary>
    public static RewardCatalog FromTables(CsvTable rewards, CsvTable achievements, List<string> issues, CsvTable? banners = null)
    {
        RewardSchema.Normalize(rewards);
        var locales = RewardSchema.Locales(rewards);
        if (!rewards.Has("id"))
        {
            throw new InvalidDataException($"{MappingStore.RewardsFile} 缺少 id 欄位。");
        }
        if (locales.Count == 0)
        {
            throw new InvalidDataException($"{MappingStore.RewardsFile} 缺少 name 欄位，例如 name.en。");
        }
        Require(achievements, MappingStore.AchievementsFile, "id", "name");

        var catalog = new RewardCatalog();
        foreach (var row in banners?.Rows ?? [])
        {
            if (row.Get("id").Length > 0)
            {
                catalog._featured[row.Get("id")] = new HashSet<string>(BannerEntry.ParseFeatured(row.Get(BannerEntry.FeaturedColumn)), StringComparer.OrdinalIgnoreCase);
            }
        }
        foreach (var row in rewards.Rows)
        {
            var entry = RewardSchema.ToEntry(row, locales, out var validFocus);
            if (entry.Id.Length == 0)
            {
                issues.Add($"{MappingStore.RewardsFile} 第 {row.Line} 行：缺少 id。");
                continue;
            }
            if (!validFocus)
            {
                issues.Add($"{MappingStore.RewardsFile} 第 {row.Line} 行：無法辨識關注類別「{row.Get(RewardSchema.FocusColumn)}」，已改用預設值。");
            }
            // A row with only an ID is a placeholder, so the reward still counts as unmapped.
            if (entry.Names.Count == 0 && entry.Rank.Length == 0 && entry.FocusOverride is null)
            {
                continue;
            }
            if (catalog._rewards.ContainsKey(entry.Id))
            {
                issues.Add($"{MappingStore.RewardsFile} 第 {row.Line} 行：{entry.Id} 重複，以這一行為準。");
            }
            var type = entry.Type.Length > 0 ? entry.Type : InferType(entry.Id);
            catalog._rewards[entry.Id] = new RewardMeta(entry.Id, type, entry.Rank.Length > 0 ? entry.Rank : null,
                entry.FocusOverride ?? RewardSchema.InferFocus(entry.Id, entry.Rank, type), entry.Names);
        }
        foreach (var row in achievements.Rows)
        {
            var id = row.Get("id");
            if (id.Length == 0 || row.Get("name").Length == 0)
            {
                continue;
            }
            try
            {
                catalog._achievements[id] = new AchievementMeta(
                    row.Get("name"), row.Get("description"), row.Get("quality"),
                    Number(row.Get("target").Length > 0 ? row.Get("target") : "0"), ParseRewards(row.Get("rewards")));
            }
            catch (FormatException exception)
            {
                issues.Add($"{MappingStore.AchievementsFile} 第 {row.Line} 行：{exception.Message}");
            }
        }
        return catalog;
    }

    private static void Require(CsvTable table, string file, params string[] columns)
    {
        foreach (var column in columns.Where(column => !table.Has(column)))
        {
            throw new InvalidDataException($"{file} 缺少 {column} 欄位。");
        }
    }

    /// <summary>Parses "item:10;other:5". Item IDs may hold any text, so only the last colon splits.</summary>
    private static JsonElement ParseRewards(string text)
    {
        var rewards = new JsonArray();
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = part.LastIndexOf(':');
            if (colon <= 0)
            {
                throw new FormatException($"獎勵「{part}」應為 物品:數量。");
            }
            rewards.Add(new JsonObject
            {
                ["item_id"] = part[..colon],
                ["amount"] = JsonNode.Parse(Number(part[(colon + 1)..]).GetRawText()),
            });
        }
        return JsonDocument.Parse(rewards.ToJsonString()).RootElement.Clone();
    }

    private static JsonElement Number(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new FormatException($"「{text}」不是數字。");
        }
        var json = long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole)
            ? whole.ToString(CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static RewardCatalog LoadBundled()
    {
        var issues = new List<string>();
        var catalog = FromTables(ReadSeed(MappingStore.RewardsFile), ReadSeed(MappingStore.AchievementsFile), issues, ReadSeed(MappingStore.BannersFile));
        return issues.Count == 0 ? catalog : throw new InvalidOperationException(string.Join("; ", issues));
    }

    private static CsvTable ReadSeed(string fileName)
    {
        using var stream = OpenSeed(fileName);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return CsvTable.Parse(CsvTable.DecodeText(buffer.ToArray()));
    }
}
