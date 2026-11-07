using System.Net;

namespace NteOmikuji.Core.Mapping;

/// <param name="Files">The files that were downloaded, by name.</param>
/// <param name="Missing">Files the source does not have, which leaves the local ones as they are.</param>
public sealed record MappingDownload(IReadOnlyDictionary<string, byte[]> Files, IReadOnlyList<string> Missing);

/// <summary>Downloads the mapping tables from this project's repository. It only reads, and sends nothing about the player.</summary>
/// <param name="timeLimit">How long the whole download may take.</param>
public sealed class MappingUpdater(HttpClient http, string baseUrl = MappingUpdater.DefaultBaseUrl, TimeSpan? timeLimit = null)
{
    private static readonly TimeSpan DefaultTimeLimit = TimeSpan.FromSeconds(60);

    public const string DefaultBaseUrl = "https://raw.githubusercontent.com/WWW5911/NTE_Omikuji/main/src/NteOmikuji.Core/Mappings/";

    private const int MaxFileBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Where to download from, so the community can take over if this project stops being updated. Only HTTPS is
    /// accepted, and anything else falls back to this project's repository.
    /// </summary>
    public static string SourceOrDefault(string? configured)
    {
        var source = configured?.Trim();
        if (string.IsNullOrEmpty(source) || !Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return DefaultBaseUrl;
        }
        return source.EndsWith('/') ? source : source + "/";
    }

    /// <summary>A failed download throws, so an update never applies part of a set because of a bad connection.</summary>
    public async Task<MappingDownload> DownloadAsync(CancellationToken token = default)
    {
        // HttpClient's own timeout stops at the headers, so a stalled body needs this one.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeLimit ?? DefaultTimeLimit);
        try
        {
            return await DownloadFilesAsync(limit.Token);
        }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested)
        {
            throw new HttpRequestException("下載逾時。", exception);
        }
    }

    private async Task<MappingDownload> DownloadFilesAsync(CancellationToken token)
    {
        var files = new Dictionary<string, byte[]>();
        var missing = new List<string>();
        foreach (var name in MappingStore.UpdatableFiles)
        {
            using var response = await http.GetAsync(baseUrl + name, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                missing.Add(name);
                continue;
            }
            response.EnsureSuccessStatusCode();
            try
            {
                files[name] = await ReadLimitedAsync(response, name, token);
            }
            catch (IOException exception)
            {
                throw new HttpRequestException($"下載 {name} 時連線中斷。", exception);
            }
        }
        return new MappingDownload(files, missing);
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, string name, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > MaxFileBytes)
            {
                throw new InvalidDataException($"下載的 {name} 超過大小上限。");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
