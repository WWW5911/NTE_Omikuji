using System.Net;

namespace NteOmikuji.Core.Mapping;

/// <param name="Files">The files that were downloaded, by name.</param>
/// <param name="Missing">Files the source does not have, which leaves the local ones as they are.</param>
public sealed record MappingDownload(IReadOnlyDictionary<string, byte[]> Files, IReadOnlyList<string> Missing);

/// <summary>Downloads the mapping tables from this project's repository. It only reads, and sends nothing about the player.</summary>
public sealed class MappingUpdater(HttpClient http, string baseUrl = MappingUpdater.DefaultBaseUrl)
{
    public const string DefaultBaseUrl = "https://raw.githubusercontent.com/WWW5911/NTE_Omikuji/main/src/NteOmikuji.Core/Mappings/";

    private const int MaxFileBytes = 8 * 1024 * 1024;

    /// <summary>A failed download throws, so an update never applies part of a set because of a bad connection.</summary>
    public async Task<MappingDownload> DownloadAsync(CancellationToken token = default)
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
            files[name] = await ReadLimitedAsync(response, name, token);
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
