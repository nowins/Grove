using System.Net;
using System.Net.Http.Headers;

namespace GitX.Core.Updates;

public interface IUpdateSource
{
    Task<byte[]> PageAsync(int page, CancellationToken token);
    Task DownloadAsync(UpdateAsset asset, Stream destination, Action<long>? progress, CancellationToken token);
}

// Fixed public source; no auth, cookies or persistent signed download URLs.
public sealed class GitHubUpdateSource : IUpdateSource
{
    public const string Repository = "nowins/Grove";
    public const string ReleasesUrl = "https://github.com/nowins/Grove/releases";
    private static readonly HttpClient Shared = new(new SocketsHttpHandler
    { AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    private readonly HttpClient _http;
    public GitHubUpdateSource() => _http = Shared;
    internal GitHubUpdateSource(HttpClient http) => _http = http;
    public async Task<byte[]> PageAsync(int page, CancellationToken token)
    {
        if (page is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(page));
        using var buffer = new MemoryStream();
        await ReadAsync($"https://api.github.com/repos/{Repository}/releases?per_page=50&page={page}", false, buffer, 4 * 1024 * 1024, null, token).ConfigureAwait(false);
        return buffer.ToArray();
    }
    public Task DownloadAsync(UpdateAsset asset, Stream destination, Action<long>? progress, CancellationToken token)
    {
        if (!asset.Valid) throw new IOException("安装包元数据无效。");
        return ReadAsync($"https://api.github.com/repos/{Repository}/releases/assets/{asset.Id}", true, destination, asset.Size, progress, token);
    }
    private async Task ReadAsync(string endpoint, bool binary, Stream destination, long limit, Action<long>? progress, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(binary ? TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(2));
        var original = new Uri(endpoint); var current = original;
        try
        {
            for (var hop = 0; hop < 4; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Grove-Desktop-Updater", "1.0"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(binary ? "application/octet-stream" : "application/vnd.github+json"));
                if (current.Host == "api.github.com") request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (!binary || hop == 3 || response.Headers.Location is not { } location) throw new IOException("更新源发生无法验证的重定向，已停止请求。");
                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (!AllowedRedirect(original, next)) throw new IOException("下载重定向不属于受支持的 GitHub 资产来源，已停止。");
                    current = next; continue;
                }
                if ((int)response.StatusCode is 403 or 429) throw new IOException("GitHub 请求受限，请稍后重试。公开 API 的匿名访问有频率限制；请求失败不表示没有更新。");
                if (response.StatusCode == HttpStatusCode.NotFound) throw new IOException("更新源或资源不可访问。仓库可能尚未公开，或版本资源已移除；请查看正式 Releases 页面。");
                if (response.StatusCode != HttpStatusCode.OK) throw new IOException("更新服务器请求失败，请稍后重试。");
                if (response.Content.Headers.ContentLength is { } length && (length > limit || binary && length != limit)) throw new IOException("下载大小与资源声明不符，已停止。");
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                var bytes = new byte[64 * 1024]; long total = 0;
                while (true)
                {
                    var read = await input.ReadAsync(bytes, timeout.Token).ConfigureAwait(false); if (read == 0) break;
                    total += read; if (total > limit) throw new IOException("下载内容超过声明大小，已停止。");
                    await destination.WriteAsync(bytes.AsMemory(0, read), timeout.Token).ConfigureAwait(false); progress?.Invoke(total);
                }
                timeout.Token.ThrowIfCancellationRequested();
                if (binary && total != limit) throw new IOException("安装包下载不完整，可重试。");
                return;
            }
            throw new IOException("下载重定向次数过多。");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("更新请求超时，可稍后重试。"); }
        catch (HttpRequestException) { throw new IOException("无法连接公开 GitHub 更新源。请检查网络后重试。"); }
        catch (UriFormatException) { throw new IOException("更新源返回了无效的重定向地址。"); }
    }
    private static bool AllowedRedirect(Uri original, Uri next)
    {
        if (next.Scheme != Uri.UriSchemeHttps || next.Port != 443 || next.UserInfo.Length > 0 || next.Fragment.Length > 0 || next.AbsoluteUri.Length > 16384) return false;
        return next.Host switch
        {
            "api.github.com" => next.AbsolutePath == original.AbsolutePath,
            "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com" => true,
            "github.com" => next.AbsolutePath.StartsWith("/nowins/Grove/releases/download/", StringComparison.Ordinal),
            _ => false
        };
    }
}
