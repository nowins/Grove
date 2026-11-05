using System.Net;
using System.Net.Http.Headers;
using GitX.Core.Updates;
using Xunit;

namespace GitX.Core.Tests;

public sealed class UpdateRequestBackoffTests
{
    [Fact]
    public void Unknown_403_does_not_claim_exhaustion_or_block_retry()
    {
        var backoff = new UpdateRequestBackoff();
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("secret proxy HTML") };
        var error = backoff.Restriction("api.github.com", response);
        Assert.Contains("尚不能确认", error.Message);
        Assert.DoesNotContain("secret", error.Message);
        backoff.ThrowIfWaiting("api.github.com");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Retry_after_blocks_new_sources_then_recovers(bool date)
    {
        var clock = new Clock(); var backoff = new UpdateRequestBackoff(clock); var calls = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            calls++;
            var response = new HttpResponseMessage(calls == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
            if (calls == 1) response.Headers.RetryAfter = date ? new RetryConditionHeaderValue(clock.Now.AddSeconds(120)) : new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            else response.Content = new StringContent("[]");
            return response;
        }));
        await Assert.ThrowsAsync<IOException>(() => new GitHubUpdateSource(client, backoff).PageAsync(1, default));
        clock.Now = clock.Now.AddSeconds(119);
        await Assert.ThrowsAsync<IOException>(() => new GitHubUpdateSource(client, backoff).PageAsync(1, default));
        Assert.Equal(1, calls);
        clock.Now = clock.Now.AddSeconds(1);
        await new GitHubUpdateSource(client, backoff).PageAsync(1, default);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Exhausted_api_uses_later_reset_and_isolates_hosts()
    {
        var clock = new Clock(); var backoff = new UpdateRequestBackoff(clock);
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("X-RateLimit-Reset", clock.Now.AddMinutes(3).ToUnixTimeSeconds().ToString());
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
        Assert.Contains("匿名 API 额度已耗尽", backoff.Restriction("api.github.com", response).Message);
        clock.Now = clock.Now.AddMinutes(2);
        Assert.Throws<IOException>(() => backoff.ThrowIfWaiting("api.github.com"));
        backoff.ThrowIfWaiting("release-assets.githubusercontent.com");
        clock.Now = clock.Now.AddMinutes(1);
        backoff.ThrowIfWaiting("api.github.com");
    }

    [Theory]
    [InlineData("invalid")] [InlineData("-1")] [InlineData("99999999999999999999999")] [InlineData("0")]
    public void Invalid_or_past_deadlines_use_one_minute(string header)
    {
        var clock = new Clock(); var backoff = new UpdateRequestBackoff(clock);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", header);
        response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
        response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", header);
        backoff.Restriction("api.github.com", response);
        Assert.Throws<IOException>(() => backoff.ThrowIfWaiting("api.github.com"));
        clock.Now = clock.Now.AddMinutes(1);
        backoff.ThrowIfWaiting("api.github.com");
    }

    [Fact]
    public async Task Cancellation_wins_over_cooldown()
    {
        var backoff = new UpdateRequestBackoff();
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        backoff.Restriction("api.github.com", response);
        using var client = new HttpClient(new Handler(_ => throw new Exception("must not send")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitHubUpdateSource(client, backoff).PageAsync(1, new CancellationToken(true)));
    }

    [Fact]
    public void Asset_host_cannot_claim_api_quota_and_deadline_never_shortens()
    {
        var clock = new Clock(); var backoff = new UpdateRequestBackoff(clock);
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("X-RateLimit-Remaining", "0");
        Assert.Contains("尚不能确认", backoff.Restriction("release-assets.githubusercontent.com", response).Message);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
        backoff.Restriction("api.github.com", response);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
        backoff.Restriction("api.github.com", response);
        clock.Now = clock.Now.AddMinutes(2);
        Assert.Throws<IOException>(() => backoff.ThrowIfWaiting("api.github.com"));
    }

    [Fact]
    public async Task Asset_redirect_cooldown_does_not_leak_signed_url()
    {
        var backoff = new UpdateRequestBackoff(new Clock()); var calls = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            calls++;
            if (request.RequestUri!.Host == "api.github.com")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://release-assets.githubusercontent.com/file?secret=never-log");
                return redirect;
            }
            var denied = new HttpResponseMessage(HttpStatusCode.Forbidden);
            denied.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return denied;
        }));
        using var output = new MemoryStream(); var source = new GitHubUpdateSource(client, backoff);
        var asset = new UpdateAsset(17, "package.dmg", 10, new string('a', 64));
        var error = await Assert.ThrowsAsync<IOException>(() => source.DownloadAsync(asset, output, null, default));
        Assert.DoesNotContain("匿名 API", error.Message); Assert.DoesNotContain("secret", error.Message);
        await Assert.ThrowsAsync<IOException>(() => source.DownloadAsync(asset, output, null, default));
        Assert.Equal(3, calls); Assert.Equal(0, output.Length);
    }

    [Fact]
    public void Duplicate_quota_headers_are_not_misclassified()
    {
        var backoff = new UpdateRequestBackoff();
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", new[] { "0", "20" });
        Assert.Contains("尚不能确认", backoff.Restriction("api.github.com", response).Message);
    }

    [Theory]
    [InlineData("linux-x64", false)]
    [InlineData("linux-x64", true)]
    [InlineData("linux-arm64", false)]
    [InlineData("linux-arm64", true)]
    public async Task Linux_manual_updates_stay_off_with_diagnostic_backoff(string rid, bool cooling)
    {
        var clock = new Clock(); var backoff = new UpdateRequestBackoff(clock); var calls = 0;
        if (cooling)
        {
            using var denied = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            backoff.Restriction("api.github.com", denied);
        }
        using var http = new HttpClient(new Handler(_ =>
        { calls++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }; }));
        var client = new UpdateClient(new GitHubUpdateSource(http, backoff));
        var error = await Assert.ThrowsAsync<IOException>(() => client.CheckAsync(UpdateVersion.Parse("0.1.0-preview.5")!, true, rid, default));
        Assert.Contains("手动更新", error.Message);
        Assert.Equal(0, calls);
        if (cooling) Assert.Throws<IOException>(() => backoff.ThrowIfWaiting("api.github.com"));
        else backoff.ThrowIfWaiting("api.github.com");
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    [InlineData("osx-x64")]
    [InlineData("osx-arm64")]
    public async Task Supported_platform_checks_still_use_diagnostic_source(string rid)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Null(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        }));
        var offer = await new UpdateClient(new GitHubUpdateSource(http, new(new Clock())))
            .CheckAsync(UpdateVersion.Parse("0.1.0-preview.5")!, true, rid, default);
        Assert.Null(offer); Assert.Equal(1, calls);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(send(request));
    }
}
