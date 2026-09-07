using Kiln.Core.Transforms;

namespace Kiln.Core.Entities;

/// <summary>
/// The simulation's authoritative model: entities, their components, their grouping, and
/// the transform hierarchy that relates them.
/// </summary>
/// <remarks>
/// The world is not a rendering structure and does not know a renderer exists. It holds
/// no GPU handles for its own use — only opaque ones it passes through to extraction.
///
/// Threading: the world is a single-threaded object. The simulation thread has exclusive
/// access to it while stepping; a headless tool or a test drives the same object on the
/// calling thread. Nothing here starts a thread or takes a lock — that is
/// <c>Kiln.Runtime</c>'s job.
///
/// The engine ships two component types (Transform, Renderable). Every other component is
/// registered by the game through <see cref="RegisterComponent{T}"/> and is a first-class citizen:
/// same storage, same change feed, same serializer hook.
/// </remarks>
public sealed partial class World
{
    /// <summary>
    /// Component types are bit positions in a single-word presence mask.
    /// TODO: widen to a multi-word mask if a game ever needs more than 64.
    /// </summary>
    public const int MaxComponentTypes = 64;

    const int InitialEntityCapacity = 256;

    // ── Entity identity ────────────────────────────────────────────────────
    // Parallel arrays indexed by EntityId.Index. A free slot keeps its generation so the
    // next occupant differs from the last.
    uint[]    _generation = new uint[InitialEntityCapacity];
    bool[]    _alive      = new bool[InitialEntityCapacity];
    GroupId[] _group      = new GroupId[InitialEntityCapacity];
    ulong[]   _presence   = new ulong[InitialEntityCapacity];

    readonly Stack<uint> _free = new();
    uint _highWater;

    // ── Component registry ─────────────────────────────────────────────────
    readonly Dictionary<Type, ComponentTypeId> _typeIds = new();
    readonly IStore?[] _stores = new IStore?[MaxComponentTypes];
    ushort _typeCount;

    // ── Groups ─────────────────────────────────────────────────────────────
    readonly Dictionary<GroupId, EntityGroup> _groups = new();
    uint _nextGroupId = 1;

    readonly ChangeFeed _changes = new();

    /// <summary>Engine-owned attachment structure. Not a component; see §5.</summary>
    public TransformHierarchy Transforms { get; } = new();

    /// <summary>Typed Added / Removed / Changed per store, for the current publish cycle.</summary>
    public IChangeFeed Changes => _changes;

    public int EntityCount { get; private set; }

    public IReadOnlyCollection<EntityGroup> Groups => _groups.Values;

    // ═══ Groups ════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a membership and persistence set. The first game creates one always-Full
    /// global group for terrain, roads and everything on world terrain, plus one group per
    /// lot whose proxy is the baked imposter.
    /// </summary>
    public EntityGroup CreateGroup(string name, IGroupProxy? proxy = null)
    {
        var group = new EntityGroup(new GroupId(_nextGroupId++), name, proxy);
        _groups[group.Id] = group;
        return group;
    }

    public EntityGroup GetGroup(GroupId id) =>
        _groups.TryGetValue(id, out var group)
            ? group
            : throw new ArgumentException($"{id} does not exist.", nameof(id));

    /// <summary>
    /// Moves an entity between groups. Changes what it is saved with, and which proxy
    /// hides it; changes nothing about its simulation. A sim crossing a lot boundary is
    /// this call.
    /// </summary>
    public void Move(EntityId id, GroupId target)
    {
        if (!IsAlive(id))
            throw new InvalidOperationException($"{id} is not alive.");

        var destination = GetGroup(target);
        if (_group[id.Index] == target) return;

        if (_groups.TryGetValue(_group[id.Index], out var source))
            source.Remove(id);

        destination.Add(id);
        _group[id.Index] = target;
    }

    /// <summary>
    /// Streaming axis. Unloading serializes the group's entities into its save record and
    /// destroys them; loading reverses it.
    /// </summary>
    /// <remarks>
    /// Not implemented, and not on the critical path: the first game is an open world that
    /// never unloads. It exists so a game that streams cells has the seam.
    /// </remarks>
    public void SetLoaded(GroupId group, bool loaded)
    {
        var target = GetGroup(group);
        if (target.Loaded == loaded) return;

        throw new NotImplementedException(
            "Group streaming requires component serializers and a save record (§3, §4).");
    }

    /// <summary>
    /// Render axis. Emits the extraction deltas and touches no simulation state: entities
    /// on a Proxy group keep simulating, routing, and — if their renderable is not
    /// detail-bound — drawing.
    /// </summary>
    /// <remarks>
    /// The engine never decides <i>when</i> to flip; that is the game's camera policy.
    /// TODO(§6): publish the flip so extraction can emit Remove for detail-bound
    /// renderables plus Add for the proxy.
    /// </remarks>
    public void SetDetail(GroupId group, GroupDetail detail)
    {
        var target = GetGroup(group);
        if (target.Detail == detail) return;

        target.Detail = detail;
    }

    // ═══ Internals ═════════════════════════════════════════════════════════

    static ulong Bit(ComponentTypeId type) => 1UL << type.Value;

    void EnsureEntityCapacity(uint index)
    {
        if (index < (uint)_generation.Length)
        {
            // Generations start at 1 so default(EntityId) is never a live id.
            if (_generation[index] == 0) _generation[index] = 1;
            return;
        }

        int size = _generation.Length;
        while (index >= (uint)size) size *= 2;

        Array.Resize(ref _generation, size);
        Array.Resize(ref _alive, size);
        Array.Resize(ref _group, size);
        Array.Resize(ref _presence, size);

        if (_generation[index] == 0) _generation[index] = 1;
    }
}
