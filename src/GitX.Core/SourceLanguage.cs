using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitX.Core;

// Shared metadata is embedded for native callers and bundled for both Web views.
// Grammar selection is path-sensitive for TSX while Monaco uses "typescript".
public static partial class SourceLanguage
{
    private sealed record Definition(string Language, string FileType, string? Grammar, string[] Extensions);
    [JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(Definition[]))]
    private partial class LanguageJsonContext : JsonSerializerContext { }
    private static readonly IReadOnlyDictionary<string, Definition> Extensions = Read();
    private static readonly HashSet<string> StructureLanguages = Extensions.Values.Where(d => d.Grammar is not null)
        .Select(d => d.Language).ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, Definition> Read()
    {
        using var stream = typeof(SourceLanguage).Assembly.GetManifestResourceStream("GitX.Core.SourceLanguages.json")
            ?? throw new InvalidOperationException("缺少内置语言定义。");
        var definitions = JsonSerializer.Deserialize(stream, LanguageJsonContext.Default.DefinitionArray)
            ?? throw new InvalidOperationException("内置语言定义无效。");
        return definitions.SelectMany(d => d.Extensions.Select(extension => (extension, d)))
            .ToDictionary(pair => pair.extension, pair => pair.d, StringComparer.OrdinalIgnoreCase);
    }

    private static Definition? Find(string path) => Extensions.GetValueOrDefault(Path.GetExtension(path));
    public static string Identify(string path) => Find(path)?.Language ?? "plaintext";
    public static string FileType(string path) => Find(path)?.FileType ?? "≡";
    public static string? StructureGrammar(string path) => Find(path)?.Grammar;
    public static bool SupportsStructure(string? language) => language is not null && StructureLanguages.Contains(language);
}
