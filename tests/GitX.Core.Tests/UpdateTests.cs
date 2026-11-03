using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitX.Core.Updates;
using GitX.Desktop.Services;
using Xunit;

namespace GitX.Core.Tests;

public sealed class UpdateTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0-preview.2")]
    [InlineData("1.0.0-preview.10", "1.0.0-preview.2")]
    [InlineData("1.0.0-beta", "1.0.0-999999999999999999999999999")]
    [InlineData("1.0.0-preview.2.1", "1.0.0-preview.2")]
    [InlineData("1.1.0", "1.0.99")]
    [InlineData("2.0.0", "1.99.99")]
    public void Versions_follow_semantic_precedence(string newer, string older)
    { Assert.True(UpdateVersion.Parse(newer)!.CompareTo(UpdateVersion.Parse(older)) > 0); }
    [Theory]
    [InlineData("01.0.0")] [InlineData("1.0")] [InlineData("1.0.0-preview.01")]
    [InlineData("1.0.0\n")] [InlineData("1.0.0/../evil")] [InlineData("2147483648.0.0")]
    public void Invalid_versions_are_rejected(string text) => Assert.Null(UpdateVersion.Parse(text));
    [Fact]
    public void Build_metadata_and_v_prefix_do_not_change_precedence()
    { Assert.Equal(0, UpdateVersion.Parse("v1.2.3+build.17")!.CompareTo(UpdateVersion.Parse("1.2.3"))); }
    [Theory]
    [InlineData("windows", "x64", "win-x64")] [InlineData("windows", "arm64", "win-arm64")]
    [InlineData("macos", "x64", "osx-x64")] [InlineData("macos", "arm64", "osx-arm64")]
    public void Packages_use_native_OS_architecture(string os, string architecture, string rid) => Assert.Equal(rid, UpdateOffer.DetectRid(os, architecture));
    [Fact]
    public void Unknown_platform_is_not_guessed() => Assert.Throws<IOException>(() => UpdateOffer.DetectRid("windows", "x86"));
    [Fact]
    public void Channel_selection_excludes_previews_and_downgrades_but_allows_explicit_current_repair()
    {
        var stable = Fixture.Release("1.0.0"); var preview = Fixture.Release("1.1.0-preview.1");
        Assert.Equal("1.0.0", UpdateOffer.Select([stable, preview], UpdateVersion.Parse("1.0.0")!, false, "win-x64")!.Release.Version.ToString());
        Assert.True(UpdateOffer.Select([stable, preview], UpdateVersion.Parse("1.0.0")!, true, "win-x64")!.IsNewer);
        Assert.False(UpdateOffer.Select([stable], stable.Version, false, "win-x64")!.IsNewer);
        Assert.Null(UpdateOffer.Select([stable], UpdateVersion.Parse("2.0.0")!, true, "win-x64"));
    }
    [Fact]
    public void Broken_newest_release_does_not_silently_fall_back_to_an_older_installer()
    {
        var broken = Fixture.Release("2.0.0") with { Assets = [] };
        Assert.Throws<IOException>(() => UpdateOffer.Select([Fixture.Release("1.0.0"), broken], UpdateVersion.Parse("1.0.0")!, true, "win-x64"));
        var valid = Fixture.Release("2.0.0");
        Assert.Throws<IOException>(() => UpdateOffer.Select([valid with { Assets = [.. valid.Assets, valid.Assets[0]] }], valid.Version, true, "win-x64"));
    }
    [Fact]
    public void Drafts_and_invalid_asset_digests_cannot_become_downloads()
    {
        var json = Encoding.UTF8.GetBytes("[{\"id\":1,\"tag_name\":\"v9.0.0\",\"draft\":true,\"assets\":[]},{\"id\":2,\"tag_name\":\"v1.0.0\",\"draft\":false,\"prerelease\":false,\"assets\":[{\"id\":3,\"name\":\"Grove-1.0.0-win-x64-setup.exe\",\"size\":10,\"state\":\"uploaded\",\"digest\":\"md5:abc\"}]}]");
        var release = Assert.Single(UpdateRelease.ParsePage(json)); Assert.Empty(release.Assets);
        Assert.Throws<IOException>(() => UpdateOffer.Select([release], release.Version, true, "win-x64"));
    }
    [Theory]
    [InlineData("{}")] [InlineData("[null]")]
    [InlineData("[{\"draft\":false,\"tag_name\":\"v1.0.0\",\"id\":\"bad\",\"assets\":[]}]")]
    public void Malformed_feed_is_an_error_not_no_updates(string json) => Assert.Throws<IOException>(() => UpdateRelease.ParsePage(Encoding.UTF8.GetBytes(json)));
    [Fact]
    public async Task Check_pages_until_completion_and_includes_releases_beyond_the_first_page()
    {
        var source = new FakeSource();
        source.Pages.Add(JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range(1, 50).Select(i => new { draft = true })));
        source.Pages.Add(Fixture.Page(Fixture.Release("2.0.0")));
        var result = await new UpdateClient(source).CheckAsync(UpdateVersion.Parse("1.0.0")!, true, "win-x64", default);
        Assert.Equal("2.0.0", result!.Release.Version.ToString()); Assert.Equal(2, source.PageCalls);
    }
    [Fact]
    public async Task Pagination_limit_is_visible_and_cancellation_is_honored()
    {
        var source = new FakeSource { RepeatPage = JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range(1, 50).Select(i => new { draft = true })) };
        await Assert.ThrowsAsync<IOException>(() => new UpdateClient(source).CheckAsync(UpdateVersion.Parse("1.0.0")!, true, "win-x64", default)); Assert.Equal(20, source.PageCalls);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UpdateClient(source).CheckAsync(UpdateVersion.Parse("1.0.0")!, true, "win-x64", cancelled.Token)); Assert.Equal(20, source.PageCalls);
    }
    [Fact]
    public async Task Download_checks_both_digests_reuses_valid_cache_and_rehashes_before_handoff()
    {
        using var fixture = new Fixture(); var client = new UpdateClient(fixture.Source); var progress = new List<long>();
        var downloaded = await client.DownloadAsync(fixture.Offer, fixture.Cache, progress.Add, default);
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(downloaded.Path)); Assert.Contains((long)fixture.Bytes.Length, progress);
        Assert.Equal(downloaded, await client.DownloadAsync(fixture.Offer, fixture.Cache, null, default)); Assert.Equal(1, fixture.Source.PackageCalls);
        await File.WriteAllTextAsync(downloaded.Path, "tampered");
        await Assert.ThrowsAsync<UpdateIntegrityException>(() => UpdateClient.VerifyFileAsync(downloaded, fixture.Cache, default));
        var repaired = await client.DownloadAsync(fixture.Offer, fixture.Cache, null, default);
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(repaired.Path)); Assert.Equal(2, fixture.Source.PackageCalls);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(repaired.Path)!, "*.invalid-*"));
    }
    [Fact]
    public async Task Cancellation_removes_only_own_partial_and_can_retry()
    {
        using var fixture = new Fixture(); using var cancellation = new CancellationTokenSource();
        fixture.Source.InterruptPackage = async (stream, token) => { await stream.WriteAsync(new byte[] { 1, 2 }, token); cancellation.Cancel(); token.ThrowIfCancellationRequested(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new UpdateClient(fixture.Source).DownloadAsync(fixture.Offer, fixture.Cache, null, cancellation.Token));
        Assert.Empty(Directory.GetFiles(fixture.Cache, "*.part", SearchOption.AllDirectories));
        fixture.Source.InterruptPackage = null;
        Assert.True(File.Exists((await new UpdateClient(fixture.Source).DownloadAsync(fixture.Offer, fixture.Cache, null, default)).Path));
    }
    [Fact]
    public async Task Incorrect_remote_manifest_or_package_is_never_promoted()
    {
        using var fixture = new Fixture(); var original = fixture.Source.Data[102];
        fixture.Source.Data[102] = Encoding.UTF8.GetBytes("bad manifest");
        await Assert.ThrowsAsync<IOException>(() => new UpdateClient(fixture.Source).DownloadAsync(fixture.Offer, fixture.Cache, null, default));
        fixture.Source.Data[102] = original; fixture.Source.Data[101] = Encoding.UTF8.GetBytes("bad payload");
        await Assert.ThrowsAsync<UpdateIntegrityException>(() => new UpdateClient(fixture.Source).DownloadAsync(fixture.Offer, fixture.Cache, null, default));
        Assert.Empty(Directory.GetFiles(fixture.Cache, "*.exe", SearchOption.AllDirectories)); Assert.Empty(Directory.GetFiles(fixture.Cache, "*.part", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task Concurrent_downloads_converge_without_overwriting_each_others_partials()
    {
        using var fixture = new Fixture();
        var all = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => new UpdateClient(fixture.Source).DownloadAsync(fixture.Offer, fixture.Cache, null, default)));
        Assert.Single(all.Select(d => d.Path).Distinct()); Assert.Empty(Directory.GetFiles(fixture.Cache, "*.part", SearchOption.AllDirectories));
        await UpdateClient.VerifyFileAsync(all[0], fixture.Cache, default);
    }
    [Fact]
    public void Checksum_manifest_requires_a_unique_exact_filename_and_matching_hash()
    {
        using var fixture = new Fixture(); var asset = fixture.Offer.Package; var line = asset.Sha256 + "  " + asset.Name;
        UpdateClient.VerifyManifest(line + "\r\n", asset);
        Assert.Throws<IOException>(() => UpdateClient.VerifyManifest(line + "\n" + line, asset));
        Assert.Throws<IOException>(() => UpdateClient.VerifyManifest(line.Replace(asset.Name, "folder/" + asset.Name), asset));
        Assert.Throws<IOException>(() => UpdateClient.VerifyManifest(new string('0', 64) + "  " + asset.Name, asset));
    }
    [Fact]
    public async Task Preferences_are_opt_in_and_round_trip_without_credentials()
    {
        using var fixture = new Fixture(); var path = Path.Combine(fixture.Root, "preferences.json"); var store = new UpdatePreferencesStore(path);
        Assert.Equal(new(IncludePreviews: true), await store.ReadAsync(true, default));
        var saved = new UpdatePreferences(true, true, false); await store.WriteAsync(saved, default); Assert.Equal(saved, await store.ReadAsync(true, default));
        Assert.DoesNotContain("token", await File.ReadAllTextAsync(path), StringComparison.OrdinalIgnoreCase);
        await File.WriteAllTextAsync(path, "broken"); await Assert.ThrowsAsync<IOException>(() => store.ReadAsync(true, default));
    }
    [Fact]
    public void Background_download_requires_opt_in_and_a_strictly_newer_release()
    {
        using var fixture = new Fixture();
        Assert.False(new UpdatePreferences().ShouldDownload(fixture.Offer));
        Assert.True(new UpdatePreferences(BackgroundDownload: true).ShouldDownload(fixture.Offer));
        Assert.False(new UpdatePreferences(BackgroundDownload: true).ShouldDownload(fixture.Offer with { IsNewer = false }));
        Assert.False(new UpdatePreferences(BackgroundDownload: true).ShouldDownload(null));
    }
    [Theory]
    [InlineData("[]")] [InlineData("null")]
    public async Task Non_object_preferences_do_not_enable_automatic_network_access(string json)
    {
        using var fixture = new Fixture(); var path = Path.Combine(fixture.Root, "prefs.json"); await File.WriteAllTextAsync(path, json);
        await Assert.ThrowsAsync<IOException>(() => new UpdatePreferencesStore(path).ReadAsync(true, default));
    }
    [Fact]
    public async Task Native_download_marker_is_preserved_without_running_an_installer()
    {
        using var fixture = new Fixture(); var path = Path.Combine(fixture.Root, "download.bin"); await File.WriteAllTextAsync(path, "marker test");
        await UpdateInstaller.MarkDownloadedAsync(path, default);
        if (OperatingSystem.IsWindows()) Assert.Contains("ZoneId=3", await File.ReadAllTextAsync(path + ":Zone.Identifier"));
        else if (OperatingSystem.IsMacOS())
        {
            var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/xattr") { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var arg in new[] { "-p", "com.apple.quarantine", path }) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!; var marker = await process.StandardOutput.ReadToEndAsync(); await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode); Assert.StartsWith("0083;", marker);
        }
        Assert.Equal("marker test", await File.ReadAllTextAsync(path));
    }
    private sealed class FakeSource : IUpdateSource
    {
        public List<byte[]> Pages = []; public byte[]? RepeatPage; public int PageCalls, PackageCalls;
        public Dictionary<long, byte[]> Data = [];
        public Func<Stream, CancellationToken, Task>? InterruptPackage;
        public Task<byte[]> PageAsync(int page, CancellationToken token)
        { token.ThrowIfCancellationRequested(); PageCalls++; return Task.FromResult(RepeatPage ?? Pages[page - 1]); }
        public async Task DownloadAsync(UpdateAsset asset, Stream destination, Action<long>? progress, CancellationToken token)
        {
            if (asset.Id == 101) { Interlocked.Increment(ref PackageCalls); if (InterruptPackage is not null) { await InterruptPackage(destination, token); return; } }
            await destination.WriteAsync(Data[asset.Id], token); progress?.Invoke(Data[asset.Id].Length);
        }
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Grove.Update.Tests", Guid.NewGuid().ToString("N"));
        public string Cache => Path.Combine(Root, "cache");
        public byte[] Bytes = Encoding.UTF8.GetBytes("Grove updater isolated fixture bytes");
        public FakeSource Source = new(); public UpdateOffer Offer;
        public Fixture()
        {
            Directory.CreateDirectory(Root); var release = Release("1.2.3"); var package = release.Assets[0];
            var manifest = Encoding.UTF8.GetBytes(package.Sha256 + "  " + package.Name + "\n");
            release = release with { Assets = [package, new(102, "SHA256SUMS.txt", manifest.Length, Hash(manifest))] };
            Offer = UpdateOffer.Select([release], UpdateVersion.Parse("1.0.0")!, true, "win-x64")!;
            Source.Data[101] = Bytes; Source.Data[102] = manifest;
        }
        public static UpdateRelease Release(string version)
        {
            var bytes = Encoding.UTF8.GetBytes("Grove updater isolated fixture bytes");
            return new(1, UpdateVersion.Parse(version)!, version.Contains('-'), [new(101, $"Grove-{version}-win-x64-setup.exe", bytes.Length, Hash(bytes)), new(102, "SHA256SUMS.txt", 100, new string('a', 64))]);
        }
        public static byte[] Page(UpdateRelease release) => JsonSerializer.SerializeToUtf8Bytes(new[] { new { id = release.Id, tag_name = "v" + release.Version, draft = false, prerelease = release.Preview, assets = release.Assets.Select(a => new { id = a.Id, name = a.Name, size = a.Size, digest = "sha256:" + a.Sha256, state = "uploaded" }) } });
        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
