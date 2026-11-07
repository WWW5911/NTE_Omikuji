using System.Text.RegularExpressions;

namespace NteOmikuji.Core.Mapping;

/// <summary>One reward row. Names are keyed by lower-case locale; other languages are just more keys.</summary>
/// <param name="FocusOverride">The row's own category. Null means it follows <see cref="RewardSchema.InferFocus"/>.</param>
public sealed record RewardEntry(string Id, string Type, string Rank, RewardFocus? FocusOverride, IReadOnlyDictionary<string, string> Names)
{
    public RewardFocus Focus => FocusOverride ?? RewardSchema.InferFocus(Id, Rank, Type);
}

public sealed record RewardTable(IReadOnlyList<string> Locales, IReadOnlyList<RewardEntry> Entries)
{
    public static RewardTable Empty { get; } = new([], []);
}

/// <summary>One row of banners.csv. Limited status belongs to the event, since a reward featured on one can be off-banner on another.</summary>
public sealed record BannerEntry(string Id, IReadOnlyList<string> Featured)
{
    public const string FeaturedColumn = "featured";
    private const char Separator = ';';

    public static IReadOnlyList<string> ParseFeatured(string? text) =>
        (text ?? "").Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string JoinFeatured(IEnumerable<string> featured) => string.Join(Separator, featured);
}

public sealed record BannerTable(IReadOnlyList<BannerEntry> Entries)
{
    public static BannerTable Empty { get; } = new([]);
}

/// <summary>
/// The rewards.csv layout: <c>id,type,rank,focus,name.zh-hant,name.en,...</c>.
/// Adding a <c>name.xx</c> column adds a language, with no code or app update.
/// </summary>
public static partial class RewardSchema
{
    public const string NamePrefix = "name.";
    public const string FocusColumn = "focus";
    public const string Chinese = "zh-hant";
    public const string English = "en";
    private const string LegacyNameColumn = "name";
    private const string LegacyFlagColumn = "is_s";

    private static readonly Dictionary<RewardFocus, (string Code, string Label)> FocusNames = new()
    {
        [RewardFocus.None] = ("none", "不關注"),
        [RewardFocus.SCharacter] = ("s_character", "S 角色"),
        [RewardFocus.SArc] = ("s_arc", "S 弧盤"),
        [RewardFocus.SLimitedCharacter] = ("s_limited_character", "S 限定角色"),
        [RewardFocus.SLimitedArc] = ("s_limited_arc", "S 限定弧盤"),
        [RewardFocus.Outfit] = ("outfit", "角色服裝"),
        [RewardFocus.VehicleSkin] = ("vehicle_skin", "車皮"),
        [RewardFocus.Glider] = ("glider", "降落傘"),
    };

    [GeneratedRegex("^[a-z]{2,3}(-[a-z0-9]{2,8})*$")]
    private static partial Regex LocalePattern();

    [GeneratedRegex(@"\p{IsCJKUnifiedIdeographs}")]
    private static partial Regex HanPattern();

    public static IReadOnlyList<RewardFocus> AllFocus { get; } = Enum.GetValues<RewardFocus>();

    public static string FocusCode(RewardFocus focus) => FocusNames[focus].Code;

    public static string FocusLabel(RewardFocus focus) => FocusNames[focus].Label;

    /// <summary>
    /// Sorts rewards with no explicit category. Cosmetic IDs name their kind in the
    /// game's own prefix. Limited status has no such signal, so it is only ever set by hand.
    /// </summary>
    public static RewardFocus InferFocus(string id, string rank, string type)
    {
        if (id.StartsWith("fashion_glide_", StringComparison.OrdinalIgnoreCase))
        {
            return RewardFocus.Glider;
        }
        if (id.StartsWith("fashion_vehicle_", StringComparison.OrdinalIgnoreCase))
        {
            return RewardFocus.VehicleSkin;
        }
        if (id.StartsWith("fashion_", StringComparison.OrdinalIgnoreCase))
        {
            return RewardFocus.Outfit;
        }
        if (!rank.Equals("S", StringComparison.OrdinalIgnoreCase))
        {
            return RewardFocus.None;
        }
        return type.ToLowerInvariant() switch
        {
            "character" => RewardFocus.SCharacter,
            "arc" => RewardFocus.SArc,
            _ => RewardFocus.None,
        };
    }

