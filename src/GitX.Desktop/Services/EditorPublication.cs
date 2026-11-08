using System.Text.Json;
using GitX.Core;
using GitX.Desktop.ViewModels;

namespace GitX.Desktop.Services;

// One atomic publication owns its metadata and optional source body. It never
// retains the preceding publication or a history of serialized documents.
public sealed class EditorPublication
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record Content(string Id, string Root, string Tab, string Path, string Language, string? Revision, string Text);
    private readonly Content? _content;
    private readonly Lazy<byte[]> _full, _metadata;
    internal MarkdownResourceContext? Markdown { get; }
    internal ContinuousDiffSession? Continuous { get; }

    internal EditorPublication(byte[] bytes, MarkdownResourceContext? markdown = null)
    { Markdown = markdown; _full = new(() => bytes); _metadata = _full; }

    internal EditorPublication(long version, RepositoryDocument document, EditorToolbarState? toolbar,
        MarkdownResourceContext? markdown, string? repository, EditorPublication? previous, ContinuousDiffSession? continuous = null)
    {
        Markdown = markdown;
        Continuous = continuous;
        if (!document.IsNotice && !document.IsDiff && document.Diff is null && !string.IsNullOrEmpty(repository)
            && toolbar is { SourceMode: true, ActiveSourceTabId: { Length: > 0 } tab })
        {
            var old = previous?._content;
            var same = old is not null && old.Root == repository && old.Tab == tab && old.Path == document.Path
                && old.Language == document.Language && old.Revision == document.SourceRevision && old.Text == document.Text;
            // Reuse the identity, but retain this capture's string shared with
            // the native document, not a second equal string from the old one.
            _content = new(same ? old!.Id : Guid.NewGuid().ToString("N"), repository, tab, document.Path, document.Language, document.SourceRevision, document.Text);
        }
        var metadata = _content is null ? document : document with { Text = "" };
        _full = new(() => Serialize(version, metadata, toolbar, reuse: false));
        _metadata = _content is null ? _full : new(() => Serialize(version, metadata, toolbar, reuse: true));
    }

    private byte[] Serialize(long version, RepositoryDocument metadata, EditorToolbarState? toolbar, bool reuse)
    {
        var document = _content is null ? metadata : metadata with { Text = reuse ? "" : _content.Text };
        if (document is { IsDiff: true, Diff: not null, IsNotice: false }) document = document with { Text = "" };
        return JsonSerializer.SerializeToUtf8Bytes(new { version, document, toolbar, contentId = _content?.Id, reuseContent = reuse }, Json);
    }

    internal byte[] Response(string? knownContent)
        => _content is not null && knownContent == _content.Id ? _metadata.Value : _full.Value;
}
