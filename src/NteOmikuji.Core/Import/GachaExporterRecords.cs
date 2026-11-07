using System.Globalization;
using System.Text.Json;
using NteOmikuji.Core.Protocol;
using NteOmikuji.Core.Session;

namespace NteOmikuji.Core.Import;

/// <param name="Rows">Rows per pool, newest first, ready to fill gaps in saved history.</param>
/// <param name="Ignored">Records with a pool, result or time this reader does not recognize.</param>
public sealed record GachaExporterImport(IReadOnlyDictionary<HistoryKind, List<HistoryRow>> Rows, int Ignored);

/// <summary>
/// Reads the records.json kept by NTE_Gacha_Exporter, and the JSON it exports. They store the same pulls with
/// their own IDs and no timestamp bytes, so rows can only be matched to saved history by time. Boards name
/// their event only from its version 1.2.5.
/// </summary>
public static class GachaExporterRecords
{
    private const int SupportedSchema = 1;
    private const string PublicSchema = "nte-gacha-export";
    private static readonly string[] SupportedPublicVersions = ["2.0", "2.1"];
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private const string PermanentEvent = "Lottery_Permanent";
    private const string PointsRewardLabel = "BPUI_LotteryResult_jidianzengli";
    private const string ChaseRewardLabel = "BPUI_LotteryResult_chenmiandi";
    private const string PointsReward = "points_gift";
    private const string ChaseReward = "chase_reward";
    private const int ChaseRewardRoll = -4;

    public static bool Matches(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && (IsPublicExport(root)
            || (root.TryGetProperty("schema_version", out _)
                && root.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array));

    private static bool IsPublicExport(JsonElement root) =>
        root.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object && Text(info, "schema") == PublicSchema;

    private static JsonElement RecordsOf(JsonElement root)
    {
        if (!IsPublicExport(root))
        {
            if (!root.GetProperty("schema_version").TryGetInt32(out var version) || version != SupportedSchema)
            {
                throw new InvalidDataException($"不支援的 records.json 版本：{root.GetProperty("schema_version")}");
            }
            return root.GetProperty("records");
        }
        var exported = Text(root.GetProperty("info"), "schema_version");
        if (!SupportedPublicVersions.Contains(exported))
        {
            throw new InvalidDataException($"不支援的 NTE_Gacha_Exporter 匯出版本：{exported}");
        }
        if (!root.TryGetProperty("nte", out var nte) || !nte.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("NTE_Gacha_Exporter 匯出檔缺少 nte.list。");
        }
        return list;
    }

    public static GachaExporterImport Read(JsonElement root)
    {
        var perPool = new Dictionary<HistoryKind, List<(long Order, HistoryRow Row)>>();
        var ignored = 0;
        var position = 0L;
        foreach (var record in RecordsOf(root).EnumerateArray())
        {
            var order = record.TryGetProperty("source_order", out var value) && value.TryGetInt64(out var stored) ? stored : position;
            position++;
            if (ToRow(record) is not { } row)
            {
                ignored++;
                continue;
            }
            if (!perPool.TryGetValue(row.Kind, out var rows))
            {
                perPool[row.Kind] = rows = [];
            }
            rows.Add((order, row.Row));
        }

        var result = new Dictionary<HistoryKind, List<HistoryRow>>();
        foreach (var (kind, rows) in perPool)
        {
            // The file runs oldest first, and history here runs newest first.
            var ordered = rows.OrderByDescending(item => item.Order).Select(item => item.Row).ToList();
            var poolInfo = PoolInfo.For(kind);
            var system = kind == HistoryKind.ArcMiracleBox ? "gashapon" : "monopoly";
            foreach (var group in ordered.GroupBy(row => row.TimestampDecoded))
            {
                var ordinal = 0;
                foreach (var row in group)
                {
                    row.PoolGroupId = poolInfo.Id;
                    row.GroupOrdinal = ordinal;
                    row.Uid = HistoryAssembly.MakeUid(system, poolInfo.Id, $"time:{group.Key}", ordinal);
                    ordinal++;
                }
            }
            result[kind] = ordered;
        }
        return new GachaExporterImport(result, ignored);
    }

    private static (HistoryKind Kind, HistoryRow Row)? ToRow(JsonElement record)
    {
        var time = Text(record, "time");
        var itemId = Text(record, "item_id");
        if (itemId is null || !DateTime.TryParseExact(time, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return null;
        }
        var row = new HistoryRow
        {
            RewardId = itemId,
            TimestampDecoded = time!,
            DecoderMode = "imported",
        };
        var pool = Text(record, "pool_id");
        switch (Text(record, "record_type"))
        {
            case "fork":
                row.ResultType = "";
                row.SourceType = "miracle_box";
                row.EventId = RecordBlockParser.IsEventId(pool) ? pool : null;
                return (HistoryKind.ArcMiracleBox, row);
            case "monopoly" when BoardOf(pool) is { } board && ApplyResult(record, row):
                row.Quantity = record.TryGetProperty("count", out var count) && count.TryGetInt64(out var quantity) ? quantity : null;
                row.EventId = Text(record, "lottery_id") is { } lottery && RecordBlockParser.IsEventId(lottery)
                    ? lottery
                    : board == HistoryKind.Permanent ? PermanentEvent : null;
                return (board, row);
            default:
                return null;
        }
    }

    /// <summary>Their names for the two boards, found by matching pull times against this app's captures.</summary>
    private static HistoryKind? BoardOf(string? pool) => pool switch
    {
        "CardPool_Character" => HistoryKind.LimitedCharacter,
        "CardPool_NewRole" => HistoryKind.Permanent,
        _ => null,
    };

    private static bool ApplyResult(JsonElement record, HistoryRow row)
    {
        switch (Text(record, "roll_label_id"))
        {
            case PointsRewardLabel:
                (row.ResultType, row.Dice) = (PointsReward, 0);
                return true;
            case ChaseRewardLabel:
                (row.ResultType, row.Dice) = (ChaseReward, ChaseRewardRoll);
                return true;
            case null when record.TryGetProperty("roll_points", out var points) && points.TryGetInt32(out var roll) && roll is >= 1 and <= 6:
                (row.ResultType, row.Dice) = ("dice", roll);
                return true;
            default:
                return false;
        }
    }

    private static string? Text(JsonElement record, string name) =>
        record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
