using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using NteOmikuji.Core.Protocol;

namespace NteOmikuji.Core.Export;

public sealed record ExportContext(
    string Source,
    string? CaptureSource = null,
    string? UserUid = null,
    string? ServerId = null,
    int? FlowIndex = null,
    int? CandidatePairs = null);

/// <summary>Writes the version 1 JSON formats, which trackers already consume.</summary>
public static class HistoryJsonWriter
{
    public const string AppName = "nte-omikuji";

    /// <summary>Comes from Directory.Build.props, the one place a release's version is set.</summary>
    public static readonly string AppVersion =
        typeof(HistoryJsonWriter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    private const string GameName = "Neverness to Everness";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ToJson(JsonNode node) => node.ToJsonString(Options);

    public static JsonObject BuildHistory(PoolHistory history, ExportContext context)
    {
        var scan = new JsonObject
        {
            ["mode"] = "stable_only",
            ["boundary_policy"] = "export_ordinal_stable_groups",
            ["decoded_records"] = history.Rows.Count,
            ["exported_records"] = history.Rows.Count,
            ["skipped_records"] = 0,
            ["warnings"] = new JsonArray(history.Warnings.Select(WarningNode).ToArray<JsonNode?>()),
        };
        if (context.FlowIndex is { } flowIndex)
        {
            scan["udp_flow_index"] = flowIndex;
        }
        if (context.CandidatePairs is { } pairs)
        {
            scan["candidate_request_response_pairs"] = pairs;
        }
        scan["pages_seen"] = new JsonArray(history.PagesSeen.Select(page => (JsonNode?)page).ToArray());

        var export = Header("nte-history-export", context);
        export["banner"] = new JsonObject
        {
            ["id"] = history.Pool.Id,
            ["name"] = history.Pool.Name,
            ["system"] = history.Pool.System,
            ["shared_pity"] = history.Pool.SharedPity,
        };
        export["scan"] = scan;
        AddAccount(export, context);
        export["records"] = new JsonArray(history.Rows.Select(row => RecordNode(history.Pool, row)).ToArray<JsonNode?>());
        return export;
    }

    public static JsonObject BuildAchievements(IReadOnlyList<AchievementRecord> records, RewardCatalog catalog, ExportContext context)
    {
        var inGame = new Summary();
        var playstation = new Summary();
        var categories = new JsonObject();
        foreach (var record in records)
        {
            var isPlaystation = record.Id.StartsWith("playstation_", StringComparison.OrdinalIgnoreCase);
            (isPlaystation ? playstation : inGame).Add(record.Completed);
            var separator = record.Id.IndexOf('_');
            var category = separator >= 0 ? record.Id[..separator].ToLowerInvariant() : "uncategorized";
            var node = new JsonObject
            {
                ["id"] = record.Id,
                ["platform"] = isPlaystation ? "playstation" : "in_game",
                ["status"] = record.Status,
                ["progress"] = record.Progress,
                ["completed"] = record.Completed,
                ["completed_at"] = record.CompletedAt,
            };
            if (catalog.FindAchievement(record.Id) is { } meta)
            {
                node["name"] = meta.Name;
                node["description"] = meta.Description;
                node["quality"] = meta.Quality;
                node["target"] = JsonNode.Parse(meta.Target.GetRawText());
                node["rewards"] = JsonNode.Parse(meta.Rewards.GetRawText());
            }
            if (categories[category] is not JsonArray list)
            {
                list = [];
                categories[category] = list;
            }
            list.Add(node);
        }

        var export = Header("nte-achievement-export", context);
        export["scan"] = new JsonObject { ["in_game"] = inGame.ToNode(), ["playstation"] = playstation.ToNode() };
        AddAccount(export, context);
        export["categories"] = categories;
        return export;
    }

    public static string FileName(string? userUid, string label, DateTime now) =>
        $"{SafeUid(userUid)}_{label}_{now:yyyyMMdd_HHmmss}.json";

    public static string LibraryFileName(string? userUid, string label) => $"{SafeUid(userUid)}_{label}.json";

    /// <summary>Keeps only safe characters, since UIDs can come from packets, files, or typing.</summary>
    private static string SafeUid(string? userUid)
    {
        var safe = new string((userUid ?? "").Trim().Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        return safe.Length == 0 ? "unknown" : safe;
    }

    private static JsonObject Header(string format, ExportContext context)
    {
        var export = new JsonObject
        {
            ["format"] = format,
            ["format_version"] = 1,
            ["game"] = GameName,
            ["source"] = context.Source,
        };
        if (context.CaptureSource is not null)
        {
            export["capture_source"] = context.CaptureSource;
        }
        export["exporter"] = new JsonObject { ["name"] = AppName, ["version"] = AppVersion };
        return export;
    }

    private static void AddAccount(JsonObject export, ExportContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.UserUid))
        {
            export["user_uid"] = context.UserUid.Trim();
        }
        if (!string.IsNullOrWhiteSpace(context.ServerId))
        {
            export["server_id"] = context.ServerId.Trim();
            if (ServerRegions.RegionFor(context.ServerId) is { } region)
            {
                export["account_region"] = region;
            }
        }
    }

    private static JsonObject WarningNode(CaptureWarning warning)
    {
        var node = new JsonObject { ["code"] = warning.Code };
        if (warning.IgnoredPages is not null)
        {
            node["ignored_pages"] = new JsonArray(warning.IgnoredPages.Select(page => (JsonNode?)page).ToArray());
        }
        node["reason"] = warning.Reason;
        return node;
    }

    internal static JsonObject RecordNode(PoolInfo poolInfo, HistoryRow row)
    {
        var node = new JsonObject
        {
            ["uid"] = row.Uid,
            ["pool_group_id"] = row.PoolGroupId,
            ["timestamp"] = row.TimestampDecoded,
            ["timestamp_group_ordinal"] = row.GroupOrdinal,
        };
        switch (poolInfo.Kind)
        {
            case HistoryKind.ArcMiracleBox:
                AddReward(node, row);
                node["source_type"] = row.SourceType;
                break;
            case HistoryKind.MysteryBox:
                node["result_type"] = "single_pull";
                AddReward(node, row);
                node["quantity"] = row.Quantity;
                node["source_type"] = row.SourceType;
                break;
            default:
                node["roll_result"] = row.Dice;
                node["result_type"] = row.ResultType;
                AddReward(node, row);
                node["quantity"] = row.Quantity;
                break;
        }
        // Additive to format version 1: present only when the game's result text differs from the reward.
        if (row.ResultText is not null)
        {
            node["result_text"] = row.ResultText;
        }
        // Also additive: present only when the row's event could be read.
        if (row.EventId is not null)
        {
            node["pool_id"] = row.EventId;
        }
        // Also additive: present only when nothing confirmed the row's fields.
        if (row.Unverified)
        {
            node["unverified"] = true;
        }
        return node;
    }

    private static void AddReward(JsonObject node, HistoryRow row)
    {
        node["reward_type"] = row.RewardType;
        node["reward_id"] = row.RewardId;
        node["reward_name"] = row.RewardName;
        node["reward_rank"] = row.RewardRank;
    }

    private sealed class Summary
    {
        private int _completed;
        private int _inProgress;

        public void Add(bool completed)
        {
            if (completed)
            {
                _completed++;
            }
            else
            {
                _inProgress++;
            }
        }

        public JsonObject ToNode() => new()
        {
            ["total_achievements"] = _completed + _inProgress,
            ["completed_achievements"] = _completed,
            ["in_progress_achievements"] = _inProgress,
        };
    }
}
