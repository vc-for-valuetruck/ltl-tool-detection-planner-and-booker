using System.Collections.Concurrent;

namespace LtlTool.Api.Features.Ltl.DispatchAssist;

/// <summary>
/// App-side record of assembled dispatch decisions. In-memory, matching the same posture as the
/// consolidation audit store — a decision is never pushed to Alvys from here. Newest first.
/// </summary>
public interface IDispatchAssemblyStore
{
    DispatchAssembly Add(DispatchAssembly assembly);
    IReadOnlyList<DispatchAssembly> Recent(int max);
    IReadOnlyList<DispatchAssembly> ForLoad(string loadId);
}

/// <summary>Thread-safe in-memory <see cref="IDispatchAssemblyStore"/>.</summary>
public sealed class InMemoryDispatchAssemblyStore : IDispatchAssemblyStore
{
    private readonly ConcurrentQueue<DispatchAssembly> _items = new();
    private const int Capacity = 1000;

    public DispatchAssembly Add(DispatchAssembly assembly)
    {
        _items.Enqueue(assembly);
        while (_items.Count > Capacity && _items.TryDequeue(out _)) { }
        return assembly;
    }

    public IReadOnlyList<DispatchAssembly> Recent(int max) =>
        _items.Reverse().Take(Math.Clamp(max, 1, Capacity)).ToList();

    public IReadOnlyList<DispatchAssembly> ForLoad(string loadId) =>
        _items.Reverse()
            .Where(a => string.Equals(a.LoadId, loadId, StringComparison.OrdinalIgnoreCase))
            .ToList();
}
