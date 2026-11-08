using GitX.Core;

namespace GitX.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    public async Task OpenMarkdownLinkAsync(string href, long version)
    {
        if (_closed || version != _documentVersion || _snapshot is not { } snapshot ||
            _document is not { IsDiff: false, IsNotice: false, Language: "markdown" } source) return;
        var link = MarkdownResources.Resolve(source.Path, href);
        var repository = _repositoryVersion;
        var kind = source.SourceRevision is null ? ComparisonKind.WorkingFile : ComparisonKind.RevisionFile;
        // Resolve/read before changing selection. A missing or unsafe target
        // leaves the current file and its preview intact.
        var document = await _reader.DocumentAsync(new(snapshot.Root, link.Path, kind, source.SourceRevision), _documentRead.Token);
        if (_closed || repository != _repositoryVersion || version != _documentVersion) return;
        if (document.IsNotice) throw new IOException(document.Text);
        if (source.SourceRevision is { } revision) await OpenRevisionSourceAsync(link.Path, revision);
        else
        {
            SetEditorContext(0);
            var entry = NavigationEntries(0).FirstOrDefault(entry => entry.Path == link.Path) ?? new(link.Path, link.Path, ComparisonKind.WorkingFile);
            OpenWorkingTab(entry); _selectedEntry = entry; Changed(nameof(SelectedEntry));
            await SelectEntryAsync(entry);
        }
    }

    public async Task<bool> OpenRecentFileAsync(string root, string path, string? revision)
    {
        if (_closed || Root != root || !Services.RecentFileStore.SafePath(path) || !Services.RecentFileStore.SafeRevision(revision)) return false;
        var repository = _repositoryVersion; var version = _documentVersion;
        try
        {
            // Prove the target can be read before changing the active tab.
            var document = await _reader.DocumentAsync(new(root, path, revision is null ? ComparisonKind.WorkingFile : ComparisonKind.RevisionFile, revision), _documentRead.Token);
            if (_closed || repository != _repositoryVersion || version != _documentVersion || document.IsNotice) return false;
            var existing = _sourceTabs.FirstOrDefault(tab => revision is null
                ? tab.Target is null && tab.Entry.Kind == ComparisonKind.WorkingFile && tab.Entry.Path == path
                : tab.Target?.Sha == revision && tab.Target.Path == path);
            if (existing is not null) await SelectSourceTabAsync(existing.Id, version);
            else if (revision is not null) await OpenRevisionSourceAsync(path, revision);
            else await OpenEditorEntryAsync(new(path, path, ComparisonKind.WorkingFile));
            return !_closed && Root == root && _document is { IsNotice: false } opened && opened.Path == path;
        }
        catch (OperationCanceledException) { return false; }
        catch (IOException) { return false; }
        catch (LibGit2Sharp.LibGit2SharpException) { return false; }
    }

    private CancellationTokenSource _noteRead = new();
    public void CancelNoteSource() { if (!_closed) Replace(ref _noteRead); }
    public bool RepositoryFilesTruncated => _snapshot?.FilesTruncated == true;

    public async Task<bool> OpenCodeSearchMatchAsync(string root, CodeSearchMatch match)
    {
        if (_closed || _snapshot?.Root != root || !_snapshot.Files.Any(file => file.Path == match.Path)) return false;
        SetEditorContext(0);
        var entry = NavigationEntries(0).FirstOrDefault(entry => entry.Path == match.Path) ?? new(match.Path, match.Path, ComparisonKind.WorkingFile);
        OpenWorkingTab(entry); _selectedEntry = entry; Changed(nameof(SelectedEntry));
        await SelectEntryAsync(entry, location: match);
        return !_closed && _mode == 0 && Root == root && _document is { IsNotice: false } document && document.Path == match.Path &&
            document.RevealLine == match.Line && document.RevealColumn == match.Column;
    }

    public async Task<bool> OpenNoteSourceAsync(string root, CommentAnchor anchor)
    {
        if (_closed || _snapshot is not { } snapshot || snapshot.Root != root ||
            anchor.Context.Kind != ComparisonKind.WorkingFile || anchor.Side != 1 ||
            !snapshot.Files.Any(file => file.Path == anchor.Path)) return false;
        var owner = ++_annotationNavigation; var repository = _repositoryVersion; var version = _documentVersion;
        Replace(ref _noteRead);
        using var read = CancellationTokenSource.CreateLinkedTokenSource(_noteRead.Token, _documentRead.Token, _repositoryRead.Token);
        bool Current() => !_closed && !read.IsCancellationRequested && owner == _annotationNavigation && repository == _repositoryVersion && version == _documentVersion;
        try
        {
            var annotate = _sourceTabs.FirstOrDefault(tab => tab.Target is null && tab.Entry.Path == anchor.Path)?.Annotate ?? _showBlame;
            var document = annotate ? await _blameReader.ReadAsync(root, anchor.Path, read.Token)
                : await _reader.DocumentAsync(new(root, anchor.Path, ComparisonKind.WorkingFile), read.Token);
            // Publish only after proving the complete original context. A changed
            // HEAD, text or worktree keeps the note on its original snapshot.
            if (!Current() || document.IsNotice || !anchor.Matches(document.CommentContext)) return false;
            await CaptureReadingAsync();
            if (!Current()) return false;
            SetEditorContext(0);
            var entry = NavigationEntries(0).FirstOrDefault(entry => entry.Path == anchor.Path) ?? new(anchor.Path, anchor.Path, ComparisonKind.WorkingFile);
            OpenWorkingTab(entry); _selectedEntry = entry; Changed(nameof(SelectedEntry));
            Replace(ref _documentRead);
            _document = document with { RevealLine = anchor.StartLine, RevealColumn = 1 };
            TrackReading(entry, _document, preserve: false, explicitJump: true);
            Error = ""; FileTitle = document.Path; Comparison = document.Comparison; Detail = document.Detail;
            WriteStateChanged(); DocumentChanged?.Invoke(++_documentVersion, _document);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch when (!Current()) { return false; }
    }
}
