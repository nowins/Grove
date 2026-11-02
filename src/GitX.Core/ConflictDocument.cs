namespace GitX.Core;

public sealed record ConflictSide(string Label, string Path, string? Sha, int Mode, string? Text)
{
    public bool Exists => Sha is not null;
}
public sealed record MergeBlock(int Id, int StartOffset, int Length, string LeftText, string BaseText, string RightText,
    bool IsConflict = true, bool LeftChanged = true, bool RightChanged = true, string? AutomaticText = null,
    int LeftStart = 0, int LeftCount = 0, int RightStart = 0, int RightCount = 0);
public sealed record MergeLayout(SideBySideDiff Left, SideBySideDiff Right);
public sealed record ConflictFile(string Path, string Left, string Right);
public sealed record ConflictDocument(string Root, string Path, string Token, RepositoryOperation? Operation,
    ConflictSide Base, ConflictSide Left, ConflictSide Right, string? Result, IReadOnlyList<MergeBlock> Blocks,
    string? Limitation, string LineEnding, bool Bom, bool ExistingResult, MergeLayout? Layout, int MarkerSize = 7)
{
    public bool IsText => Limitation is null;
}
