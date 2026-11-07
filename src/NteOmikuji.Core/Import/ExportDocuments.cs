using System.Globalization;
using System.Text.Json;
using NteOmikuji.Core.Mapping;

namespace NteOmikuji.Core.Import;

public enum ImportKind
{
    Flows,
    HistoryExport,
    AchievementExport,
    RewardMapping,
    AchievementMapping,
}

public sealed record HistoryDocument(BannerInfo Banner, List<HistoryRow> Rows, string? UserUid, string? ServerId, string? Source, string? CaptureSource);

public sealed record AchievementDocument(List<AchievementRecord> Records, string? UserUid, string? ServerId, string? Source, string? CaptureSource);

/// <summary>Reads files by what they contain, so one import control accepts captures, exports, and mappings.</summary>
public static class ExportDocuments
{
    private static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static ImportKind Classify(string fileName, byte[] bytes)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension == ".flows")
        {
            return ImportKind.Flows;
        }
        if (extension == ".csv")
        {
            var table = CsvTable.Parse(CsvTable.DecodeText(bytes));
            if (!table.Has("id") || (!table.Has("name") && RewardSchema.Locales(table).Count == 0))
            {
                throw new InvalidDataException("CSV 需要 id 與名稱欄位，例如 name.en。");
            }
            return table.Has("description") || table.Has("target") ? ImportKind.AchievementMapping : ImportKind.RewardMapping;
        }
        if (extension != ".json")
        {
            throw new InvalidDataException($"不支援 {extension} 檔。");
        }

        using var document = Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("JSON 最外層必須是物件。");
        }
        if (root.TryGetProperty("format", out var format))
        {
            return format.GetString() switch
            {
                "nte-history-export" => ImportKind.HistoryExport,
                "nte-achievement-export" => ImportKind.AchievementExport,
                var other => throw new InvalidDataException($"不支援的格式：{other}"),
            };
        }
        if (root.TryGetProperty("pool_key", out _))
        {
            throw new InvalidDataException("這是卡池設定檔，不是獎勵對應表。");
        }
        var first = root.EnumerateObject().Select(entry => entry.Value).FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("無法辨識這個 JSON 的內容。");
        }
        return first.TryGetProperty("description", out _) || first.TryGetProperty("target", out _)
            ? ImportKind.AchievementMapping
            : ImportKind.RewardMapping;
    }

    public static CsvTable ReadMapping(string fileName, byte[] bytes, bool isAchievements)
    {
        if (Path.GetExtension(fileName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return CsvTable.Parse(CsvTable.DecodeText(bytes));
        }
        using var document = Parse(bytes);
        return MappingStore.TableFromJson(document.RootElement, isAchievements);
    }

    public static HistoryDocument ReadHistory(byte[] bytes)
    {
        using var document = Parse(bytes);
        var root = document.RootElement;
        RequireVersion(root);
        var bannerId = root.GetProperty("banner").GetProperty("id").GetString();
        var banner = Enum.GetValues<HistoryKind>().Select(BannerInfo.For).FirstOrDefault(info => info.Id == bannerId)
            ?? throw new InvalidDataException($"不支援的卡池：{bannerId}");

        var rows = new List<HistoryRow>();
        foreach (var record in root.GetProperty("records").EnumerateArray())
        {
            rows.Add(new HistoryRow
            {
                Uid = Text(record, "uid") ?? throw new InvalidDataException("紀錄缺少 uid。"),
                PoolGroupId = banner.Id,
                TimestampDecoded = Text(record, "timestamp") ?? "",
                GroupOrdinal = (int)(Number(record, "timestamp_group_ordinal") ?? 0),
                Dice = (int?)Number(record, "roll_result"),
                ResultType = Text(record, "result_type") ?? "",
                ResultText = Text(record, "result_text"),
                RewardType = Text(record, "reward_type") ?? "",
                RewardId = Text(record, "reward_id") ?? "",
                RewardName = Text(record, "reward_name") ?? "",
                RewardRank = Text(record, "reward_rank"),
                Quantity = Number(record, "quantity"),
                SourceType = Text(record, "source_type"),
                DecoderMode = "imported",
            });
        }
        return new HistoryDocument(banner, rows, Text(root, "user_uid"), Text(root, "server_id"), Text(root, "source"), Text(root, "capture_source"));
    }

    public static AchievementDocument ReadAchievements(byte[] bytes)
    {
        using var document = Parse(bytes);
        var root = document.RootElement;
        RequireVersion(root);
        var records = new List<AchievementRecord>();
        foreach (var category in root.GetProperty("categories").EnumerateObject())
        {
            foreach (var record in category.Value.EnumerateArray())
            {
                var id = Text(record, "id") ?? throw new InvalidDataException("成就缺少 id。");
                records.Add(new AchievementRecord(id, (ulong)(Number(record, "progress") ?? 0), CompletionTicks(record)));
            }
        }
        return new AchievementDocument(records, Text(root, "user_uid"), Text(root, "server_id"), Text(root, "source"), Text(root, "capture_source"));
    }

    private static ulong CompletionTicks(JsonElement record)
    {
        if (Text(record, "completed_at") is { } text
            && DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var completed))
        {
            return (ulong)completed.Ticks;
        }
        // Completed with an unreadable time: any non-zero value keeps the status.
        return record.TryGetProperty("completed", out var flag) && flag.ValueKind == JsonValueKind.True ? 1UL : 0UL;
    }

    private static JsonDocument Parse(byte[] bytes)
    {
        try
        {
            return JsonDocument.Parse(CsvTable.DecodeText(bytes), JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"JSON 格式錯誤：{exception.Message}", exception);
        }
    }

    private static void RequireVersion(JsonElement root)
    {
        if (!root.TryGetProperty("format_version", out var version) || version.GetInt32() != 1)
        {
            throw new InvalidDataException("只支援 format_version 1。");
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}
