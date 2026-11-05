using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using LibGit2Sharp;
using GitX.Core;

if (Environment.GetEnvironmentVariable(GitAskPassSession.PipeVariable) is not null && args.Length > 0)
{ Environment.ExitCode = await GitAskPassSession.RunClientAsync(args[0]); return; }
if (args.Length == 2 && args[0] == "--askpass-probe")
{ await AskPassProbe.RunAsync(args[1]); return; }

if (args.Length != 3) throw new ArgumentException("Expected architecture, publish directory and JSON result path.");
var expected = args[0]; var publish = Path.GetFullPath(args[1]);
var execution = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
var operatingSystem = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
if (execution != expected || operatingSystem != expected)
    throw new InvalidOperationException($"Native execution required: expected {expected}, .NET process {execution}, OS {operatingSystem}.");
// Accessing Version loads the actual libgit2 binary in this .NET process.
var libGit2 = GlobalSettings.Version.ToString();
var nativeAssets = new List<object>();
if (OperatingSystem.IsWindows())
{
    var expectedMachine = expected switch { "arm64" => 0xaa64, "x64" => 0x8664, _ => throw new InvalidOperationException("Unsupported Windows architecture.") };
    var gitAssets = Directory.GetFiles(publish, "git2-*.dll");
    if (gitAssets.Length == 0) throw new FileNotFoundException("Published libgit2 DLL missing.");
    foreach (var file in new[] { "Grove.exe", "libSkiaSharp.dll", "libHarfBuzzSharp.dll" }.Select(name => Path.Combine(publish, name)).Concat(gitAssets))
    {
        var machine = Machine(file);
        if (machine != expectedMachine) throw new BadImageFormatException($"Wrong native architecture for {Path.GetFileName(file)}: 0x{machine:x4}, expected 0x{expectedMachine:x4}.");
        nativeAssets.Add(new { file = Path.GetFileName(file), machine = $"0x{machine:x4}", architecture = expected });
    }
}

if (OperatingSystem.IsMacOS())
{
    var required = expected == "arm64" ? "arm64" : "x86_64";
    var files = new[] { Path.Combine(publish, "Grove") }.Concat(Directory.GetFiles(publish, "*.dylib"));
    foreach (var file in files)
    {
        var start = new ProcessStartInfo("/usr/bin/lipo") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-archs"); start.ArgumentList.Add(file);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start Mach-O architecture probe.");
        var architectures = process.StandardOutput.ReadToEnd().Trim(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0 || !architectures.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(required))
            throw new BadImageFormatException($"Wrong Mach-O architecture for {Path.GetFileName(file)}: {architectures} {error}");
        nativeAssets.Add(new { file = Path.GetFileName(file), architecture = architectures });
    }
}

if (OperatingSystem.IsLinux())
{
    var machine = expected switch { "x64" => (ushort)62, "arm64" => (ushort)183, _ => throw new InvalidOperationException("Unsupported Linux architecture.") };
    var gitAssets = Directory.GetFiles(publish, "*git2*.so");
    if (gitAssets.Length == 0) throw new FileNotFoundException("Published libgit2 SO missing.");
    foreach (var file in new[] { "Grove", "libSkiaSharp.so", "libHarfBuzzSharp.so" }.Select(name => Path.Combine(publish, name)).Concat(gitAssets))
    {
        using var stream = File.OpenRead(file);
        var header = new byte[20]; stream.ReadExactly(header);
        if (!header.AsSpan(0, 4).SequenceEqual(new byte[] { 0x7f, 0x45, 0x4c, 0x46 }) || header[4] != 2 || header[5] != 1 || System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18)) != machine)
            throw new BadImageFormatException($"Wrong ELF architecture for {Path.GetFileName(file)}.");
        nativeAssets.Add(new { file = Path.GetFileName(file), machine = $"0x{machine:x4}", architecture = expected });
    }
}

// The WebView package uses a managed WebView2 loader. Verify the shipped
// assembly is portable IL; browser runtime discovery is not a GUI smoke test.
var webViewAssembly = Path.Combine(publish, "Avalonia.Controls.WebView.dll");
using (var stream = File.OpenRead(webViewAssembly))
using (var pe = new PEReader(stream))
{
    if (!pe.HasMetadata || pe.PEHeaders.CorHeader is not { } clr || (clr.Flags & CorFlags.Requires32Bit) != 0)
        throw new BadImageFormatException("Published WebView assembly is missing or requires a 32-bit process.");
    var machine = (ushort)pe.PEHeaders.CoffHeader.Machine;
    if (machine != 0x014c && machine != (expected == "arm64" ? 0xaa64 : 0x8664))
        throw new BadImageFormatException("Published WebView assembly targets another architecture.");
}
var runtimes = new List<object>();
if (OperatingSystem.IsWindows())
{
    foreach (var programFiles in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Distinct())
    {
        var applications = Path.Combine(programFiles, "Microsoft", "EdgeWebView", "Application");
        if (!Directory.Exists(applications)) continue;
        foreach (var directory in Directory.EnumerateDirectories(applications))
        {
            var browser = Path.Combine(directory, "msedgewebview2.exe");
            if (File.Exists(browser)) runtimes.Add(new { version = FileVersionInfo.GetVersionInfo(browser).FileVersion, machine = $"0x{Machine(browser):x4}" });
        }
    }
}
var result = new { executionArchitecture = execution, operatingSystemArchitecture = operatingSystem,
    nativeLibGit2Loaded = true, libGit2Version = libGit2, nativeAssets,
    webView = new { portableManagedAssembly = true, runtimeCandidates = runtimes, guiTested = false } };
var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
File.WriteAllText(args[2], json); Console.WriteLine(json);

static ushort Machine(string file)
{
    using var stream = File.OpenRead(file); using var pe = new PEReader(stream);
    return (ushort)pe.PEHeaders.CoffHeader.Machine;
}
