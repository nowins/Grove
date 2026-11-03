using LibGit2Sharp;

namespace GitX.Core;

public sealed record MarkdownLink(string Path, string Fragment);
public sealed record MarkdownAsset(byte[] Bytes, string ContentType);

// Repository-relative resources only. Callers supply the published document's
// root/revision; neither value comes from HTML or a browser request.
public static class MarkdownResources
{
    public const int MaxImageBytes = 8 * 1024 * 1024;

    public static Uri? ExternalLink(string href)
        => href.Length <= 4096 && href == href.Trim() && !href.Any(char.IsControl) && !href.Contains('\\')
            && Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.IsWellFormedOriginalString()
            && uri.Scheme is "https" or "http" && uri.Host.Length > 0 && uri.UserInfo.Length == 0 ? uri : null;

    public static MarkdownLink Resolve(string documentPath, string href)
    {
        if (href.Length > 4096 || href.Any(char.IsControl) || href != href.Trim() || href.Contains('\\'))
            throw new IOException("Markdown 链接无效。");
        var hash = href.IndexOf('#');
        var rawPath = hash < 0 ? href : href[..hash];
        var fragment = hash < 0 ? "" : Decode(href[(hash + 1)..]);
        var path = Decode(rawPath);
        if (path.StartsWith('/') || path.Contains(':') || path.Contains('?') || path.Contains('\\') || path.Any(char.IsControl) || fragment.Any(char.IsControl))
            throw new IOException("仅支持仓库内的相对文件链接。");
        var parts = new List<string>();
        var relative = path.Length == 0 ? documentPath : documentPath[..(documentPath.LastIndexOf('/') + 1)] + path;
        foreach (var part in relative.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (parts.Count == 0) throw new IOException("链接超出仓库范围。"); parts.RemoveAt(parts.Count - 1); }
            else
            {
                // Git metadata and Windows alternate streams/device names are
                // never document resources, on any platform.
                var stem = part.Split('.')[0].ToUpperInvariant();
                if (part.Equals(".git", StringComparison.OrdinalIgnoreCase) || part.EndsWith('.') || part.EndsWith(' ')
                    || part.IndexOfAny([':', '\\', '<', '>', '"', '|', '?', '*', '\0']) >= 0
                    || stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]))
                    throw new IOException("链接指向不受支持的路径。");
                parts.Add(part);
            }
        }
        if (parts.Count == 0) throw new IOException("链接没有文件路径。");
        return new(string.Join('/', parts), fragment);
    }

    private static string Decode(string value)
    {
        for (var i = 0; i < value.Length; i++)
            if (value[i] == '%' && (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2])))
                throw new IOException("链接包含无效编码。");
        return Uri.UnescapeDataString(value);
    }

    public static Task<MarkdownAsset> ReadImageAsync(string root, string documentPath, string? revision, string href, CancellationToken cancellation = default)
        => Task.Run(() => ReadImage(root, documentPath, revision, href, cancellation), cancellation);

    private static MarkdownAsset ReadImage(string root, string documentPath, string? revision, string href, CancellationToken token)
    {
        var link = Resolve(documentPath, href);
        if (link.Fragment.Length != 0) throw new IOException("图片链接不能包含锚点。");
        var mime = System.IO.Path.GetExtension(link.Path).ToLowerInvariant() switch
        { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", _ => throw new IOException("仅支持 PNG、JPEG、GIF 和 WebP 图片。") };
        token.ThrowIfCancellationRequested();
        byte[] bytes;
        if (revision is not null)
        {
            if (revision.Length != 40 || !revision.All(Uri.IsHexDigit)) throw new IOException("历史修订无效。");
            using var repo = new Repository(root);
            var entry = repo.Lookup<Commit>(revision)?.Tree[link.Path];
            if (entry is null || entry.Mode == Mode.SymbolicLink || entry.Target is not Blob blob || blob.Size > MaxImageBytes)
                throw new IOException("该修订中没有可预览的图片，或图片超过 8 MiB。");
            using var stream = blob.GetContentStream(); bytes = ReadBounded(stream, token);
        }
        else
        {
            var path = RepositoryReader.SafeWorktreePath(root, link.Path);
            for (FileSystemInfo? cursor = new FileInfo(path); cursor is not null; cursor = Directory.GetParent(cursor.FullName))
                if ((cursor.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("不读取符号链接或目录联接中的图片。");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bytes = ReadBounded(stream, token);
        }
        if (!MatchesImage(bytes, mime)) throw new IOException("图片内容与文件类型不符。");
        return new(bytes, mime);
    }

    private static byte[] ReadBounded(Stream stream, CancellationToken token)
    {
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = stream.Read(buffer)) > 0)
        { token.ThrowIfCancellationRequested(); if (output.Length + count > MaxImageBytes) throw new IOException("图片超过 8 MiB。"); output.Write(buffer, 0, count); }
        return output.ToArray();
    }

    private static bool MatchesImage(byte[] bytes, string mime) => mime switch
    {
        "image/png" => bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "image/jpeg" => bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }),
        "image/gif" => bytes.AsSpan().StartsWith("GIF87a"u8) || bytes.AsSpan().StartsWith("GIF89a"u8),
        "image/webp" => bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
        _ => false
    };
}
