using System.Net;
using System.Text;
using GitX.Core.Updates;
using Xunit;

namespace GitX.Core.Tests;

public sealed class HttpUpdateSourceTests
{
    [Fact]
    public async Task Metadata_uses_only_the_public_official_endpoint_without_credentials()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            calls++; Assert.Equal("https://api.github.com/repos/nowins/Grove/releases?per_page=50&page=1", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie")); Assert.Null(request.Headers.Referrer);
            return new(HttpStatusCode.OK) { Content = new StringContent("[]") };
        }));
        Assert.Equal("[]", Encoding.UTF8.GetString(await new GitHubUpdateSource(client).PageAsync(1, default))); Assert.Equal(1, calls);
    }
    [Fact]
    public async Task GitHub_asset_redirect_can_stream_without_login_or_secret_headers()
    {
        var calls = 0; var bytes = Encoding.UTF8.GetBytes("test payload");
        using var client = new HttpClient(new Handler(request =>
        {
            calls++; Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("Cookie")); Assert.Null(request.Headers.Referrer);
            if (calls == 1) { var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new("https://release-assets.githubusercontent.com/resource?temporary=value"); return response; }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        using var destination = new MemoryStream();
        await new GitHubUpdateSource(client).DownloadAsync(new(17, "test.exe", bytes.Length, new string('a', 64)), destination, null, default);
        Assert.Equal(bytes, destination.ToArray()); Assert.Equal(2, calls);
    }
    [Theory]
    [InlineData("http://release-assets.githubusercontent.com/file")]
    [InlineData("https://evil.example/file")]
    [InlineData("https://release-assets.githubusercontent.com.evil.example/file")]
    [InlineData("https://name:password@release-assets.githubusercontent.com/file")]
    [InlineData("https://release-assets.githubusercontent.com:444/file")]
    [InlineData("https://github.com/login")]
    [InlineData("https://api.github.com/repos/other/project/releases/assets/17")]
    public async Task Unexpected_redirect_is_blocked_before_another_request(string location)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(_ => { calls++; var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new(location); return response; }));
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<IOException>(() => new GitHubUpdateSource(client).DownloadAsync(new(17, "test.exe", 1, new string('a', 64)), output, null, default)); Assert.Equal(1, calls); Assert.Equal(0, output.Length);
    }
    [Theory]
    [InlineData(403)] [InlineData(404)] [InlineData(429)] [InlineData(500)]
    public async Task Access_or_network_failure_is_not_reported_as_no_updates(int status)
    {
        using var client = new HttpClient(new Handler(_ => new((HttpStatusCode)status)));
        await Assert.ThrowsAsync<IOException>(() => new GitHubUpdateSource(client).PageAsync(1, default));
    }
    [Fact]
    public async Task Metadata_redirect_and_oversized_asset_are_rejected()
    {
        using var redirect = new HttpClient(new Handler(_ => { var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new("https://api.github.com/repos/other/repo/releases"); return response; }));
        await Assert.ThrowsAsync<IOException>(() => new GitHubUpdateSource(redirect).PageAsync(1, default));
        using var oversized = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2]) }));
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<IOException>(() => new GitHubUpdateSource(oversized).DownloadAsync(new(17, "test.exe", 1, new string('a', 64)), stream, null, default)); Assert.Equal(0, stream.Length);
    }
    [Fact]
    public async Task Redirect_loops_are_bounded()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(_ => { calls++; var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new("https://release-assets.githubusercontent.com/resource"); return response; }));
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<IOException>(() => new GitHubUpdateSource(client).DownloadAsync(new(17, "test.exe", 1, new string('a', 64)), output, null, default)); Assert.Equal(4, calls);
    }
    [Fact]
    public async Task Cancellation_is_honored_and_errors_do_not_expose_download_urls()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var client = new HttpClient(new Handler(_ => throw new HttpRequestException("private signed download URL")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitHubUpdateSource(client).PageAsync(1, cancellation.Token));
        var error = await Assert.ThrowsAsync<IOException>(() => new GitHubUpdateSource(client).PageAsync(1, default)); Assert.DoesNotContain("signed", error.Message);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(response(request)); }
    }
}
