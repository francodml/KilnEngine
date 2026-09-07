using System.Buffers;

namespace Kiln.Core.Entities;

/// <summary>
/// Non-generic face of a component store, so <see cref="World"/> can hold every store
/// in one array indexed by <see cref="ComponentTypeId"/>.
/// </summary>
public interface IStore
{
    ComponentTypeId TypeId { get; }
    int Count { get; }
    bool Has(EntityId id);

    /// <summary>Removes the component if present. Called by <see cref="World.DestroyEntity"/>.</summary>
    bool Remove(EntityId id);
}

/// <summary>
/// Optional persistence hook for a component type, supplied at registration.
/// A type without one is not written to a group's save record.
/// </summary>
public interface IComponentSerializer<T> where T : struct
{
    void Write(in T value, IBufferWriter<byte> destination);
    T Read(ReadOnlySpan<byte> source);
}

/// <summary>
/// Dense per-type component storage: a sparse set over a packed array.
/// </summary>
/// <remarks>
/// Iteration is a linear scan of <see cref="Span"/>; single-entity lookup is one
/// indirection. Removal is swap-with-last, so <b>nothing may hold a dense index across a
/// step</b> — hold an <see cref="EntityId"/> instead.
///
/// Add and Remove are internal: they go through <see cref="World"/> so the entity's
/// presence mask and the change feed stay consistent with the store. <see cref="Ref"/>
/// is public because mutating a component in place changes no membership.
/// </remarks>
public sealed class Store<T> : IStore where T : struct
{
    const int InitialCapacity = 64;

    T[]        _dense  = new T[InitialCapacity];
    EntityId[] _owner  = new EntityId[InitialCapacity];   // dense slot -> entity
    int[]      _sparse = [];                              // entity index -> dense slot, or -1
    int        _count;

    internal Store(ComponentTypeId typeId, IComponentSerializer<T>? serializer)
    {
        TypeId = typeId;
        Serializer = serializer;
    }

    public ComponentTypeId TypeId { get; }

    public IComponentSerializer<T>? Serializer { get; }

    public int Count => _count;

    /// <summary>The packed components. Valid until the next Add or Remove.</summary>
    public Span<T> Span => _dense.AsSpan(0, _count);

    /// <summary>Entity owning each dense slot, parallel to <see cref="Span"/>.</summary>
    public ReadOnlySpan<EntityId> Owners => _owner.AsSpan(0, _count);

    public bool Has(EntityId id) =>
        id.Index < (uint)_sparse.Length && _sparse[id.Index] >= 0;

    /// <summary>The component, by reference. Throws if the entity does not have one.</summary>
    public ref T Ref(EntityId id)
    {
        if (!Has(id))
            throw new InvalidOperationException($"{id} has no {typeof(T).Name}.");

        return ref _dense[_sparse[id.Index]];
    }

    internal ref T Add(EntityId id, in T value)
    {
        EnsureSparse(id.Index);

        if (_sparse[id.Index] >= 0)
        {
            ref T existing = ref _dense[_sparse[id.Index]];
            existing = value;
            return ref existing;
        }

        if (_count == _dense.Length)
        {
            Array.Resize(ref _dense, _dense.Length * 2);
            Array.Resize(ref _owner, _owner.Length * 2);
        }

        int slot = _count++;
        _dense[slot] = value;
        _owner[slot] = id;
        _sparse[id.Index] = slot;
        return ref _dense[slot];
    }

    public bool Remove(EntityId id)
    {
        if (!Has(id)) return false;

        int slot = _sparse[id.Index];
        int last = --_count;

        // Swap-with-last, then repoint the moved entity's sparse entry.
        if (slot != last)
        {
            _dense[slot] = _dense[last];
            _owner[slot] = _owner[last];
            _sparse[_owner[slot].Index] = slot;
        }

        _dense[last] = default;
        _owner[last] = EntityId.None;
        _sparse[id.Index] = -1;
        return true;
    }

    void EnsureSparse(uint index)
    {
        if (index < (uint)_sparse.Length) return;

        int old = _sparse.Length;
        int size = Math.Max(InitialCapacity, old == 0 ? 1 : old);
        while (index >= (uint)size) size *= 2;

        Array.Resize(ref _sparse, size);
        Array.Fill(_sparse, -1, old, size - old);
    }
}
