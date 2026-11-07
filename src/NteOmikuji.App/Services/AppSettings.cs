using System.Text.Json;
using NteOmikuji.Core.Mapping;

namespace NteOmikuji.App.Services;

/// <summary>Display preferences, kept with the user's data so they travel with the folder.</summary>
public sealed class AppSettings
{
    private const string FileName = "settings.json";

    public string DisplayLanguage { get; set; } = RewardSchema.Chinese;

    public HashSet<string> VisibleLocales { get; set; } = [RewardSchema.Chinese, RewardSchema.English];

    public bool AutoUpdateMappings { get; set; } = true;

    /// <summary>An HTTPS folder holding the mapping files, for when someone else takes over maintaining them.</summary>
    public string? MappingSource { get; set; }

    public static AppSettings Load(string folder)
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(folder, FileName))) ?? new AppSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing or damaged preferences are not worth interrupting anyone for.
            return new AppSettings();
        }
    }

    public void Save(string folder)
    {
        Directory.CreateDirectory(folder);
        AtomicFile.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
