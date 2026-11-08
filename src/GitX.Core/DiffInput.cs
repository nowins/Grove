namespace GitX.Core;

// Hashing scans text once, on the CPU scheduler. Equality still compares every
// field, so hash collisions cannot alias different contents or display sides.
internal sealed class DiffInput(DiffSide oldSide, DiffSide newSide, DiffWhitespace whitespace, string? capturedIdentity = null) : IEquatable<DiffInput>
{
    public DiffSide Old { get; } = oldSide;
    public DiffSide New { get; } = newSide;
    public DiffWhitespace Whitespace { get; } = whitespace;
    private readonly string? _identity = capturedIdentity;
    // Repository capture already hashes the complete sides for CommentContext.
    // Reuse that bucket hint instead of rescanning/rescheduling captured text.
    private readonly int _hash = capturedIdentity is null ? HashCode.Combine(oldSide, newSide, whitespace) : HashCode.Combine(capturedIdentity, whitespace);
    public long RetainedBytes => 512L + 2L * (Old.Text.Length + (long)New.Text.Length + Old.Path.Length + New.Path.Length + Old.Label.Length + New.Label.Length + (_identity?.Length ?? 0));
    public bool Equals(DiffInput? other) => ReferenceEquals(this, other) || other is not null &&
        _hash == other._hash && _identity == other._identity && Whitespace == other.Whitespace && Old == other.Old && New == other.New;
    public override bool Equals(object? obj) => obj is DiffInput other && Equals(other);
    public override int GetHashCode() => _hash;
}
