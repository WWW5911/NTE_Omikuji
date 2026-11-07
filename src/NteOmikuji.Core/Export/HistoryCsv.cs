using System.Globalization;
using NteOmikuji.Core.Analysis;
using NteOmikuji.Core.Mapping;

namespace NteOmikuji.Core.Export;

/// <summary>Writes history for spreadsheets, in the display language and without the account's UID.</summary>
public static class HistoryCsv
{
    private const char FormulaGuard = '\'';

    private static readonly string[] Headers = ["卡池", "活動", "活動ID", "時間", "結果", "骰點", "獎勵", "獎勵ID", "稀有度", "類型", "數量", "關注類別"];

    /// <param name="nameOf">How to show a reward.</param>
    /// <param name="bannerName">How to show an event, when it has a name.</param>
    public static string ToText(IEnumerable<BannerHistory> histories, Func<HistoryRow, string> nameOf, Func<string, string?> bannerName)
    {
        var table = new CsvTable(Headers);
        foreach (var history in histories)
        {
            foreach (var row in history.Rows)
            {
                var dice = row.Dice is >= 1 ? row.Dice.Value.ToString(CultureInfo.InvariantCulture) : "";
                var fields = new[]
                {
                    PoolName(history.Banner.Kind),
                    row.BannerId is { } id ? bannerName(id) ?? "" : "",
                    row.BannerId ?? "",
                    row.TimestampDecoded,
                    row.ResultType.Length == 0 ? "" : HistoryAnalyzer.ResultLabel(row),
                    dice,
                    nameOf(row),
                    row.RewardId,
                    row.RewardRank ?? "",
                    row.RewardType,
                    row.Quantity?.ToString(CultureInfo.InvariantCulture) ?? "",
                    RewardSchema.FocusLabel(row.Focus),
                };
                table.Rows.Add(new CsvRow(0, Headers.Zip(fields, (header, field) => (header, Guard(field))).ToDictionary(pair => pair.header, pair => pair.Item2)));
            }
        }
        return table.ToText();
    }

    public static string PoolName(HistoryKind kind) => kind switch
    {
        HistoryKind.LimitedCharacter => "限定棋盤",
        HistoryKind.Permanent => "常駐棋盤",
        HistoryKind.ArcMiracleBox => "弧盤研募",
        HistoryKind.MysteryBox => "盲盒",
        _ => kind.ToString(),
    };

    /// <summary>Reward text can be anything, and Excel runs a cell that starts like a formula when the file opens.</summary>
    internal static string Guard(string field) =>
        field.Length > 0 && field[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? FormulaGuard + field : field;
}
