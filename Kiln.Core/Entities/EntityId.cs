namespace Kiln.Core.Entities;

/// <summary>
/// A handle to an entity. <see cref="Index"/> addresses a slot; <see cref="Generation"/>
/// distinguishes successive occupants of that slot.
/// </summary>
/// <remarks>
/// Destroying an entity bumps the slot's generation, so an id held across the destroy
/// fails <see cref="World.IsAlive"/> instead of silently addressing whatever entity
/// reused the slot. Generations start at 1, so <c>default(EntityId)</c> is never live.
/// </remarks>
public readonly record struct EntityId(uint Index, uint Generation)
{
    public static readonly EntityId None = default;

    public bool IsNone => Generation == 0;

    public override string ToString() => IsNone ? "Entity(none)" : $"Entity({Index}v{Generation})";
}

/// <summary>
/// Identifies a registered component type. Assigned by
/// <see cref="World.Register{T}"/> in registration order, and used as the bit position
/// in an entity's presence mask.
/// </summary>
public readonly record struct ComponentTypeId(ushort Value)
{
    public override string ToString() => $"ComponentType({Value})";
}

/// <summary>
/// Identifies an <see cref="EntityGroup"/>: a membership and persistence set.
/// </summary>
public readonly record struct GroupId(uint Value)
{
    public static readonly GroupId None = default;

    public bool IsNone => Value == 0;

    public override string ToString() => IsNone ? "Group(none)" : $"Group({Value})";
}
