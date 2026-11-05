using System.Globalization;
using System.Net;

namespace GitX.Core.Updates;

// Only transient response metadata, never response bodies or signed URLs.
internal sealed class UpdateRequestBackoff(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, DateTimeOffset> _until = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private const string Recovery = "可点击“在浏览器查看正式 Releases”手动下载安装包。请求失败不表示没有更新。";

    internal void ThrowIfWaiting(string host)
    {
        lock (_gate)
        {
            if (!_until.TryGetValue(host, out var until)) return;
            if (_clock.GetUtcNow() < until) throw Waiting(until);
            _until.Remove(host);
        }
    }

    internal IOException Restriction(string host, HttpResponseMessage response)
    {
        var now = _clock.GetUtcNow();
        DateTimeOffset? retry = null;
        if (response.Headers.RetryAfter is { } after)
        {
            if (after.Date is { } date) retry = date;
            else if (after.Delta is { } delta && delta >= TimeSpan.Zero)
                retry = delta > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delta;
        }
        var exhausted = host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            && Header(response, "X-RateLimit-Remaining") == "0";
        if (exhausted && long.TryParse(Header(response, "X-RateLimit-Reset"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && seconds is >= 0 and <= 253402300799)
        {
            var reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (retry is null || reset > retry) retry = reset;
        }
        if (response.StatusCode != HttpStatusCode.TooManyRequests && !exhausted && retry is null)
            return new IOException("更新请求被拒绝（HTTP 403），尚不能确认是限流。请检查网络或代理是否允许访问 GitHub。" + Recovery);

        var until = retry is { } deadline && deadline > now ? deadline : now.AddMinutes(1);
        lock (_gate)
        {
            if (_until.TryGetValue(host, out var existing) && existing > until) until = existing;
            _until[host] = until;
        }
        var reason = exhausted ? "GitHub 匿名 API 额度已耗尽。" : "更新请求暂受限制（HTTP " + (int)response.StatusCode + "）。";
        return new IOException(reason + Waiting(until).Message);
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) && values.Take(2).ToArray() is { Length: 1 } single ? single[0] : null;
    private static IOException Waiting(DateTimeOffset until) => new(
        $"请在 {until.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC 后重试；等待期间不会向同一主机重复请求。" + Recovery);
}
