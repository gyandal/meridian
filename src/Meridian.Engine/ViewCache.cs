using Meridian.Caching;
using Meridian.Views;

namespace Meridian.Engine;

/// <summary>
/// An in-process cache of finished <see cref="ChartView"/>s for repeated identical requests, so a warm
/// report skips transforms and projection too — which matters when a result has many thousands of points.
///
/// Correctness rests on the key, built by the engine: it includes the data version of every (metric,
/// entity) the view was built from, so a version bump makes old views unreachable, and every input that
/// shapes the output (transforms, view spec, calendar) through a stable <c>ICacheIdentity</c>; requests
/// that can't be described that way aren't cached. As a second line, <see cref="InvalidateAsync"/> drops
/// the views an invalidated entity contributed to. Theme, labels and formatter are matched by instance.
///
/// Memory is bounded by the total number of marks held (least recently used views go first); a view
/// larger than the whole budget is simply not cached. Views are immutable, so sharing them is safe.
/// </summary>
public sealed class ViewCache(long capacityMarks = 1_000_000) : ICacheInvalidator
{
    private sealed record Entry(string Key, ChartView View, long Marks, string[] Tags, ProjectionOptions Options);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _byKey = [];
    private readonly Dictionary<string, HashSet<string>> _byTag = [];
    private readonly LinkedList<Entry> _recency = new();
    private long _marks;

    public long CapacityMarks { get; } = capacityMarks;

    public int Count { get { lock (_gate) return _byKey.Count; } }

    public long Marks { get { lock (_gate) return _marks; } }

    public bool TryGet(string key, ProjectionOptions options, out ChartView view)
    {
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out var node) && SamePresentation(node.Value.Options, options))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
                view = node.Value.View;
                return true;
            }
        }
        view = null!;
        return false;
    }

    public void Set(string key, ProjectionOptions options, ChartView view, IEnumerable<string> tags)
    {
        long marks = view.Series.Sum(s => (long)s.Marks.Count) + 1;
        if (marks > CapacityMarks) return;

        lock (_gate)
        {
            Remove(key);
            var entry = new Entry(key, view, marks, [.. tags.Distinct()], options);
            _byKey[key] = _recency.AddFirst(entry);
            _marks += marks;
            foreach (var tag in entry.Tags)
            {
                if (!_byTag.TryGetValue(tag, out var keys)) _byTag[tag] = keys = [];
                keys.Add(key);
            }
            while (_marks > CapacityMarks && _recency.Last is { } oldest) Remove(oldest.Value.Key);
        }
    }

    public ValueTask InvalidateAsync(ChangeScope scope, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_byTag.TryGetValue(Tag(scope.Tenant, scope.Metric, scope.Entity), out var keys))
            {
                foreach (var key in keys.ToList()) Remove(key);
            }
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>The invalidation tag for one (tenant, metric, entity) a view was built from.</summary>
    public static string Tag(string tenant, string metric, EntityRef entity) =>
        $"{tenant}|{metric}|{entity.Dimension.Name}|{entity.Id}";

    private void Remove(string key)
    {
        if (!_byKey.Remove(key, out var node)) return;
        _recency.Remove(node);
        _marks -= node.Value.Marks;
        foreach (var tag in node.Value.Tags)
        {
            if (_byTag.TryGetValue(tag, out var keys) && keys.Remove(key) && keys.Count == 0) _byTag.Remove(tag);
        }
    }

    private static bool SamePresentation(ProjectionOptions a, ProjectionOptions b) =>
        ReferenceEquals(a.Theme, b.Theme) && ReferenceEquals(a.Labels, b.Labels) && ReferenceEquals(a.Formatter, b.Formatter);
}
