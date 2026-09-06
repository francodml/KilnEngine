using System.Numerics;

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
/// registered by the game through <see cref="Register{T}"/> and is a first-class citizen:
/// same storage, same change feed, same serializer hook.
/// </remarks>
public sealed class World
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

    // ═══ Entities ══════════════════════════════════════════════════════════

    /// <summary>
    /// Creates an entity with no components, belonging to <paramref name="group"/>.
    /// </summary>
    /// <remarks>
    /// The specification writes this as <c>Create(EntityGroup)</c>. It takes a
    /// <see cref="GroupId"/> instead so callers cannot retain a mutable group object;
    /// resolving the id is one dictionary hit.
    /// </remarks>
    public EntityId Create(GroupId group)
    {
        if (!_groups.TryGetValue(group, out var target))
            throw new ArgumentException($"{group} does not exist.", nameof(group));

        uint index = _free.Count > 0 ? _free.Pop() : _highWater++;
        EnsureEntityCapacity(index);

        _alive[index]    = true;
        _group[index]    = group;
        _presence[index] = 0;

        var id = new EntityId(index, _generation[index]);
        target.Add(id);
        Transforms.Add(id);
        EntityCount++;
        return id;
    }

    /// <summary>Creates an entity and lets a definition decide what it is born with.</summary>
    public EntityId Create(GroupId group, IEntityDefinition definition)
    {
        var id = Create(group);
        definition.Instantiate(this, id);
        return id;
    }

    /// <summary>
    /// Destroys an entity, removing every component it holds and bumping its generation so
    /// ids held elsewhere become detectably stale.
    /// </summary>
    public void Destroy(EntityId id)
    {
        if (!IsAlive(id)) return;

        ulong mask = _presence[id.Index];
        while (mask != 0)
        {
            int type = BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;

            _stores[type]!.Remove(id);
            _changes.Record(new ComponentTypeId((ushort)type), ChangeKind.Removed, id);
        }

        if (_groups.TryGetValue(_group[id.Index], out var group))
            group.Remove(id);

        Transforms.Remove(id);

        _alive[id.Index]    = false;
        _presence[id.Index] = 0;
        _group[id.Index]    = GroupId.None;
        _generation[id.Index]++;              // any id still holding the old value now fails IsAlive
        _free.Push(id.Index);
        EntityCount--;
    }

    public bool IsAlive(EntityId id) =>
        id.Index < _highWater && _alive[id.Index] && _generation[id.Index] == id.Generation;

    public GroupId GroupOf(EntityId id) =>
        IsAlive(id) ? _group[id.Index] : GroupId.None;

    /// <summary>Façade over one entity. Convenience, not the iteration path.</summary>
    public Entity Entity(EntityId id) => new(this, id);

    // ═══ Components ════════════════════════════════════════════════════════

    /// <summary>
    /// Registers a component type and returns its id. Idempotent: registering the same
    /// type twice returns the first id rather than creating a second store.
    /// </summary>
    /// <param name="serializer">
    /// Optional persistence hook. A type without one is not written to a group's save record.
    /// </param>
    public ComponentTypeId Register<T>(IComponentSerializer<T>? serializer = null) where T : struct
    {
        if (_typeIds.TryGetValue(typeof(T), out var existing))
            return existing;

        if (_typeCount == MaxComponentTypes)
            throw new InvalidOperationException(
                $"At most {MaxComponentTypes} component types may be registered.");

        var typeId = new ComponentTypeId(_typeCount++);
        _stores[typeId.Value] = new Store<T>(typeId, serializer);
        _typeIds[typeof(T)] = typeId;
        _changes.RegisterType(typeId);
        return typeId;
    }

    public bool IsRegistered<T>() where T : struct => _typeIds.ContainsKey(typeof(T));

    /// <summary>The dense store for a registered component type.</summary>
    public Store<T> Store<T>() where T : struct =>
        _typeIds.TryGetValue(typeof(T), out var id)
            ? (Store<T>)_stores[id.Value]!
            : throw new InvalidOperationException(
                $"{typeof(T).Name} is not registered. Call World.Register<{typeof(T).Name}>() during game configuration.");

    public bool Has<T>(EntityId id) where T : struct =>
        IsAlive(id) && (_presence[id.Index] & Bit(Store<T>().TypeId)) != 0;

    /// <summary>
    /// Adds or overwrites a component and returns it by reference.
    /// </summary>
    /// <remarks>
    /// Add and Remove go through the world rather than the store so the presence mask and
    /// the change feed stay consistent with the storage. Mutating in place does not, which
    /// is why <see cref="Store{T}.Ref"/> is public.
    /// </remarks>
    public ref T Add<T>(EntityId id, in T value) where T : struct
    {
        if (!IsAlive(id))
            throw new InvalidOperationException($"{id} is not alive.");

        var store = Store<T>();
        bool isNew = !store.Has(id);

        ref T slot = ref store.Add(id, value);

        _presence[id.Index] |= Bit(store.TypeId);
        _changes.Record(store.TypeId, isNew ? ChangeKind.Added : ChangeKind.Changed, id);
        return ref slot;
    }

    public bool Remove<T>(EntityId id) where T : struct
    {
        var store = Store<T>();
        if (!store.Remove(id)) return false;

        _presence[id.Index] &= ~Bit(store.TypeId);
        _changes.Record(store.TypeId, ChangeKind.Removed, id);
        return true;
    }

    /// <summary>
    /// Announces that a component mutated in place, so the change reaches the feed.
    /// </summary>
    /// <remarks>
    /// The cost of dense storage plus by-reference mutation: the world cannot observe a
    /// write through <see cref="Store{T}.Ref"/>. A system that mutates and wants extraction
    /// to notice says so. TODO(§6): transforms avoid this via their own dirty flag; decide
    /// whether other hot components deserve the same rather than a feed entry.
    /// </remarks>
    public void Touch<T>(EntityId id) where T : struct
    {
        if (Has<T>(id))
            _changes.Record(Store<T>().TypeId, ChangeKind.Changed, id);
    }

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
