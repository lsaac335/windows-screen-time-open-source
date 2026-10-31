using System;
using System.Collections.Generic;

namespace ScreenTime;

public readonly record struct WindowRect(int Left, int Top, int Right, int Bottom)
{
    public long Area => Math.Max(0L, (long)Right - Left) * Math.Max(0L, (long)Bottom - Top);
    public WindowRect Intersect(WindowRect other) => new(Math.Max(Left, other.Left), Math.Max(Top, other.Top),
        Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));
}

/// <summary>Rectangular occlusion estimate. Work and retained regions have hard limits.</summary>
public sealed class VisibilityGeometry
{
    private List<WindowRect> _remaining = new();
    public const int MaximumRegions = 1024;
    public bool IsReliable { get; private set; } = true;
    public VisibilityGeometry(IEnumerable<WindowRect> monitors)
    {
        foreach (var rect in monitors)
        {
            // Monitors normally do not overlap; subtract clones before adding their union.
            var pieces = new List<WindowRect> { rect };
            foreach (var existing in _remaining) pieces = SubtractAll(pieces, existing);
            _remaining.AddRange(pieces);
            if (_remaining.Count > MaximumRegions) { IsReliable = false; _remaining.Clear(); break; }
        }
    }
    public long VisibleArea(WindowRect bounds)
    {
        long area = 0;
        foreach (var rect in _remaining) area += rect.Intersect(bounds).Area;
        return area;
    }
    public void Cover(WindowRect bounds)
    {
        if (!IsReliable) return;
        var next = SubtractAll(_remaining, bounds);
        if (next.Count > MaximumRegions) { IsReliable = false; _remaining.Clear(); }
        else _remaining = next;
    }
    private static List<WindowRect> SubtractAll(List<WindowRect> regions, WindowRect cover)
    {
        var result = new List<WindowRect>();
        foreach (var r in regions)
        {
            var i = r.Intersect(cover);
            if (i.Area == 0) result.Add(r);
            else
            {
                Add(new(r.Left, r.Top, r.Right, i.Top)); Add(new(r.Left, i.Bottom, r.Right, r.Bottom));
                Add(new(r.Left, i.Top, i.Left, i.Bottom)); Add(new(i.Right, i.Top, r.Right, i.Bottom));
            }
            if (result.Count > MaximumRegions) break;
        }
        return result;
        void Add(WindowRect r) { if (r.Area > 0) result.Add(r); }
    }
}

public sealed record VisibleApp(string Id, string Name);
public sealed record VisibleInterval(DateTimeOffset Start, DateTimeOffset End, double Seconds, VisibleApp[] Apps);
