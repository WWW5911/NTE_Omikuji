using System.Security.Cryptography;
using System.Text;
using NteOmikuji.Core.Protocol;

namespace NteOmikuji.Core.Session;

/// <summary>Turns captured pages into ordered rows with stable UIDs.</summary>
public static class HistoryAssembly
{
    private const int PullSetSize = 10;

    public const string UnverifiedCode = "UNVERIFIED_DECODE";

    public static CaptureWarning UnverifiedWarning() => new(
        UnverifiedCode,
        "有紀錄的格式無法確認，可能是遊戲改版。這些紀錄不會蓋掉已存的資料，內容也可能不準，程式更新後重新擷取就會更正。");

    public static PoolHistory Build(HistoryKind kind, IReadOnlyList<CapturedPage> pages, RewardCatalog catalog)
    {
        // Each Mystery Box pool numbers its own pages from 1.
        var run = new List<CapturedPage>();
        var warnings = new List<CaptureWarning>();
        foreach (var pool in pages.GroupBy(page => page.Pool ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var (poolRun, poolWarnings) = SelectRun(pool.ToList());
            run.AddRange(poolRun);
            warnings.AddRange(poolWarnings);
        }
        var poolInfo = PoolInfo.For(kind);
        var rows = kind switch
        {
            HistoryKind.ArcMiracleBox => BuildRows(run, catalog, ArcDecoder.DecodeResponse, RecordBlockType.Fork, ArcDecoder.RowsFromStructured),
            HistoryKind.MysteryBox => BuildRows(run, catalog, MysteryBoxDecoder.DecodeResponse, null, null),
            _ => BuildRows(run, catalog, MonopolyDecoder.DecodeResponse, RecordBlockType.Monopoly, MonopolyDecoder.RowsFromStructured),
        };
        rows.ForEach(row =>
        {
            row.PoolGroupId = poolInfo.Id;
            row.Focus = catalog.FocusOf(row.RewardId, row.EventId);
            // A Mystery Box has only one way to read its rows, so there is nothing for it to fall back from.
            row.Unverified = kind != HistoryKind.MysteryBox && row.DecoderMode is not ("scan_checked" or "block_fallback");
        });
        if (rows.Any(row => row.Unverified))
        {
            warnings.Add(UnverifiedWarning());
        }
        switch (kind)
        {
            case HistoryKind.ArcMiracleBox:
                ReconcileArcBatches(rows);
                AssignOrdinals(rows, "gashapon", adjacentOnly: false);
                break;
            case HistoryKind.MysteryBox:
                AssignOrdinals(rows, "gashapon", adjacentOnly: false);
                break;
            default:
                ReconcileMonopolySplits(rows);
                AssignOrdinals(rows, "monopoly", adjacentOnly: true);
                break;
        }
        return new PoolHistory(poolInfo, rows, warnings, run.Select(page => page.Page).ToList());
    }

    public static string MakeUid(string system, string poolId, string timestampRawHex, int ordinal)
    {
        var source = string.Join('|', "nte", system, poolId, timestampRawHex, ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..32];
    }

    internal static (List<CapturedPage> Run, List<CaptureWarning> Warnings) SelectRun(IReadOnlyList<CapturedPage> pages)
    {
        var warnings = new List<CaptureWarning>();
        if (pages.Count == 0)
        {
            return ([], warnings);
        }
        var byPage = new Dictionary<int, CapturedPage>();
        foreach (var page in pages)
        {
            byPage[page.Page] = page;
        }
        if (byPage.ContainsKey(1))
        {
            var selected = new List<CapturedPage>();
            for (var number = 1; byPage.TryGetValue(number, out var page); number++)
            {
                selected.Add(page);
            }
            if (selected.Count < byPage.Count)
            {
                var kept = selected.Select(page => page.Page).ToHashSet();
                var ignored = byPage.Keys.Where(page => !kept.Contains(page)).Order().ToList();
                warnings.Add(new CaptureWarning(
                    "PAGE_GAP_DETECTED",
                    $"Page gap detected after page {selected[^1].Page}; ignored later pages [{string.Join(", ", ignored)}]. Re-scan or scroll more slowly.",
                    ignored));
            }
            return (selected, warnings);
        }

        warnings.Add(new CaptureWarning("DID_NOT_START_AT_PAGE_1", "Page 1 was not captured; results may be unstable. Re-scan from the top."));
        var best = new List<CapturedPage>();
        var current = new List<CapturedPage>();
        foreach (var page in pages)
        {
            if (current.Count > 0 && page.Page != current[^1].Page + 1)
            {
                current = [];
            }
            current.Add(page);
            if (current.Count > best.Count)
            {
                best = current;
            }
        }
        return (best, warnings);
    }

    private static List<HistoryRow> BuildRows(
        List<CapturedPage> run,
        RewardCatalog catalog,
        Func<byte[], RewardCatalog, List<HistoryRow>> decode,
        RecordBlockType? structuredType,
        Func<IEnumerable<BlockRecord>, RewardCatalog, List<HistoryRow>>? fromStructured)
    {
        var rows = new List<HistoryRow>();
        foreach (var page in run)
        {
            foreach (var row in decode(page.Payload, catalog).Skip(page.SliceStart).Take(page.SliceCount))
            {
                row.Page = page.Page;
                rows.Add(row);
            }
        }
        if (structuredType is null || fromStructured is null || rows.Any(row => row.DecoderMode != "block_fallback"))
        {
            return rows;
        }

        // Fully structured runs are assembled across packets so retransmissions cannot duplicate rows.
        var assembler = new RecordBlockAssembler();
        for (var index = 0; index < run.Count; index++)
        {
            assembler.AddBlocks(RecordBlockParser.ParseBlocks(run[index].Payload, structuredType.Value, index));
        }
        var assembled = assembler.Rows();
        if (assembled.Count == 0)
        {
            return rows;
        }
        var rebuilt = fromStructured(assembled, catalog);
        for (var i = 0; i < rebuilt.Count; i++)
        {
            rebuilt[i].Page = run[assembled[i].SourceIndex ?? 0].Page;
        }
        return rebuilt;
    }

    private static bool IsDice(HistoryRow row) => row.ResultType == "dice";

    /// <summary>Joins timestamp fragments only when they form a ten-pull split across a page boundary.</summary>
    private static void ReconcileMonopolySplits(List<HistoryRow> rows)
    {
        var groups = new List<List<HistoryRow>>();
        foreach (var row in rows)
        {
            if (groups.Count > 0 && groups[^1][0].TimestampRawHex == row.TimestampRawHex)
            {
                groups[^1].Add(row);
            }
            else
            {
                groups.Add([row]);
            }
        }

        var index = 0;
        while (index < groups.Count)
        {
            var first = groups[index];
            var diceCount = first.Count(IsDice);
            if (diceCount is <= 0 or >= PullSetSize)
            {
                index++;
                continue;
            }
            var displayed = first[0].TimestampDecoded;
            var pageSet = first.Select(row => row.Page).ToHashSet();
            var end = index + 1;
            while (end < groups.Count && groups[end][0].TimestampDecoded == displayed)
            {
                var candidateDice = groups[end].Count(IsDice);
                if (diceCount + candidateDice > PullSetSize)
                {
                    break;
                }
                diceCount += candidateDice;
                pageSet.UnionWith(groups[end].Select(row => row.Page));
                end++;
                if (diceCount == PullSetSize)
                {
                    break;
                }
            }
            if (diceCount == PullSetSize && pageSet.Count > 1 && end > index + 1)
            {
                var canonical = first[0].TimestampRawHex;
                foreach (var row in groups.Skip(index).Take(end - index).SelectMany(group => group))
                {
                    row.TimestampRawHex = canonical;
                }
                index = end;
            }
            else
            {
                index++;
            }
        }
    }

    /// <summary>Arc pulls always come in tens, so each batch shares its first raw timestamp.</summary>
    private static void ReconcileArcBatches(List<HistoryRow> rows)
    {
        var start = 0;
        while (start < rows.Count)
        {
            var end = start + 1;
            while (end < rows.Count && rows[end].TimestampDecoded == rows[start].TimestampDecoded)
            {
                end++;
            }
            for (var batch = start; batch < end; batch += PullSetSize)
            {
                var canonical = rows[batch].TimestampRawHex;
                for (var i = batch; i < Math.Min(batch + PullSetSize, end); i++)
                {
                    rows[i].TimestampRawHex = canonical;
                }
            }
            start = end;
        }
    }

    private static void AssignOrdinals(List<HistoryRow> rows, string system, bool adjacentOnly)
    {
        var seen = new Dictionary<string, int>();
        string? previous = null;
        foreach (var row in rows)
        {
            if (adjacentOnly && row.TimestampRawHex != previous)
            {
                seen.Remove(row.TimestampRawHex);
            }
            var ordinal = seen.GetValueOrDefault(row.TimestampRawHex);
            seen[row.TimestampRawHex] = ordinal + 1;
            row.GroupOrdinal = ordinal;
            row.Uid = MakeUid(system, row.PoolGroupId, row.TimestampRawHex, ordinal);
            previous = row.TimestampRawHex;
        }
    }
}
