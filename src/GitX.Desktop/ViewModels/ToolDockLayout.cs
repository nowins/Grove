using System.Text.Json;

namespace GitX.Desktop.ViewModels;

public enum ToolId { Project, Commit, GitLog, FileHistory, Output, Worktrees }
public enum ToolEdge { Left, Right, Top, Bottom }
public sealed record ToolPlacement(ToolId Id, ToolEdge Edge, bool Floating = false, bool Visible = false,
    int Order = 0, double X = 100, double Y = 100, double Width = 900, double Height = 600, bool Independent = false);
public sealed record ToolLayoutSnapshot(int Version, ToolPlacement[] Tools, double Left = 280, double Right = 300, double Top = 260, double Bottom = 340);

// No repository state or editor contents belong in a window layout.
public sealed class ToolDockLayout
{
    private readonly Dictionary<ToolId, ToolPlacement> _tools = [];
    public ToolDockLayout() => Reset();
    public IReadOnlyList<ToolPlacement> Tools => _tools.Values.OrderBy(t => t.Order).ThenBy(t => t.Id).ToArray();
    public ToolPlacement this[ToolId id] => _tools[id];
    public void Reset()
    {
        _tools.Clear();
        foreach (var id in Enum.GetValues<ToolId>())
            _tools[id] = new(id, id is ToolId.Project or ToolId.Commit ? ToolEdge.Left : ToolEdge.Bottom,
                Visible: id == ToolId.Project, Order: (int)id, Width: id is ToolId.Project or ToolId.Commit ? 420 : 1100);
    }
    public void Show(ToolId id)
    {
        var tool = this[id];
        if (!tool.Floating)
            foreach (var other in Tools.Where(t => t.Id != id && t.Edge == tool.Edge && !t.Floating))
                _tools[other.Id] = other with { Visible = false };
        _tools[id] = tool with { Visible = true };
    }
    public void Hide(ToolId id) => _tools[id] = this[id] with { Visible = false };
    public void Dock(ToolId id, ToolEdge edge, int? before = null)
    {
        var neighbors = Tools.Where(t => t.Id != id && t.Edge == edge).Select(t => t.Id).ToList();
        neighbors.Insert(Math.Clamp(before ?? neighbors.Count, 0, neighbors.Count), id);
        _tools[id] = this[id] with { Edge = edge, Floating = false, Independent = false };
        for (var i = 0; i < neighbors.Count; i++) _tools[neighbors[i]] = this[neighbors[i]] with { Order = i };
        Show(id);
    }
    public void Float(ToolId id, double? x = null, double? y = null, bool independent = false)
    {
        var current = this[id];
        _tools[id] = current with { Floating = true, Visible = true, X = x ?? current.X, Y = y ?? current.Y, Independent = independent };
    }
    public void SetBounds(ToolId id, double x, double y, double width, double height)
    { if (Valid(x, y, width, height)) _tools[id] = this[id] with { X = x, Y = y, Width = width, Height = height }; }
    public void Restore(ToolLayoutSnapshot snapshot)
    {
        Reset(); if (snapshot.Version != 1) return;
        var seen = new HashSet<ToolId>();
        foreach (var tool in snapshot.Tools ?? [])
        {
            if (!Enum.IsDefined(tool.Id) || !Enum.IsDefined(tool.Edge) || !seen.Add(tool.Id) || !Valid(tool.X, tool.Y, tool.Width, tool.Height)) continue;
            _tools[tool.Id] = tool with { Order = Math.Clamp(tool.Order, 0, 20) };
        }
        foreach (var edge in Enum.GetValues<ToolEdge>())
        {
            var visible = Tools.Where(t => t.Edge == edge && t.Visible && !t.Floating).ToArray();
            foreach (var tool in visible.Skip(1)) Hide(tool.Id);
        }
    }
    public static ToolEdge? DropEdge(double x, double y, double width, double height)
    {
        if (width <= 0 || height <= 0 || x < 0 || y < 0 || x > width || y > height) return null;
        var distances = new[] { (ToolEdge.Left, x), (ToolEdge.Right, width - x), (ToolEdge.Top, y), (ToolEdge.Bottom, height - y) };
        var nearest = distances.MinBy(pair => pair.Item2);
        return nearest.Item2 <= Math.Min(80, Math.Min(width, height) * .24) ? nearest.Item1 : null;
    }
    public static ToolPlacement FitFloat(ToolPlacement tool, double x, double y, double width, double height, double scaling)
    {
        if (!double.IsFinite(scaling) || scaling <= 0) scaling = 1;
        var w = Math.Clamp(tool.Width, 280, Math.Max(280, width / scaling));
        var h = Math.Clamp(tool.Height, 200, Math.Max(200, height / scaling));
        return tool with { Width = w, Height = h, X = Math.Clamp(tool.X, x, Math.Max(x, x + width - w * scaling)),
            Y = Math.Clamp(tool.Y, y, Math.Max(y, y + height - h * scaling)) };
    }
    private static bool Valid(double x, double y, double w, double h) => double.IsFinite(x) && double.IsFinite(y) &&
        double.IsFinite(w) && double.IsFinite(h) && Math.Abs(x) <= 100000 && Math.Abs(y) <= 100000 && w is >= 240 and <= 10000 && h is >= 160 and <= 10000;
}

internal sealed class ToolLayoutStore(string path)
{
    private readonly SemaphoreSlim _write = new(1);
    private long _requested;
    public Task<ToolLayoutSnapshot?> ReadAsync() => Task.Run(async () => {
        try { if (new FileInfo(path).Length > 65536) return null; return JsonSerializer.Deserialize<ToolLayoutSnapshot>(await File.ReadAllTextAsync(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    });
    public Task WriteAsync(ToolLayoutSnapshot snapshot)
    {
        var request = Interlocked.Increment(ref _requested);
        return Task.Run(async () =>
        {
            await _write.WaitAsync();
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                if (request != Volatile.Read(ref _requested)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(snapshot));
                if (request == Volatile.Read(ref _requested)) File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); _write.Release(); }
        });
    }
}
