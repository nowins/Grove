using System.Runtime.InteropServices;
using System.Text;
using LibGit2Sharp;

namespace GitX.Core;

internal readonly record struct DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount);
internal sealed record BufferPatch(IReadOnlyList<DiffHunk> Hunks, byte[] Utf8);

// The same pinned libgit2 already used for repository reads can compare captured
// buffers without creating files, Git objects or a child process. Keep the CLI
// path for large inputs: its process can be interrupted while native xdiff cannot.
internal static class BufferDiff
{
    private static readonly Lazy<Api?> Library = new(Load);

    internal static bool TryCompare(string before, string after, CancellationToken token, out string patch)
    {
        patch = "";
        if (!TryCapture(before, after, token, out var captured)) return false;
        patch = Encoding.UTF8.GetString(captured.Utf8);
        return true;
    }

    internal static bool TryCapture(string before, string after, CancellationToken token, out BufferPatch captured)
    {
        captured = new([], []);
        token.ThrowIfCancellationRequested();
        if (before.Length + after.Length > 256 * 1024 ||
            (long)(before.Count(c => c == '\n') + 1) * (after.Count(c => c == '\n') + 1) > 16_000_000)
            return false;
        var api = Library.Value;
        if (api is null) return false;
        var oldBytes = Encoding.UTF8.GetBytes(before);
        var newBytes = Encoding.UTF8.GetBytes(after);
        var options = new DiffOptions();
        Check(api.OptionsInit(ref options, 1));
        options.ContextLines = 0;
        options.InterhunkLines = 0;
        options.Flags = 1u << 20; // FORCE_TEXT; Myers, no indent heuristic.
        nint nativePatch = 0;
        var buffer = new GitBuffer();
        try
        {
            Check(api.FromBuffers(out nativePatch, oldBytes, (nuint)oldBytes.Length, "old.txt", newBytes, (nuint)newBytes.Length, "new.txt", ref options));
            token.ThrowIfCancellationRequested();
            var count = nativePatch == 0 ? 0 : checked((int)api.NumHunks(nativePatch));
            if (count != 0)
            {
                var hunks = new DiffHunk[count];
                for (var i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    Check(api.GetHunk(out var pointer, out _, nativePatch, (nuint)i));
                    // libgit2 1.8 git_diff_hunk begins with four C ints. Copy
                    // only those fields; no native pointer survives this call.
                    var hunk = Marshal.PtrToStructure<HunkRange>(pointer);
                    hunks[i] = new(hunk.OldStart - (hunk.OldCount == 0 ? 0 : 1), hunk.OldCount,
                        hunk.NewStart - (hunk.NewCount == 0 ? 0 : 1), hunk.NewCount);
                }
                Check(api.ToBuffer(ref buffer, nativePatch));
                if (buffer.Size > GitReadProcess.MaxOutputCharacters * 4u) throw new IOException("Diff 输出超过预览上限。");
                var bytes = new byte[checked((int)buffer.Size)];
                Marshal.Copy(buffer.Pointer, bytes, 0, bytes.Length);
                captured = new(Array.AsReadOnly(hunks), bytes);
            }
            token.ThrowIfCancellationRequested();
            return true;
        }
        finally
        {
            api.BufferDispose(ref buffer);
            if (nativePatch != 0) api.PatchFree(nativePatch);
        }
        static void Check(int result) { if (result < 0) throw new IOException($"内存 Diff 计算失败 ({result})。"); }
    }

    private static Api? Load()
    {
        // Resolve the exact native asset selected by the locked LibGit2Sharp
        // package. Do not search arbitrary user directories or load system git2.
        var revision = GlobalSettings.Version.LibGit2CommitSha;
        if (!revision.StartsWith("5853918", StringComparison.Ordinal)) return null;
        var name = "git2-" + revision[..7];
        var configured = GlobalSettings.NativeLibraryPath;
        if (configured is not null)
            name = Path.Combine(configured, OperatingSystem.IsWindows() ? name + ".dll" : "lib" + name + (OperatingSystem.IsMacOS() ? ".dylib" : ".so"));
        if (!NativeLibrary.TryLoad(name, typeof(Repository).Assembly,
                DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories, out var library)) return null;
        try
        {
            T Export<T>(string symbol) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, symbol));
            Export<VersionDelegate>("git_libgit2_version")(out var major, out var minor, out _);
            // DiffOptions is the libgit2 1.8 ABI shipped by the pinned package
            // (without experimental SHA256). Other assets use the existing CLI.
            if (major != 1 || minor != 8) { NativeLibrary.Free(library); return null; }
            var api = new Api(Export<OptionsInitDelegate>("git_diff_options_init"), Export<FromBuffersDelegate>("git_patch_from_buffers"),
                Export<NumHunksDelegate>("git_patch_num_hunks"), Export<GetHunkDelegate>("git_patch_get_hunk"), Export<ToBufferDelegate>("git_patch_to_buf"),
                Export<BufferDisposeDelegate>("git_buf_dispose"), Export<FreeDelegate>("git_patch_free"));
            if (Export<InitDelegate>("git_libgit2_init")() < 0) { NativeLibrary.Free(library); return null; }
            // One initialization/reference retained for this process lifetime,
            // independently of LibGit2Sharp's finalizer and concurrent readers.
            return api;
        }
        catch (EntryPointNotFoundException) { NativeLibrary.Free(library); return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DiffOptions
    {
        public uint Version, Flags;
        public int IgnoreSubmodules;
        public nint Paths;
        public nuint PathCount;
        public nint Notify, Progress, Payload;
        public uint ContextLines, InterhunkLines;
        public ushort IdAbbrev;
        public long MaxSize;
        public nint OldPrefix, NewPrefix;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct GitBuffer { public nint Pointer; public nuint Reserved, Size; }
    [StructLayout(LayoutKind.Sequential)]
    private struct HunkRange { public int OldStart, OldCount, NewStart, NewCount; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void VersionDelegate(out int major, out int minor, out int revision);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OptionsInitDelegate(ref DiffOptions options, uint version);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FromBuffersDelegate(out nint patch,
        byte[] before, nuint beforeLength, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        byte[] after, nuint afterLength, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, ref DiffOptions options);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint NumHunksDelegate(nint patch);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetHunkDelegate(out nint hunk, out nuint lines, nint patch, nuint index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ToBufferDelegate(ref GitBuffer buffer, nint patch);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void BufferDisposeDelegate(ref GitBuffer buffer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeDelegate(nint patch);
    private sealed record Api(OptionsInitDelegate OptionsInit, FromBuffersDelegate FromBuffers, NumHunksDelegate NumHunks, GetHunkDelegate GetHunk,
        ToBufferDelegate ToBuffer, BufferDisposeDelegate BufferDispose, FreeDelegate PatchFree);
}
