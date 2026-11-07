using System.Globalization;
using System.Text;
using NteOmikuji.Core.Protocol;
using NteOmikuji.Core.Session;

namespace NteOmikuji.Core.Import;

public sealed record FlowImportResult(int FlowIndex, int CandidatePairs, HistorySession Session);

/// <summary>Imports mitmproxy .flows captures. The tnetstring reader only builds plain data.</summary>
public static class MitmproxyFlows
{
    private const int MaxDepth = 64;
    private const string LocalIp = "192.0.2.1";
    private const string RemoteIp = "198.51.100.1";
    private const int LocalPort = 50000;
    private const int RemotePort = 40000;

    public static FlowImportResult Import(byte[] data, RewardCatalog catalog)
    {
        var flows = ReadFlows(data);
        var candidates = flows
            .Select((flow, index) => (Flow: flow as Dictionary<string, object?>, Index: index))
            .Where(entry => entry.Flow is not null
                && entry.Flow.TryGetValue("messages", out var messages) && messages is List<object?>
                && entry.Flow.TryGetValue("server_conn", out var server)
                && server is Dictionary<string, object?> connection
                && connection.TryGetValue("transport_protocol", out var transport)
                && transport is byte[] protocol && protocol.AsSpan().SequenceEqual("udp"u8))
            .ToList();
        if (candidates.Count == 0)
        {
            throw new InvalidDataException("no UDP flow with messages found");
        }
        var chosen = candidates
            .OrderByDescending(entry => ((List<object?>)entry.Flow!["messages"]!).Count)
            .ThenByDescending(entry => entry.Index)
            .First();

        var session = new HistorySession(LocalIp, catalog);
        var pairs = 0;
        foreach (var message in (List<object?>)chosen.Flow!["messages"]!)
        {
            if (message is not List<object?> { Count: >= 2 } parts || parts[0] is not bool fromClient || parts[1] is not byte[] content)
            {
                continue;
            }
            var packet = fromClient
                ? new IpPacket(LocalIp, RemoteIp, LocalPort, RemotePort, content, false)
                : new IpPacket(RemoteIp, LocalIp, RemotePort, LocalPort, content, false);
            pairs += session.Process(packet).Count;
        }
        return new FlowImportResult(chosen.Index, pairs, session);
    }

    internal static List<object?> ReadFlows(byte[] data)
    {
        var flows = new List<object?>();
        var position = 0;
        while (position < data.Length)
        {
            flows.Add(ReadValue(data, ref position, data.Length, 0));
        }
        return flows;
    }

    private static object? ReadValue(byte[] data, ref int position, int limit, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidDataException("tnetstring is nested too deeply");
        }
        var colon = Array.IndexOf(data, (byte)':', position, limit - position);
        if (colon < 0 || !int.TryParse(data.AsSpan(position, colon - position), NumberStyles.None, CultureInfo.InvariantCulture, out var length))
        {
            throw new InvalidDataException("invalid tnetstring length");
        }
        var start = colon + 1;
        if (length > limit - start - 1)
        {
            throw new InvalidDataException("tnetstring exceeds its container");
        }
        var end = start + length;
        var type = (char)data[end];
        position = end + 1;
        switch (type)
        {
            case ',' or ';':
                return data.AsSpan(start, length).ToArray();
            case '#':
                return long.Parse(data.AsSpan(start, length), CultureInfo.InvariantCulture);
            case '^':
                return double.Parse(data.AsSpan(start, length), CultureInfo.InvariantCulture);
            case '!':
                return data.AsSpan(start, length).SequenceEqual("true"u8);
            case '~':
                return null;
            case ']':
                var list = new List<object?>();
                for (var inner = start; inner < end;)
                {
                    list.Add(ReadValue(data, ref inner, end, depth + 1));
                }
                return list;
            case '}':
                var map = new Dictionary<string, object?>();
                for (var inner = start; inner < end;)
                {
                    if (ReadValue(data, ref inner, end, depth + 1) is not byte[] key)
                    {
                        throw new InvalidDataException("tnetstring map key is not text");
                    }
                    map[Encoding.Latin1.GetString(key)] = ReadValue(data, ref inner, end, depth + 1);
                }
                return map;
            default:
                throw new InvalidDataException($"unknown tnetstring type '{type}'");
        }
    }
}
