using System.Runtime.InteropServices;

namespace Kiln.Core.Entities;

/// <summary>
/// What happened to a component during a publish cycle.
/// </summary>
public enum ChangeKind
{
    Added,
    Removed,
    Changed
}

/// <summary>
/// The single mechanism by which systems learn what changed, replacing the pattern of
/// every subsystem inventing its own message queue to stay coherent.
/// </summary>
/// <remarks>
/// The feed accumulates across every step in a <b>publish cycle</b> and is emptied by
/// <see cref="Drain"/>. A cycle is usually one step, but a frame that catches up several
/// fixed steps produces several — so the feed deliberately does not clear per step: the
/// consumer sees everything that happened since it last read, not just the final step.
///
/// Extraction (§6) is the first consumer and calls <see cref="Drain"/> once it has turned
/// the cycle into scene deltas. Nothing stops a game system reading the same feed, but
/// only the owner drains it.
/// </remarks>
public interface IChangeFeed
{
    ReadOnlySpan<EntityId> Entries(ComponentTypeId type, ChangeKind kind);

    /// <summary>True if nothing has been recorded since the last <see cref="Drain"/>.</summary>
    bool IsEmpty { get; }

    /// <summary>Ends the publish cycle and clears it. Called by the consumer, never by a step.</summary>
    void Drain();
}

/// <summary>
/// Scaffold implementation: correct shape, naive storage.
/// </summary>
/// <remarks>
/// TODO: the lists are read in place, so a consumer on another thread would see them
/// mutate. Extraction runs on the simulation thread today, which makes that safe;
/// revisit with double-buffering when §6 lands and the render thread reads deltas.
/// </remarks>
public sealed class ChangeFeed : IChangeFeed
{
    readonly List<List<EntityId>[]> _byType = new();
    int _recorded;

    public bool IsEmpty => _recorded == 0;

    internal void RegisterType(ComponentTypeId type)
    {
        while (_byType.Count <= type.Value)
            _byType.Add([new List<EntityId>(), new List<EntityId>(), new List<EntityId>()]);
    }

    internal void Record(ComponentTypeId type, ChangeKind kind, EntityId id)
    {
        if (type.Value >= _byType.Count) return;

        _byType[type.Value][(int)kind].Add(id);
        _recorded++;
    }

    public ReadOnlySpan<EntityId> Entries(ComponentTypeId type, ChangeKind kind) =>
        type.Value < _byType.Count
            ? CollectionsMarshal.AsSpan(_byType[type.Value][(int)kind])
            : ReadOnlySpan<EntityId>.Empty;

    public void Drain()
    {
        if (_recorded == 0) return;

        foreach (var kinds in _byType)
            foreach (var list in kinds)
                list.Clear();

        _recorded = 0;
    }
}
