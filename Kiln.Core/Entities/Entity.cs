namespace Kiln.Core.Entities;

/// <summary>
/// Random-access ergonomics over dense storage. A façade: it holds no data beyond the
/// world it came from and the id it names, and copying one copies nothing meaningful.
/// </summary>
/// <remarks>
/// Systems should iterate <see cref="Store{T}.Span"/> directly — that is the fast path.
/// This exists for the cases where code legitimately has one entity in hand and wants to
/// ask about it.
/// </remarks>
public readonly struct Entity(World world, EntityId id)
{
    public World World { get; } = world;
    public EntityId Id { get; } = id;

    public bool IsAlive => World.IsAlive(Id);

    public GroupId Group => World.GroupOf(Id);

    public bool Has<T>() where T : struct => World.Has<T>(Id);

    public ref T Get<T>() where T : struct => ref World.Store<T>().Ref(Id);

    public ref T Add<T>(in T value) where T : struct => ref World.Add(Id, value);

    public bool Remove<T>() where T : struct => World.Remove<T>(Id);

    public void Destroy() => World.Destroy(Id);

    public override string ToString() => Id.ToString();
}

/// <summary>
/// Decides which components an entity is born with. Resolved through the resource system
/// (§3), so content authors define entity kinds as data rather than code hand-assembling
/// a game object.
/// </summary>
public interface IEntityDefinition
{
    void Instantiate(World world, EntityId id);
}
