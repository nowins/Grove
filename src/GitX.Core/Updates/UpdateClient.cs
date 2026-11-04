using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitX.Core.Updates;

public sealed record DownloadedUpdate(UpdateOffer Offer, string Path);
public sealed class UpdateIntegrityException(string message) : IOException(message);
public sealed class UpdateClient(IUpdateSource source)
{
    public async Task<UpdateOffer?> CheckAsync(UpdateVersion current, bool previews, string rid, CancellationToken token)
    {
        var releases = new List<UpdateRelease>();
        for (var page = 1; page <= 20; page++)
        {
            token.ThrowIfCancellationRequested();
            var bytes = await source.PageAsync(page, token).ConfigureAwait(false);
            var parsed = UpdateRelease.ParsePage(bytes); releases.AddRange(parsed);
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.GetArrayLength() < 50) return UpdateOffer.Select(releases, current, previews, rid);
        }
        throw new IOException("版本列表超过检查上限，未判定为最新版本。请在 GitHub Releases 中查看。");
    }
    public async Task<DownloadedUpdate> DownloadAsync(UpdateOffer offer, string cacheRoot, Action<long>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (UpdateVersion.Parse(offer.Release.Version.ToString()) is null) throw new IOException("版本标识无效。");
        // Reconstruct the selection, rather than trusting a supplied file name/path.
        var selected = UpdateOffer.Select(new[] { offer.Release }, offer.Release.Version, true, offer.Rid);
        if (selected?.Package != offer.Package || selected.Checksums != offer.Checksums) throw new IOException("安装包选择已失效。");
        using var manifest = new MemoryStream();
        await source.DownloadAsync(offer.Checksums, manifest, null, token).ConfigureAwait(false);
        VerifyBytes(manifest.ToArray(), offer.Checksums);
        try { VerifyManifest(new UTF8Encoding(false, true).GetString(manifest.ToArray()), offer.Package); }
        catch (DecoderFallbackException) { throw new IOException("校验清单不是有效 UTF-8 文本。"); }
        var root = Path.GetFullPath(cacheRoot); Directory.CreateDirectory(root); RejectLink(root);
        var directory = Path.Combine(root, $"{offer.Release.Version}-{offer.Rid}-{offer.Package.Id}-{offer.Package.Sha256[..12]}");
        Directory.CreateDirectory(directory); RejectLink(directory);
        var final = Path.Combine(directory, offer.Package.Name); var part = final + "." + Guid.NewGuid().ToString("N") + ".part";
        if (File.Exists(final))
        {
            RejectLink(final);
            try
            {
                await VerifyFileAsync(new(offer, final), root, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested(); progress?.Invoke(offer.Package.Size); return new(offer, final);
            }
            catch (UpdateIntegrityException)
            {
                // Keep the invalid cache for inspection; a retry downloads fresh
                // bytes instead of requiring the user to delete files manually.
                File.Move(final, final + ".invalid-" + Guid.NewGuid().ToString("N"), false);
            }
        }
        try
        {
            await using (var stream = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
            { await source.DownloadAsync(offer.Package, stream, progress, token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false); }
            await VerifyFileAsync(new(offer, part), root, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            try { File.Move(part, final, false); }
            catch (IOException) when (File.Exists(final)) { await VerifyFileAsync(new(offer, final), root, token).ConfigureAwait(false); }
            return new(offer, final);
        }
        finally { if (File.Exists(part)) File.Delete(part); } // This request's unique partial only.
    }
    public static void VerifyBytes(byte[] bytes, UpdateAsset asset)
    {
        if (bytes.LongLength != asset.Size || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(asset.Sha256))) throw new IOException("GitHub 资源 SHA-256 校验失败，文件未被采用。");
    }
    public static void VerifyManifest(string text, UpdateAsset package)
    {
        if (text.Length > 128 * 1024 || text.Contains('\0')) throw new IOException("校验清单无效。");
        var hashes = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length >= 67 && line[64] == ' ' && line[65] is ' ' or '*' && line[66..] == package.Name) hashes.Add(line[..64].ToLowerInvariant());
        }
        if (hashes.Count != 1 || hashes[0] != package.Sha256) throw new IOException("安装包与 SHA256SUMS.txt 不匹配，已停止下载。");
    }
    public static async Task VerifyFileAsync(DownloadedUpdate downloaded, string cacheRoot, CancellationToken token)
    {
        var path = Path.GetFullPath(downloaded.Path); var root = Path.GetFullPath(cacheRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("安装包不在更新缓存中。");
        for (var parent = Path.GetDirectoryName(path); parent is not null && parent.Length >= root.Length - 1; parent = Path.GetDirectoryName(parent)) RejectLink(parent);
        RejectLink(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        if (stream.Length != downloaded.Offer.Package.Size || !CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false), Convert.FromHexString(downloaded.Offer.Package.Sha256))) throw new UpdateIntegrityException("缓存安装包已损坏或被修改，未启动安装。重新下载会保留损坏副本并取得新文件。");
    }
    private static void RejectLink(string path)
    { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("更新缓存不能是符号链接或目录连接。"); }
}
