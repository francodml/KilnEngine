using System.Numerics;

namespace Kiln.Core.Entities;

// Entity lifecycle and identity. The state these operate on -- the parallel arrays
// indexed by EntityId.Index -- stays declared in World.cs: the presence mask is
// written from here and from World.Components.cs, and only one type enforcing that
// keeps mask, stores and change feed from disagreeing.
public sealed partial class World
{
    /// <summary>
    /// Creates an entity with no components, belonging to <paramref name="group"/>.
    /// </summary>
    /// <remarks>
    /// The specification writes this as <c>Create(EntityGroup)</c>. It takes a
    /// <see cref="GroupId"/> instead so callers cannot retain a mutable group object;
    /// resolving the id is one dictionary hit.
    /// </remarks>
    public EntityId CreateEntity(GroupId group)
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
    public EntityId CreateEntity(GroupId group, IEntityDefinition definition)
    {
        var id = CreateEntity(group);
        definition.Instantiate(this, id);
        return id;
    }

    /// <summary>
    /// Destroys an entity, removing every component it holds and bumping its generation so
    /// ids held elsewhere become detectably stale.
    /// </summary>
    public void DestroyEntity(EntityId id)
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
}
