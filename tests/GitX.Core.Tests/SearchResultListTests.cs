using System.Collections.Specialized;
using GitX.Core;
using GitX.Desktop.Services;
using Xunit;

namespace GitX.Core.Tests;

public sealed class SearchResultListTests
{
    [Fact]
    public void Final_page_adds_text_without_resetting_file_rows()
    {
        var file = new CodeSearchMatch("needle.cs", 1, 1, 0, "");
        var text = new CodeSearchMatch("needle.cs", 2, 3, 6, "needle", "fingerprint");
        var list = new SearchResultList([file]); var events = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, change) => events.Add(change.Action);
        list.AppendPage([file with { }, text]);
        Assert.Same(file, list[0]); Assert.Same(text, list[1]);
        Assert.Equal([NotifyCollectionChangedAction.Add], events);
        list.AppendPage([file, text]); Assert.Single(events);
    }

    [Fact]
    public void Changed_prefix_or_oversized_page_is_rejected_before_modifying_rows()
    {
        var file = new CodeSearchMatch("a.cs", 1, 1, 0, ""); var list = new SearchResultList([file]);
        Assert.Throws<InvalidDataException>(() => list.AppendPage([file with { Path = "b.cs" }]));
        Assert.Throws<InvalidDataException>(() => list.AppendPage([]));
        Assert.Throws<InvalidDataException>(() => list.AppendPage(Enumerable.Repeat(file, 1001).ToArray()));
        Assert.Same(file, Assert.Single(list));
    }
}