    /// <summary>Accepts a code or the Chinese label, so either reads well in Excel. Blank means "not set".</summary>
    public static bool TryParseFocus(string text, out RewardFocus? value)
    {
        var wanted = text.Replace(" ", "").Trim();
        value = null;
        if (wanted.Length == 0)
        {
            return true;
        }
        foreach (var (focus, names) in FocusNames)
        {
            if (wanted.Equals(names.Code, StringComparison.OrdinalIgnoreCase) || wanted.Equals(names.Label.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
            {
                value = focus;
                return true;
            }
        }
        return false;
    }

    public static bool IsValidLocale(string locale) => LocalePattern().IsMatch(locale);

    public static string ColumnFor(string locale) => NamePrefix + locale;

    public static bool IsLegacy(CsvTable table) => table.Has(LegacyNameColumn) || table.Has(LegacyFlagColumn);

    public static bool HasLegacyNames(CsvTable table) => table.Has(LegacyNameColumn);

    public static IReadOnlyList<string> Locales(CsvTable table) =>
        table.Headers.Where(header => header.StartsWith(NamePrefix, StringComparison.Ordinal) && header.Length > NamePrefix.Length)
            .Select(header => header[NamePrefix.Length..])
            .ToList();

    /// <summary>Moves older layouts into language columns and the focus column, keeping every value.</summary>
    public static void Normalize(CsvTable table)
    {
        if (!IsLegacy(table))
        {
            return;
        }
        if (table.Has(LegacyNameColumn))
        {
            MoveLegacyNames(table);
        }
        if (!table.Has(FocusColumn))
        {
            table.Headers.Add(FocusColumn);
        }
        if (table.Has(LegacyFlagColumn))
        {
            table.Headers.Remove(LegacyFlagColumn);
            foreach (var row in table.Rows)
            {
                if (row.Values.Remove(LegacyFlagColumn, out var flag) && row.Get(FocusColumn).Length == 0)
                {
                    row.Values[FocusColumn] = FocusFromLegacyFlag(flag, row);
                }
            }
        }
    }

    public static RewardEntry ToEntry(CsvRow row, IReadOnlyList<string> locales, out bool validFocus)
    {
        var names = new Dictionary<string, string>();
        foreach (var locale in locales)
        {
            var name = row.Get(ColumnFor(locale));
            if (name.Length > 0)
            {
                names[locale] = name;
            }
        }
        validFocus = TryParseFocus(row.Get(FocusColumn), out var focus);
        return new RewardEntry(row.Get("id"), row.Get("type"), row.Get("rank"), focus, names);
    }

    private static void MoveLegacyNames(CsvTable table)
    {
        var position = table.Headers.IndexOf(LegacyNameColumn);
        table.Headers.RemoveAt(position);
        foreach (var column in new[] { ColumnFor(English), ColumnFor(Chinese) })
        {
            if (!table.Has(column))
            {
                table.Headers.Insert(position, column);
            }
        }
        foreach (var row in table.Rows)
        {
            if (row.Values.Remove(LegacyNameColumn, out var name) && name.Trim().Length > 0)
            {
                var column = ColumnFor(HanPattern().IsMatch(name) ? Chinese : English);
                if (row.Get(column).Length == 0)
                {
                    row.Values[column] = name;
                }
            }
        }
    }

    private static string FocusFromLegacyFlag(string flag, CsvRow row)
    {
        var id = row.Get("id");
        var type = row.Get("type");
        var inferred = InferFocus(id, row.Get("rank"), type);
        switch (flag.Trim().ToLowerInvariant())
        {
            case "true" or "1" or "yes" or "y" or "是":
                // The old flag said "this is an S", so a cosmetic keeps its kind and anything else needs one.
                return FocusCode(inferred != RewardFocus.None ? inferred
                    : type.Equals("arc", StringComparison.OrdinalIgnoreCase) ? RewardFocus.SArc : RewardFocus.SCharacter);
            case "false" or "0" or "no" or "n" or "否":
                return FocusCode(inferred.IsTopRank() ? RewardFocus.None : inferred);
            default:
                return "";
        }
    }
}
