using System.Text.Json;

namespace GitX.Core.Updates;

public sealed record UpdatePreferences(bool AutomaticChecks = false, bool BackgroundDownload = false, bool IncludePreviews = false)
{
    public bool ShouldDownload(UpdateOffer? offer) => BackgroundDownload && offer?.IsNewer == true;
}
public sealed class UpdatePreferencesStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1);
    public async Task<UpdatePreferences> ReadAsync(bool defaultPreview, CancellationToken token)
    {
        try
        {
            if (!File.Exists(path)) return new(IncludePreviews: defaultPreview);
            if (new FileInfo(path).Length > 4096) throw new IOException("更新设置文件过大。");
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, token).ConfigureAwait(false));
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new IOException("更新设置格式无效，自动更新已保持关闭。");
            bool Flag(string name, bool fallback) => json.RootElement.TryGetProperty(name, out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False ? flag.GetBoolean() : fallback;
            return new(Flag("automaticChecks", false), Flag("backgroundDownload", false), Flag("includePreviews", defaultPreview));
        }
        catch (JsonException) { throw new IOException("更新设置损坏，自动更新已保持关闭。请重新设置。"); }
    }
    public async Task WriteAsync(UpdatePreferences preferences, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using (var output = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(output))
                { writer.WriteStartObject(); writer.WriteBoolean("automaticChecks", preferences.AutomaticChecks); writer.WriteBoolean("backgroundDownload", preferences.BackgroundDownload); writer.WriteBoolean("includePreviews", preferences.IncludePreviews); writer.WriteEndObject(); }
                await File.WriteAllBytesAsync(temporary, output.ToArray(), token).ConfigureAwait(false);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); _gate.Release(); }
    }
}
