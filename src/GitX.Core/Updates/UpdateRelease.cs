using System.Text.Json;
using System.Text.RegularExpressions;

namespace GitX.Core.Updates;

public sealed record UpdateVersion(int Major, int Minor, int Patch, string[] Pre) : IComparable<UpdateVersion>
{
    public static UpdateVersion? Parse(string? value)
    {
        if (value is null || value.Length > 100) return null;
        var match = Regex.Match(value, @"\Av?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z", RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major) || !int.TryParse(match.Groups[2].Value, out var minor) || !int.TryParse(match.Groups[3].Value, out var patch)) return null;
        var pre = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (pre.Any(s => s.Length > 1 && s.All(char.IsAsciiDigit) && s[0] == '0')) return null;
        return new(major, minor, patch, pre);
    }
    public int CompareTo(UpdateVersion? other)
    {
        if (other is null) return 1;
        foreach (var pair in new[] { (Major, other.Major), (Minor, other.Minor), (Patch, other.Patch) })
            if (pair.Item1.CompareTo(pair.Item2) is var order && order != 0) return order;
        if (Pre.Length == 0 || other.Pre.Length == 0) return other.Pre.Length.CompareTo(Pre.Length);
        for (var i = 0; i < Math.Min(Pre.Length, other.Pre.Length); i++)
        {
            var a = Pre[i]; var b = other.Pre[i]; var an = a.All(char.IsAsciiDigit); var bn = b.All(char.IsAsciiDigit);
            var order = an && bn ? a.Length.CompareTo(b.Length) : an != bn ? an ? -1 : 1 : 0;
            if (order == 0) order = string.CompareOrdinal(a, b);
            if (order != 0) return order;
        }
        return Pre.Length.CompareTo(other.Pre.Length);
    }
    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Pre.Length == 0 ? "" : "-" + string.Join('.', Pre));
}

public sealed record UpdateAsset(long Id, string Name, long Size, string Sha256)
{
    public const long MaxPackageBytes = 512L * 1024 * 1024;
    public bool Valid => Id > 0 && Size is > 0 and <= MaxPackageBytes && Regex.IsMatch(Sha256, @"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant);
}
public sealed record UpdateRelease(long Id, UpdateVersion Version, bool Preview, UpdateAsset[] Assets)
{
    public static IReadOnlyList<UpdateRelease> ParsePage(byte[] json)
    {
        if (json.Length > 4 * 1024 * 1024) throw new IOException("版本列表过大，已停止检查。");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 50) throw new IOException("GitHub 版本列表格式无效。");
        var releases = new List<UpdateRelease>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new IOException("GitHub 版本条目格式无效。");
            if (!item.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False) continue;
            if (!item.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String || UpdateVersion.Parse(tag.GetString()) is not { } version) continue;
            if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var releaseId) || releaseId <= 0) throw new IOException("版本标识无效，未判定为最新版本。");
            if (!item.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() > 200) throw new IOException("版本资源列表无效。");
            var parsed = new List<UpdateAsset>();
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object) throw new IOException("安装包资源格式无效。");
                if (!asset.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String || state.GetString() != "uploaded") continue;
                if (!asset.TryGetProperty("id", out var aid) || aid.ValueKind != JsonValueKind.Number || !aid.TryGetInt64(out var assetId) || !asset.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var bytes)) continue;
                if (!asset.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || !asset.TryGetProperty("digest", out var digest) || digest.ValueKind != JsonValueKind.String) continue;
                var text = digest.GetString() ?? "";
                var result = new UpdateAsset(assetId, name.GetString() ?? "", bytes, text.StartsWith("sha256:", StringComparison.Ordinal) ? text[7..].ToLowerInvariant() : "");
                if (result.Valid) parsed.Add(result);
            }
            var preview = version.Pre.Length > 0 || item.TryGetProperty("prerelease", out var flag) && flag.ValueKind == JsonValueKind.True;
            releases.Add(new(releaseId, version, preview, parsed.ToArray()));
        }
        return releases;
    }
}

public sealed record UpdateOffer(UpdateRelease Release, UpdateAsset Package, UpdateAsset Checksums, string Rid, bool IsNewer)
{
    public static string DetectRid(string os, string architecture) => (os, architecture) switch
    { ("windows", "x64") => "win-x64", ("windows", "arm64") => "win-arm64", ("macos", "x64") => "osx-x64", ("macos", "arm64") => "osx-arm64", _ => throw new IOException("当前操作系统或架构暂不支持在线更新。") };
    public static bool SupportsRid(string rid) => rid is "win-x64" or "win-arm64" or "osx-x64" or "osx-arm64";
    public static UpdateOffer? Select(IEnumerable<UpdateRelease> releases, UpdateVersion current, bool previews, string rid)
    {
        if (!SupportsRid(rid)) throw new IOException("不支持的安装包架构。");
        var release = releases.Where(r => (previews || !r.Preview) && r.Version.CompareTo(current) >= 0).OrderByDescending(r => r.Version).FirstOrDefault();
        if (release is null) return null;
        var name = $"Grove-{release.Version}-{rid}" + (rid.StartsWith("win-", StringComparison.Ordinal) ? "-setup.exe" : ".dmg");
        UpdateAsset Unique(string expected)
        {
            var matches = release.Assets.Where(a => a.Name == expected && a.Valid).ToArray();
            return matches.Length == 1 ? matches[0] : throw new IOException($"版本 {release.Version} 缺少唯一、有效的 {expected}，已停止下载。");
        }
        var package = Unique(name); var checksums = Unique("SHA256SUMS.txt");
        if (checksums.Size > 128 * 1024) throw new IOException("校验清单过大。");
        return new(release, package, checksums, rid, release.Version.CompareTo(current) > 0);
    }
}
