using System.Text.Json;

namespace GitX.Desktop.Services;

public sealed record BlameViewSettings(bool Revision = true, bool Author = true, bool Date = false, bool CommitNumber = false)
{
    public BlameViewSettings Change(string field, bool value)
    {
        var result = field switch
        {
            "revision" => this with { Revision = value }, "author" => this with { Author = value },
            "date" => this with { Date = value }, "commitNumber" => this with { CommitNumber = value },
            _ => throw new IOException("未知的注解显示字段。")
        };
        if (!result.Revision && !result.Author && !result.Date && !result.CommitNumber) throw new IOException("至少保留一个注解显示字段。");
        return result;
    }
}

internal sealed class BlameViewStore(string directory)
{
    private readonly SemaphoreSlim _gate = new(1);
    private string FilePath => Path.Combine(directory, "blame-view.json");
    public Task<BlameViewSettings> ReadAsync() => AccessAsync(null, false);
    public Task<BlameViewSettings> UpdateAsync(string field, bool value) => AccessAsync(field, value);
    private async Task<BlameViewSettings> AccessAsync(string? field, bool value)
    {
        await _gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                BlameViewSettings settings;
                try { settings = File.Exists(FilePath) ? JsonSerializer.Deserialize<BlameViewSettings>(File.ReadAllText(FilePath)) ?? new() : new(); }
                catch (JsonException) { settings = new(); }
                if (!settings.Revision && !settings.Author && !settings.Date && !settings.CommitNumber) settings = new();
                if (field is null) return settings;
                settings = settings.Change(field, value);
                Directory.CreateDirectory(directory);
                var temporary = FilePath + "." + Guid.NewGuid().ToString("N");
                try { File.WriteAllText(temporary, JsonSerializer.Serialize(settings)); File.Move(temporary, FilePath, true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return settings;
            });
        }
        finally { _gate.Release(); }
    }
}
