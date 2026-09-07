namespace Kiln.Core.Entities;

// The open component registry and per-entity component operations. Add and Remove
// live on the world rather than the store so the presence mask and the change feed
// stay consistent with the storage; see the remarks on AddComponent.
public sealed partial class World
{
    /// <summary>
    /// Registers a component type and returns its id. Idempotent: registering the same
    /// type twice returns the first id rather than creating a second store.
    /// </summary>
    /// <param name="serializer">
    /// Optional persistence hook. A type without one is not written to a group's save record.
    /// </param>
    public ComponentTypeId RegisterComponent<T>(IComponentSerializer<T>? serializer = null) where T : struct
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

    public bool HasComponent<T>(EntityId id) where T : struct =>
        IsAlive(id) && (_presence[id.Index] & Bit(Store<T>().TypeId)) != 0;

    /// <summary>
    /// Adds or overwrites a component and returns it by reference.
    /// </summary>
    /// <remarks>
    /// Add and Remove go through the world rather than the store so the presence mask and
    /// the change feed stay consistent with the storage. Mutating in place does not, which
    /// is why <see cref="Store{T}.Ref"/> is public.
    /// </remarks>
    public ref T AddComponent<T>(EntityId id, in T value) where T : struct
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

    public bool RemoveComponent<T>(EntityId id) where T : struct
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
        if (HasComponent<T>(id))
            _changes.Record(Store<T>().TypeId, ChangeKind.Changed, id);
    }
}
