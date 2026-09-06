using Kiln.Core.Entities;

namespace Kiln.Core.Transforms;

/// <summary>
/// The transform hierarchy is infrastructure, not a component in a list. It exists for
/// attachment semantics and is never traversed recursively per frame.
/// </summary>
/// <remarks>
/// <b>Scaffold.</b> The shape is fixed — structure-of-arrays indexed by
/// <see cref="EntityId.Index"/>, decomposed transforms rather than matrices, depth-sorted
/// so parents always precede children, and propagation as one linear sweep over dirty
/// entries. The bodies land with §5.
/// </remarks>
public sealed class TransformHierarchy
{
    // Parallel arrays indexed by EntityId.Index:
    //   EntityId[]  _parent
    //   Transform[] _local     translation / rotation / scale, never a matrix
    //   Matrix4x4[] _world     derived; recomputed only when dirty
    //   ushort[]    _depth     parents always sort before children
    //   NodeFlags[] _flags     Dirty | Visible | Static | CastsShadow

    /// <summary>Attaches an entity to the hierarchy at the root.</summary>
    internal void Add(EntityId id) { /* §5 */ }

    /// <summary>Detaches an entity and reparents or drops its children.</summary>
    internal void Remove(EntityId id) { /* §5 */ }

    /// <summary>
    /// Recomputes world transforms in one sweep over depth-sorted entries. Marking a node
    /// dirty marks its subtree dirty, so the sweep never rediscovers that; static subtrees
    /// resolve once at load and are excluded entirely.
    /// </summary>
    public void Propagate() { /* §5 */ }
}
