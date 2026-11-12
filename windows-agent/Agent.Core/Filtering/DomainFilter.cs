using System.Reflection;
using System.Text.Json;

namespace KidSafe.Agent.Core;

public enum FilterAction { Allow, Block }
public readonly record struct FilterDecision(FilterAction Action, string Reason)
{
    public bool Blocked => Action == FilterAction.Block;
    public static readonly FilterDecision Allow = new(FilterAction.Allow, "");
}

/// <summary>Built-in seed lists (not exhaustive; the "adult" category is additionally enforced by the family upstream DNS).</summary>
public sealed class CategoryLists(Dictionary<string, HashSet<string>> map)
{
    public static CategoryLists Default { get; } = Load();
    public IReadOnlySet<string>? Get(string category) => map.TryGetValue(category, out var s) ? s : null;

    private static CategoryLists Load()
    {
        using var s = typeof(CategoryLists).Assembly.GetManifestResourceStream("categories.json")!;
        var raw = JsonSerializer.Deserialize<Dictionary<string, string[]>>(s)!;
        return new(raw.ToDictionary(k => k.Key, v => v.Value.Select(Normalize).ToHashSet()));
    }

    public static string Normalize(string d) => d.Trim().TrimEnd('.').ToLowerInvariant();
}

public sealed class DomainFilter(CategoryLists? lists = null)
{
    private readonly CategoryLists _lists = lists ?? CategoryLists.Default;

    /// <summary>custom_allowed &gt; custom_blocked &gt; blocked_categories. Subdomains follow their parent.</summary>
    public FilterDecision Decide(string domain, Policy? policy)
    {
        if (policy is null || !policy.ProtectionFlags.WebFilter) return FilterDecision.Allow;
        var d = CategoryLists.Normalize(domain);
        if (d.Length == 0) return FilterDecision.Allow;

        if (MatchesAny(d, policy.CustomAllowedDomains)) return FilterDecision.Allow;
        if (MatchesAny(d, policy.CustomBlockedDomains)) return new(FilterAction.Block, "custom");
        foreach (var cat in policy.BlockedCategories)
            if (_lists.Get(cat) is { } set && MatchesSet(d, set)) return new(FilterAction.Block, cat);
        return FilterDecision.Allow;
    }

    private static bool MatchesAny(string d, IEnumerable<string> entries)
    {
        foreach (var e in entries)
        {
            var n = CategoryLists.Normalize(e);
            if (n.Length > 0 && (d == n || d.EndsWith("." + n, StringComparison.Ordinal))) return true;
        }
        return false;
    }

    /// <summary>Walk up the labels: a.b.example.com -> b.example.com -> example.com -> com.</summary>
    private static bool MatchesSet(string d, IReadOnlySet<string> set)
    {
        var s = d;
        while (true)
        {
            if (set.Contains(s)) return true;
            var i = s.IndexOf('.');
            if (i < 0) return false;
            s = s[(i + 1)..];
        }
    }
}
