using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitX.Core;
using GitX.Desktop.ViewModels;

namespace GitX.Desktop.Services;

public sealed record EditorToolbarState(string Path, bool CanBlame, string BlameLabel, IReadOnlyList<ChangeLine>? IncludedLines = null, bool? Included = false,
    BlameViewSettings? BlameView = null, IReadOnlyList<SourceTabInfo>? SourceTabs = null, string? ActiveSourceTabId = null, bool SourceMode = false, bool FileHistory = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool ReadingNavigation = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ContinuousDiffManifest? ContinuousDiff = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool CanContinuousDiff = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool WorkspaceActions = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool EmptyWorkspace = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool DiffFileNavigation = false);

public sealed record MarkdownResourceContext(long Version, string Root, string Path, string? Revision);

// Per-window loopback origin. Only bundled assets and the current document are exposed.
// A random URL capability prevents other local web pages from guessing the document URL.
public sealed class EditorServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _prefix = $"/{Guid.NewGuid():N}/";
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "Editor", "dist");
    private static readonly JsonSerializerOptions DocumentJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private EditorPublication _publication = new("{}"u8.ToArray());
    private byte[] _layout = "{}"u8.ToArray();
    private byte[] _comments = "{}"u8.ToArray();
    public Uri Address { get; }

    public EditorServer(string page = "index.html") : this(page, ReservePort) { }

    internal EditorServer(string page, Func<int> reservePort)
    {
        for (var attempt = 0; ; attempt++)
        {
            var candidate = new HttpListener();
            string origin;
            try
            {
                origin = $"http://127.0.0.1:{reservePort()}/";
                candidate.Prefixes.Add(origin); candidate.Start();
            }
            catch (Exception e) when (e is HttpListenerException or SocketException)
            {
                candidate.Close();
                if (attempt >= 4) { _lifetime.Dispose(); throw new IOException("无法启动本地编辑器资源服务，已尝试 5 个端口。请检查本机端口或权限。", e); }
                continue;
            }
            catch { candidate.Close(); _lifetime.Dispose(); throw; }
            _listener = candidate; Address = new Uri(origin.TrimEnd('/') + _prefix + page); break;
        }
        _ = ServeAsync();
    }

    private static int ReservePort()
    {
        // The OS ephemeral range can be customized to include WebView-blocked
        // service ports (observed: 1720). Use the dynamic/private range instead.
        using var probe = new TcpListener(IPAddress.Loopback, RandomNumberGenerator.GetInt32(49152, 65536));
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public static byte[] Serialize(long version, RepositoryDocument document, EditorToolbarState? toolbar = null)
    {
        // Monaco renders the two sides and blocks. The native document still
        // owns the patch for fallback display and Git actions; don't transfer
        // a third copy of the source text to every WebView.
        if (document is { IsDiff: true, Diff: not null, IsNotice: false }) document = document with { Text = "" };
        return JsonSerializer.SerializeToUtf8Bytes(new { version, document, toolbar }, DocumentJson);
    }

    public void Publish(byte[] document, MarkdownResourceContext? markdown = null)
        => Publish(new EditorPublication(document, markdown));
    public EditorPublication PreparePublication(long version, RepositoryDocument document, EditorToolbarState toolbar,
        MarkdownResourceContext? markdown = null, string? repository = null, ContinuousDiffSession? continuous = null)
        => new(version, document, toolbar, markdown, repository, Volatile.Read(ref _publication), continuous);
    public void Publish(EditorPublication publication) => Volatile.Write(ref _publication, publication);
    // Toolbar-only changes use the small bridge message. Retain the latest
    // immutable snapshot for a page reload, serializing once only if requested.
    public void PublishDeferred(long version, RepositoryDocument document, EditorToolbarState toolbar, MarkdownResourceContext? markdown = null)
        => Publish(PreparePublication(version, document, toolbar, markdown));
    public void PublishLayout(byte[] layout) => Volatile.Write(ref _layout, layout);
    public void PublishComments(byte[] comments) => Volatile.Write(ref _comments, comments);

    public bool Owns(Uri? uri) => uri is not null && uri.GetLeftPart(UriPartial.Authority) == Address.GetLeftPart(UriPartial.Authority)
        && uri.AbsolutePath.StartsWith(_prefix, StringComparison.Ordinal);

    private async Task ServeAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync().WaitAsync(_lifetime.Token);
                _ = RespondAsync(context);
            }
            catch (Exception e) when (e is OperationCanceledException or HttpListenerException or ObjectDisposedException) { break; }
        }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            if (request.HttpMethod != "GET" || !Owns(request.Url)) { response.StatusCode = 404; return; }
            var name = Uri.UnescapeDataString(request.Url!.AbsolutePath[_prefix.Length..]);
            if (name == "continuous-diff")
            {
                var owner = Volatile.Read(ref _publication).Continuous;
                if (owner is null || request.QueryString["id"] != owner.Manifest.Id ||
                    !long.TryParse(request.QueryString["version"], out var version) || version != owner.Manifest.Version ||
                    !int.TryParse(request.QueryString["index"], out var index) || index < 0 || index >= owner.Manifest.Files.Count)
                { response.StatusCode = 404; return; }
                ContinuousDiffFile? comparedFile;
                try { comparedFile = await owner.ReadAsync(index, _lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { response.StatusCode = 404; return; }
                catch (ContinuousDiffBusyException) { response.StatusCode = 429; response.Headers["Retry-After"] = "1"; return; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or LibGit2Sharp.LibGit2SharpException)
                { comparedFile = new(owner.Manifest.Id, index, null, null, [], e.Message, false); }
                if (comparedFile is null || !owner.IsCurrent || !ReferenceEquals(owner, Volatile.Read(ref _publication).Continuous)) { response.StatusCode = 404; return; }
                var bytes = await Task.Run(() => JsonSerializer.SerializeToUtf8Bytes(comparedFile, DocumentJson), _lifetime.Token).ConfigureAwait(false);
                if (!owner.IsCurrent || !ReferenceEquals(owner, Volatile.Read(ref _publication).Continuous)) { response.StatusCode = 404; return; }
                response.ContentType = "application/json; charset=utf-8";
                await response.OutputStream.WriteAsync(bytes, _lifetime.Token);
                return;
            }
            if (name == "markdown-image")
            {
                var owner = Volatile.Read(ref _publication).Markdown;
                if (owner is null || !long.TryParse(request.QueryString["version"], out var version) || version != owner.Version
                    || request.QueryString["href"] is not { } href) { response.StatusCode = 404; return; }
                try
                {
                    var asset = await MarkdownResources.ReadImageAsync(owner.Root, owner.Path, owner.Revision, href, _lifetime.Token);
                    if (!ReferenceEquals(owner, Volatile.Read(ref _publication).Markdown)) { response.StatusCode = 404; return; }
                    response.ContentType = asset.ContentType;
                    await response.OutputStream.WriteAsync(asset.Bytes, _lifetime.Token);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or LibGit2Sharp.LibGit2SharpException or ArgumentException)
                { response.StatusCode = 404; }
                return;
            }
            if (name is "document" or "layout" or "comments")
            {
                response.ContentType = "application/json; charset=utf-8";
                await response.OutputStream.WriteAsync(name == "document" ? Volatile.Read(ref _publication).Response(request.QueryString["content"]) : name == "comments" ? Volatile.Read(ref _comments) : Volatile.Read(ref _layout), _lifetime.Token);
                return;
            }
            // Build emits flat asset names. Never serve paths or files outside that allowlist.
            if (name.Contains('/') || name.Contains('\\') || name.Contains("..")) { response.StatusCode = 404; return; }
            var file = Path.Combine(_directory, name);
            if (!File.Exists(file)) { response.StatusCode = 404; return; }
            if (name == "source-symbols.worker.js") response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self' 'wasm-unsafe-eval'; connect-src 'self';";
            response.ContentType = Path.GetExtension(name) switch
            {
                ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8",
                ".css" => "text/css", ".ttf" => "font/ttf", ".svg" => "image/svg+xml", ".wasm" => "application/wasm", _ => "text/plain"
            };
            await using var stream = File.OpenRead(file);
            await stream.CopyToAsync(response.OutputStream, _lifetime.Token);
        }
        catch (Exception e) when (e is IOException or HttpListenerException or OperationCanceledException or ObjectDisposedException) { }
        finally { context.Response.Close(); }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _listener.Close();
        Volatile.Write(ref _publication, new("{}"u8.ToArray()));
        _lifetime.Dispose();
    }
}
