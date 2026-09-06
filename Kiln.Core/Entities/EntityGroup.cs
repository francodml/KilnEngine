namespace Kiln.Core.Entities;

/// <summary>Whether a group is drawn as its proxy or as its own entities.</summary>
public enum GroupDetail
{
    /// <summary>Detail-bound renderables are hidden; <see cref="EntityGroup.Proxy"/> stands in.</summary>
    Proxy,

    /// <summary>The group's own renderables are extracted.</summary>
    Full
}

/// <summary>
/// A game-supplied stand-in drawn while a group's detail is <see cref="GroupDetail.Proxy"/>
/// — a baked imposter, a billboard, a simplified mesh. The engine only knows it can be
/// added to and removed from the render scene.
/// </summary>
public interface IGroupProxy
{
    // Resolved during extraction (§6). Left open until the render scene lands:
    // this will expose the renderable(s) the proxy contributes.
}

/// <summary>
/// A membership and persistence set: entities that are saved together and whose
/// lifecycle is managed together. Every entity belongs to exactly one group from
/// creation and may be moved between groups with <see cref="World.Move"/>.
/// </summary>
/// <remarks>
/// A group is <b>not</b> a spatial partition and <b>not</b> a scene-graph node. Entities
/// from every group share the same dense stores, so iteration cost is unaffected by how
/// many groups exist. Spatial questions are answered by systems' own indexes.
///
/// The two axes are deliberately orthogonal:
/// <list type="bullet">
///   <item><see cref="Loaded"/> is streaming — are the entities instantiated at all.</item>
///   <item><see cref="Detail"/> is rendering only — simulation is untouched by a flip.</item>
/// </list>
/// A game whose world fits in memory never unloads anything and still uses
/// <see cref="Detail"/> freely.
/// </remarks>
public sealed class EntityGroup
{
    readonly List<EntityId> _entities = new();

    internal EntityGroup(GroupId id, string name, IGroupProxy? proxy)
    {
        Id = id;
        Name = name;
        Proxy = proxy;
        Loaded = true;
        Detail = GroupDetail.Full;
    }

    public GroupId Id { get; }

    /// <summary>Diagnostic label. The engine attaches no meaning to it.</summary>
    public string Name { get; }

    /// <summary>Simulation axis: are this group's entities instantiated in the stores?</summary>
    public bool Loaded { get; internal set; }

    /// <summary>Render axis: drawn as <see cref="Proxy"/>, or as this group's own entities?</summary>
    public GroupDetail Detail { get; internal set; }

    /// <summary>Stand-in drawn while <see cref="Detail"/> is <see cref="GroupDetail.Proxy"/>.</summary>
    public IGroupProxy? Proxy { get; }

    public IReadOnlyList<EntityId> Entities => _entities;

    internal void Add(EntityId id) => _entities.Add(id);

    internal bool Remove(EntityId id) => _entities.Remove(id);
}
