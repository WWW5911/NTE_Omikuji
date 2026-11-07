namespace NteOmikuji.Core;

/// <summary>What a player tracks a reward as, maintained per reward in the mapping table.</summary>
public enum RewardFocus
{
    None,
    SCharacter,
    SArc,
    SLimitedCharacter,
    SLimitedArc,
    Outfit,
    VehicleSkin,
    Glider,
}

public static class RewardFocusExtensions
{
    /// <summary>Top-rank rewards are the ones pull intervals are measured between. Cosmetics are only counted.</summary>
    public static bool IsTopRank(this RewardFocus focus) =>
        focus is RewardFocus.SCharacter or RewardFocus.SArc or RewardFocus.SLimitedCharacter or RewardFocus.SLimitedArc;

    public static bool IsLimited(this RewardFocus focus) => focus is RewardFocus.SLimitedCharacter or RewardFocus.SLimitedArc;

    public static bool IsCosmetic(this RewardFocus focus) => focus is RewardFocus.Outfit or RewardFocus.VehicleSkin or RewardFocus.Glider;
}

public enum HistoryKind
{
    Permanent,
    LimitedCharacter,
    ArcMiracleBox,
    MysteryBox,
}

/// <param name="LimitedChance">How often a top-rank reward is the limited one, when that is not always so.</param>
/// <param name="RisesFrom">The pull from which a top-rank reward becomes more likely.</param>
/// <param name="SecondRankAt">How many pulls guarantee an A. On boards this is the reward given every 10 pulls.</param>
/// <param name="BaseRate">The published chance of an S on each pull.</param>
/// <param name="RaisedRate">The published chance after <paramref name="RisesFrom"/> pulls without an S.</param>
public sealed record PityRule(int TopRankAt, int? LimitedAt = null, double? LimitedChance = null, int? RisesFrom = null, int? SecondRankAt = null, double BaseRate = 0, double? RaisedRate = null);

public sealed record BannerInfo(HistoryKind Kind, string Id, string Name, string System, bool SharedPity, string FilePrefix, PityRule? Pity = null)
{
    public static readonly BannerInfo Permanent = new(HistoryKind.Permanent, "Lottery_Permanent", "Standard Board", "Monopoly", false, "Permanent", new PityRule(90, RisesFrom: 70, SecondRankAt: 10, BaseRate: 0.0099, RaisedRate: 0.1959));
    public static readonly BannerInfo LimitedCharacter = new(HistoryKind.LimitedCharacter, "Lottery_LimitedCharacter", "Limited Character Board", "Monopoly", true, "Limited", new PityRule(90, RisesFrom: 70, SecondRankAt: 10, BaseRate: 0.0099, RaisedRate: 0.1959));
    public static readonly BannerInfo ArcMiracleBox = new(HistoryKind.ArcMiracleBox, "Arc_MiracleBox", "Arc Miracle Box", "Gashapon", true, "Arc", new PityRule(60, 80, 0.25, SecondRankAt: 10, BaseRate: 0.03));
    public static readonly BannerInfo MysteryBox = new(HistoryKind.MysteryBox, "Gashapon_MysteryBox", "Mystery Box", "Gashapon", false, "MysteryBox");

    public static BannerInfo For(HistoryKind kind) => kind switch
    {
        HistoryKind.Permanent => Permanent,
        HistoryKind.LimitedCharacter => LimitedCharacter,
        HistoryKind.ArcMiracleBox => ArcMiracleBox,
        HistoryKind.MysteryBox => MysteryBox,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>One decoded pull. Reward and result fields hold whatever text the game sent.</summary>
public sealed class HistoryRow
{
    public int Page { get; set; }
    public string PoolGroupId { get; set; } = "";

    /// <summary>The event the row was pulled on, such as Lottery_MingYin. Null when it could not be read.</summary>
    public string? BannerId { get; set; }

    public int? Dice { get; set; }
    public string ResultType { get; set; } = "unknown";
    public string? ResultText { get; set; }
    public string RewardType { get; set; } = "";
    public string RewardId { get; set; } = "";
    public string RewardName { get; set; } = "";
    public string? RewardRank { get; set; }
    public bool IsMappedReward { get; set; }
    public RewardFocus Focus { get; set; }
    public long? Quantity { get; set; }
    public string? SourceType { get; set; }
    public string TimestampRawHex { get; set; } = "";
    public ulong TimestampTicks { get; set; }
    public string TimestampDecoded { get; set; } = "";
    public string RecordHex { get; set; } = "";
    public string DecoderMode { get; set; } = "heuristic";
    public int GroupOrdinal { get; set; }
    public string Uid { get; set; } = "";
}

public sealed record CaptureWarning(string Code, string Reason, IReadOnlyList<int>? IgnoredPages = null);

public sealed record CapturedPage(int Page, HistoryKind Kind, byte[] Payload, int SliceStart, int SliceCount);

public sealed record BannerHistory(
    BannerInfo Banner,
    IReadOnlyList<HistoryRow> Rows,
    IReadOnlyList<CaptureWarning> Warnings,
    IReadOnlyList<int> PagesSeen);

public sealed record AchievementRecord(string Id, ulong Progress, ulong CompletionTicks)
{
    public bool Completed => CompletionTicks != 0;
    public string Status => Completed ? "completed" : "in_progress";

    public string? CompletedAt =>
        Completed && CompletionTicks <= (ulong)DateTime.MaxValue.Ticks && CompletionTicks >= (ulong)DateTime.UnixEpoch.Ticks
            ? new DateTime((long)CompletionTicks, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss")
            : null;
}

public sealed record IpPacket(string SrcIp, string DstIp, int SrcPort, int DstPort, byte[] Payload, bool IsTcp, uint Sequence = 0);
