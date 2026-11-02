using System.Globalization;
using GitX.Core;

namespace GitX.Desktop.ViewModels;

internal static class HistoryQueryInput
{
    // Validate a draft before publishing it to the active history query.
    public static HistoryQuery Create(string text, string author, string from, string until, string path, bool matchCase = false, bool useRegex = false)
    {
        static DateTimeOffset? Date(string value, bool end)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var label = end ? "结束日期" : "开始日期";
            if (!DateTime.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw new IOException($"{label}无效，请使用 yyyy-MM-dd 格式。");
            try { return new DateTimeOffset(end ? date.AddDays(1) : date); }
            catch (ArgumentOutOfRangeException) { throw new IOException($"{label}超出支持范围。"); }
        }
        var query = new HistoryQuery(text, author, Date(from, false), Date(until, true), path, matchCase, useRegex).Normalize();
        query.Validate();
        if (query.From is { } start && query.Until is { } end && start >= end)
            throw new IOException("结束日期不能早于开始日期。");
        return query;
    }
}
