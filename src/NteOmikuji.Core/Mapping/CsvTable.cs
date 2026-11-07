using System.Globalization;
using System.Text;

namespace NteOmikuji.Core.Mapping;

public sealed record CsvRow(int Line, Dictionary<string, string> Values)
{
    public string Get(string column) => Values.GetValueOrDefault(column, "").Trim();
}

/// <summary>An RFC 4180 table that keeps columns it does not know, so notes added in Excel survive a rewrite.</summary>
public sealed class CsvTable(IReadOnlyList<string> headers)
{
    static CsvTable() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public List<string> Headers { get; } = [.. headers];
    public List<CsvRow> Rows { get; } = [];

    public bool Has(string column) => Headers.Contains(column);

    /// <summary>
    /// Reads UTF-8, falling back to the Windows ANSI code page because Excel's
    /// plain "CSV" save type writes Big5 on Traditional Chinese systems.
    /// </summary>
    public static string DecodeText(byte[] bytes, int? fallbackCodePage = null)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
        }
        catch (ArgumentException)
        {
            return Encoding.GetEncoding(fallbackCodePage ?? CultureInfo.CurrentCulture.TextInfo.ANSICodePage).GetString(bytes);
        }
    }

    public static CsvTable Parse(string text)
    {
        var records = ReadRecords(text);
        if (records.Count == 0)
        {
            throw new InvalidDataException("檔案是空的，缺少標題列。");
        }
        var table = new CsvTable(records[0].Fields.Select(field => field.Trim().ToLowerInvariant()).ToList());
        foreach (var (line, fields) in records.Skip(1))
        {
            if (fields.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }
            var values = new Dictionary<string, string>();
            for (var i = 0; i < Math.Min(fields.Count, table.Headers.Count); i++)
            {
                values[table.Headers[i]] = fields[i];
            }
            table.Rows.Add(new CsvRow(line, values));
        }
        return table;
    }

    public string ToText()
    {
        var builder = new StringBuilder();
        AppendRecord(builder, Headers);
        foreach (var row in Rows)
        {
            AppendRecord(builder, Headers.Select(header => row.Values.GetValueOrDefault(header, "")));
        }
        return builder.ToString();
    }

    private static void AppendRecord(StringBuilder builder, IEnumerable<string> fields)
    {
        builder.AppendJoin(',', fields.Select(field =>
            field.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{field.Replace("\"", "\"\"")}\"" : field));
        builder.Append("\r\n");
    }

    private static List<(int Line, List<string> Fields)> ReadRecords(string text)
    {
        var records = new List<(int, List<string>)>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var line = 1;
        var recordLine = 1;
        var quoteLine = 1;

        void EndRecord()
        {
            fields.Add(field.ToString());
            field.Clear();
            records.Add((recordLine, fields));
            fields = [];
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch != '"')
                {
                    line += ch == '\n' ? 1 : 0;
                    field.Append(ch);
                }
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }
            }
            else if (ch == '"' && field.Length == 0)
            {
                quoted = true;
                quoteLine = line;
            }
            else if (ch == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (ch == '\n')
            {
                EndRecord();
                line++;
                recordLine = line;
            }
            else if (ch != '\r')
            {
                field.Append(ch);
            }
        }
        if (quoted)
        {
            throw new InvalidDataException($"第 {quoteLine} 行的引號沒有結束。");
        }
        if (field.Length > 0 || fields.Count > 0)
        {
            EndRecord();
        }
        return records;
    }
}
