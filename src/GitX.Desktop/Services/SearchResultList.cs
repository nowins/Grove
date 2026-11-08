using System.Collections.ObjectModel;
using GitX.Core;

namespace GitX.Desktop.Services;

// One query owns this collection. Keep the file prefix in place when text
// results arrive, preserving list selection and preview identities.
internal sealed class SearchResultList(IReadOnlyList<CodeSearchMatch> files) : ObservableCollection<CodeSearchMatch>(files)
{
    public void AppendPage(IReadOnlyList<CodeSearchMatch> page)
    {
        if (page.Count < Count || page.Count > RepositoryReader.MaxSearchResults ||
            this.Where((match, index) => match != page[index]).Any())
            throw new InvalidDataException("搜索结果前缀已变化，请重新搜索。");
        for (var index = Count; index < page.Count; index++) Add(page[index]);
    }
}
