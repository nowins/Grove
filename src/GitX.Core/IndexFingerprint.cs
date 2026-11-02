using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace GitX.Core;

// See https://git-scm.com/docs/index-format . Preserve all entry flags (including
// intent-to-add and skip-worktree), stages and non-cache extensions such as REUC.
// Only stat fields, rebuildable caches and the checksum are excluded.
internal static class IndexFingerprint
{
    private sealed record Cached(string Path, int ObjectIdBytes, byte[] Bytes, string Fingerprint);
    private static Cached? _cached;
    public static string Read(string path, int objectIdBytes, CancellationToken token)
    {
        if (!File.Exists(path)) return "missing";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > int.MaxValue) throw Invalid();
        var bytes = new byte[(int)stream.Length];
        var read = 0;
        while (read < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            var count = stream.Read(bytes, read, Math.Min(65536, bytes.Length - read));
            if (count == 0) throw Invalid();
            read += count;
        }
        // Read every byte on every check. Reuse semantic parsing only when the
        // complete bytes match, never based on size/mtime or a stored checksum.
        // Retain just one index, bounded to 16 MiB; no repository/UI objects.
        var cached = Volatile.Read(ref _cached);
        if (cached?.Path == path && cached.ObjectIdBytes == objectIdBytes && bytes.AsSpan().SequenceEqual(cached.Bytes))
        { token.ThrowIfCancellationRequested(); return cached.Fingerprint; }
        var end = bytes.Length - objectIdBytes;
        if (end < 12 || !bytes.AsSpan(0, 4).SequenceEqual("DIRC"u8)) throw Invalid();
        var version = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4, 4));
        if (version is < 2 or > 4) throw Invalid();
        var entries = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(bytes, 0, 12);
        var cursor = 12;
        for (uint i = 0; i < entries; i++)
        {
            token.ThrowIfCancellationRequested();
            var start = cursor;
            var fixedSize = 40 + objectIdBytes + 2;
            if (end - cursor < fixedSize) throw Invalid();
            hash.AppendData(bytes, start + 24, 4); // File mode, not stat metadata.
            var flags = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(start + 40 + objectIdBytes, 2));
            cursor += fixedSize;
            if ((flags & 0x4000) != 0)
            {
                if (version == 2 || end - cursor < 2) throw Invalid();
                cursor += 2;
            }
            if (version == 4)
            {
                var width = 0;
                do { if (cursor >= end || ++width > 10) throw Invalid(); }
                while ((bytes[cursor++] & 0x80) != 0);
            }
            var nameLength = bytes.AsSpan(cursor, end - cursor).IndexOf((byte)0);
            if (nameLength < 0) throw Invalid();
            cursor += nameLength + 1;
            // Hash raw path encoding too: format changes remain conservatively stale.
            hash.AppendData(bytes, start + 40, cursor - start - 40);
            if (version != 4) cursor = start + ((cursor - start + 7) & ~7);
            if (cursor > end) throw Invalid();
        }
        while (cursor < end)
        {
            token.ThrowIfCancellationRequested();
            if (end - cursor < 8) throw Invalid();
            var size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(cursor + 4, 4));
            if (size > end - cursor - 8) throw Invalid();
            var signature = Encoding.ASCII.GetString(bytes, cursor, 4);
            if (signature is not ("TREE" or "UNTR" or "FSMN" or "EOIE" or "IEOT"))
                hash.AppendData(bytes, cursor, checked(8 + (int)size));
            cursor += checked(8 + (int)size);
        }
        var fingerprint = Convert.ToHexString(hash.GetHashAndReset());
        if (bytes.Length <= 16 * 1024 * 1024) Volatile.Write(ref _cached, new(path, objectIdBytes, bytes, fingerprint));
        return fingerprint;
    }

    private static IOException Invalid() => new("索引格式无效或读取期间变化，请刷新后重试。");
}
