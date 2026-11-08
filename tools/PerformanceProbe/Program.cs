using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitX.Core;
using Grove.Performance;

var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
string? reportOutput = null;
var results = new List<Scenario>();
try
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < args.Length; i += 2)
    {
        if (i + 1 == args.Length || args[i] is not ("--output" or "--budgets" or "--baseline" or "--runs" or "--warmup") ||
            !options.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Expected --output DIR --budgets FILE [--baseline FILE] [--runs N] [--warmup N].");
    }
    var output = Path.GetFullPath(options["--output"]);
    var runs = int.Parse(options.GetValueOrDefault("--runs", "9"), CultureInfo.InvariantCulture);
    var warmup = int.Parse(options.GetValueOrDefault("--warmup", "3"), CultureInfo.InvariantCulture);
    if (runs is < 5 or > 100 || warmup is < 1 or > 100) throw new ArgumentException("Runs must be 5–100; warmup must be 1–100.");
    var budgets = JsonSerializer.Deserialize<Budgets>(File.ReadAllText(options["--budgets"]), json) ?? throw new InvalidDataException("Empty budgets.");
    var baseline = options.TryGetValue("--baseline", out var baselinePath)
        ? JsonSerializer.Deserialize<Report>(File.ReadAllText(baselinePath), json) ?? throw new InvalidDataException("Empty baseline.") : null;
    if (File.Exists(Path.Combine(output, "report.json")) || File.Exists(Path.Combine(output, "report.md")) || Directory.Exists(Path.Combine(output, "fixture")))
        throw new IOException("Output already contains a report or fixture; choose a new directory.");
    Directory.CreateDirectory(output);
    reportOutput = output;
    var fixture = Path.Combine(output, "fixture"); Directory.CreateDirectory(fixture);
    var inventory = Enumerable.Range(0, 19998).Select(i => $"src/group{i % 40:D2}/ViewModel{i:D5}.cs")
        .Append("src/MainViewModel.cs").Append("test/MainViewModel.cs").ToArray();
    var paths = Enumerable.Range(0, 1000).Select(i => $"files/file{i:D4}.txt").Prepend("needle.cs").ToArray();
    var contents = new List<string>();
    foreach (var (path, index) in paths.Select((path, index) => (path, index)))
    {
        var content = $"// fixture {index:D4} 中文 😀\n" + new string('x', 256) + (index % 8 == 0 ? "\nneedle\n" : "\nplain\n");
        contents.Add(content); var full = Path.Combine(fixture, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
    }
    Git("init", "-b", "main"); Git("config", "user.name", "Grove Performance"); Git("config", "user.email", "performance@example.invalid");
    Git("config", "core.autocrlf", "false"); Git("config", "commit.gpgsign", "false");
    var hooks = Path.Combine(output, "empty-hooks"); Directory.CreateDirectory(hooks); Git("config", "core.hooksPath", hooks);
    Git("add", "--all"); Git("commit", "-m", "fixture base");
    for (var i = 0; i < 20; i++) Git("commit", "--allow-empty", "-m", $"fixture history {i:D2}");
    var before = GitState();
    var inventoryHash = Hash(string.Join('\n', inventory));
    var fixtureHash = Hash(string.Join('\n', paths.Zip(contents, (path, content) => path + "\0" + content)) + "\0" + before.Head);
    Task MeasureFiles(string name, CodeSearchQuery query, int expected, bool truncated = false) =>
        Measure(name, Hash(inventoryHash + "\0" + JsonSerializer.Serialize(query)),
            () => RepositoryReader.SearchAsync("unused", inventory, query), page => SearchSignature(page, expected, truncated));
    await MeasureFiles("files.exact", new("MainViewModel.cs", FilesOnly: true), 2);
    await MeasureFiles("files.abbreviation", new("MVM", FilesOnly: true), 2);
    await MeasureFiles("files.broad", new("view", FilesOnly: true), 1000, truncated: true);
    await MeasureFiles("files.path", new("group07/ vm", FilesOnly: true), 500);
    var textQuery = new CodeSearchQuery("needle");
    var textInputHash = Hash(fixtureHash + "\0" + JsonSerializer.Serialize(textQuery));
    await Measure("search.text", textInputHash, () => RepositoryReader.SearchAsync(fixture, paths, textQuery), page => SearchSignature(page, 126));
    double? firstFiles = null;
    await Measure("search.all", textInputHash, async () =>
    {
        var started = Stopwatch.GetTimestamp(); firstFiles = null;
        return await RepositoryReader.SearchEverywhereAsync(fixture, paths, textQuery, null,
            page => { firstFiles = Stopwatch.GetElapsedTime(started).TotalMilliseconds; if (page.Matches.Count != 1) throw new InvalidDataException("Missing filename result."); return Task.CompletedTask; });
    }, page => SearchSignature(page, 127), () => firstFiles);
    using var reader = new RepositoryReader();
    await Measure("repository.open", fixtureHash, () => reader.OpenAsync(fixture), snapshot =>
    {
        if (snapshot.Files.Count != paths.Length || snapshot.FilesTruncated) throw new InvalidDataException("Incomplete inventory.");
        return string.Join('\n', snapshot.Files.Select(file => file.Path));
    });
    await Measure("history.firstPage", fixtureHash, () => reader.HistoryAsync(fixture, before.Head), page =>
    {
        if (page.Commits.Count != 21 || page.HasMore) throw new InvalidDataException("Incomplete history.");
        return string.Join('\n', page.Commits.Select(commit => commit.Subject));
    });
    var oldText = string.Join('\n', Enumerable.Range(0, 1600).Select(i => $"    value_{i:D4} = calculate({i}); // 中文")) + "\n";
    var newText = oldText.Replace("calculate(400)", "updated(400)", StringComparison.Ordinal).Replace("calculate(1200)", "updated(1200)", StringComparison.Ordinal);
    await Measure("diff.structure", Hash(oldText + "\0" + newText), () => NativeDiff.CompareStructureAsync(new("sample.py", "before", oldText, true), new("sample.py", "after", newText, true), default),
        diff => { if (diff.Blocks.Count != 2) throw new InvalidDataException("Incorrect diff blocks."); return JsonSerializer.Serialize(diff); });
    var source = string.Join('\n', Enumerable.Range(0, 8000).Select(i => $"# 中文 😀 source line {i:D4}")) + "\n";
    if (before != GitState()) throw new InvalidDataException("Read scenarios changed the fixture before source setup.");
    File.WriteAllText(Path.Combine(fixture, "source.py"), source, new UTF8Encoding(false));
    // The source scenario owns its additional untracked file; take a new proof.
    before = GitState();
    await Measure("source.read", Hash(source), () => reader.DocumentAsync(new(fixture, "source.py", ComparisonKind.WorkingFile)),
        document => { if (document.IsNotice || document.Text != source) throw new InvalidDataException("Source changed or was truncated."); return document.Text; });
    var after = GitState();
    if (before != after) throw new InvalidDataException("Read scenarios changed HEAD, index or fixture files.");
    var report = new Report(1, "grove-backend-warm-v1", Hash(Environment.MachineName), RuntimeInformation.RuntimeIdentifier,
        RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription, Environment.ProcessorCount, warmup, runs,
        FileHash(typeof(RepositoryReader).Assembly.Location), FileHash(Assembly.GetExecutingAssembly().Location), DateTimeOffset.UtcNow, results);
    var failures = Evaluation.Check(report, budgets, baseline);
    WriteNew(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, json));
    var markdown = new StringBuilder("# Grove backend performance\n\nWarm OS caches; process CPU and managed allocation only. No window, WebView, input-to-paint, child-process CPU or process-tree memory measurement. CPU timer granularity can produce zero samples.\n\n| Scenario | p50 ms | p95 ms | CPU p50 ms | allocated p50 B | first files p95 ms |\n| --- | ---: | ---: | ---: | ---: | ---: |\n");
    foreach (var result in results)
    {
        var first = result.Metrics.TryGetValue("firstFiles.p95Ms", out var firstMs) ? firstMs.ToString("F2", CultureInfo.InvariantCulture) : "—";
        markdown.AppendLine(CultureInfo.InvariantCulture, $"| {result.Name} | {result.Metrics["elapsed.p50Ms"]:F2} | {result.Metrics["elapsed.p95Ms"]:F2} | {result.Metrics["cpu.p50Ms"]:F2} | {result.Metrics["allocated.p50Bytes"]:F0} | {first} |");
    }
    markdown.AppendLine().AppendLine(failures.Count == 0 ? "PASS" : "FAIL");
    foreach (var failure in failures) markdown.AppendLine("- " + failure);
    WriteNew(Path.Combine(output, "report.md"), markdown.ToString());
    foreach (var failure in failures) Console.Error.WriteLine(failure);
    Console.WriteLine($"{results.Count} scenarios; {failures.Count} failures. {Path.Combine(output, "report.json")}");
    return failures.Count == 0 ? 0 : 1;

    async Task Measure<T>(string name, string inputHash, Func<Task<T>> action, Func<T, string> signature, Func<double?>? first = null)
    {
        var samples = new List<Sample>(); string? resultHash = null;
        using var process = Process.GetCurrentProcess();
        for (var i = -warmup; i < runs; i++)
        {
            var cpu = process.TotalProcessorTime; var allocation = GC.GetTotalAllocatedBytes(false); var start = Stopwatch.GetTimestamp();
            var value = await action();
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var sample = new Sample(elapsed, (process.TotalProcessorTime - cpu).TotalMilliseconds, GC.GetTotalAllocatedBytes(false) - allocation, first?.Invoke());
            var hash = Hash(signature(value));
            if (resultHash is not null && resultHash != hash) throw new InvalidDataException($"{name}: results changed between samples.");
            resultHash = hash; if (i >= 0) samples.Add(sample);
        }
        var metrics = new Dictionary<string, double>
        {
            ["elapsed.p50Ms"] = Evaluation.Percentile(samples.Select(sample => sample.ElapsedMs), .5),
            ["elapsed.p95Ms"] = Evaluation.Percentile(samples.Select(sample => sample.ElapsedMs), .95),
            ["cpu.p50Ms"] = Evaluation.Percentile(samples.Select(sample => sample.CpuMs), .5),
            ["allocated.p50Bytes"] = Evaluation.Percentile(samples.Select(sample => (double)sample.AllocatedBytes), .5)
        };
        if (first is not null) metrics["firstFiles.p95Ms"] = Evaluation.Percentile(samples.Select(sample => sample.FirstFilesMs ?? throw new InvalidDataException("Files were not published.")), .95);
        results.Add(new(name, inputHash, resultHash!, samples, metrics));
        Console.WriteLine(FormattableString.Invariant($"{name}: p50 {metrics["elapsed.p50Ms"]:F2} ms, p95 {metrics["elapsed.p95Ms"]:F2} ms"));
    }

    string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = fixture, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["GIT_AUTHOR_DATE"] = start.Environment["GIT_COMMITTER_DATE"] = "2000-01-01T00:00:00Z";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Git did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit(); Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new IOException($"Git {arguments[0]} failed: {stderr.Result}");
        return stdout.Result.Trim();
    }
    (string Head, string Index, string Files) GitState() => (Git("rev-parse", "HEAD"), FileHash(Path.Combine(fixture, ".git", "index")),
        Hash(string.Join('\n', Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(fixture, path).StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(fixture, path) + ":" + FileHash(path)))));
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    if (reportOutput is not null)
    {
        try { WriteNew(Path.Combine(reportOutput, "failure.json"), JsonSerializer.Serialize(new { error = error.ToString(), completedScenarios = results }, json)); }
        catch (IOException writeError) { Console.Error.WriteLine("Could not save failure details: " + writeError.Message); }
    }
    return 1;
}

static string SearchSignature(CodeSearchPage page, int expected, bool truncated = false)
{
    if (page.Matches.Count != expected || page.Truncated != truncated)
        throw new InvalidDataException($"Expected {expected} search results (truncated={truncated}); got {page.Matches.Count} (truncated={page.Truncated}).");
    return JsonSerializer.Serialize(page);
}
static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
static string FileHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
static void WriteNew(string path, string text) { using var stream = new FileStream(path, FileMode.CreateNew); using var writer = new StreamWriter(stream, new UTF8Encoding(false)); writer.Write(text); }
