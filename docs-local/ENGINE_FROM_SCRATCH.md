# Engine — Specification From Zero

Last updated: 2026-09-23 (first version 2026-08-26)

**2026-09-23 revision — engine tick vs game time.** §1, §2, §7, §9 and §10 are revised around one
separation. The engine's fixed tick is not the game's clock. The engine steps the world and runs
every registered system on every tick while the game runs. Its host-control block can suspend or
scale the tick only for app-level reasons, such as a debugger break, shutdown, or debug
slow-motion, and never as a game mechanic. In-universe time, speed settings, pause, and whether a
given entity "thinks" are game state. In the first game they belong to the script host, because
they govern which guest tasks run. Pausing the game therefore does not stop the tick: build/buy
mode, the camera, and other systems keep running while the Sims stand still. The camera is a
logic-side system that consumes input on the tick and moves *in the world*. It is not a
client-side position fed to the renderer.

**Migration plan.** `KILN_MIGRATION_PLAN.md` drives the order of Kiln work from here on. It is the
plan for moving the first game onto Kiln, and it holds the Kiln requirements register (`KR-xx`).
Part II below is kept as the 2026-08-26 inventory. For the current state of both tracks, see the
plan's §2. For Kiln's build order, see the plan's §4 and §6.

This document specifies the engine as if no code existed. Part I is a clean-room
specification: subsystems, contracts, invariants. It contains no reference to the current
codebase and no migration steps. Part II is a separate inventory pass that maps what has already
been built onto that specification.

Three things changed in the 2026-09-05 revision:

- **The engine is general-purpose.** OpenTS3 is the first *game* built on it — the way a Source
  game is built on the Source engine — not the engine's identity. Every place the first version
  named a lot, a sim, a pattern, or a TGI has been recast as an engine extension point plus the
  game's use of it. The dependency rule in §12 is the enforcement. In particular, "lot detail" is
  now a **render-detail axis on an entity group**, separate from whether the group's entities are
  loaded — because the first game is an open world in which everything keeps simulating.
- **The engine is named, and it owns the host.** The engine is `Kiln.*`; the game is `OpenTS3.*`.
  §12 previously left the window and frame loop to a "composition root" in `Tools`, which would
  have forced a game to write windowing code to start. It does not: the host owns the window, the
  device and the loop, a game implements `IGame`, and `Tools` is one line.
- **The native reference runtime has been inventoried.** `docs/native_notes/
  ts3w_runtime_symbol_inventory.md` and `ts3w_object_model_inferred.md` record what the shipped
  TS3 engine actually does. §0 states what this specification keeps from it and what it fixes.

**What this supersedes.** The first version of this document claimed to supersede nothing. After
the 2026-09-05 revision that is no longer true: the engine/game split and the entity model
contradict decisions recorded elsewhere, and a reader following those documents would build the
wrong thing. This document is authoritative for the sections named below, and *only* those — each
of these documents retains unique content that nothing here replaces.

| Document | Superseded here | Still authoritative for |
|---|---|---|
| `ARCHITECTURE.md` | all of it — already replaced by `ARCHITECTUREV2.md` | nothing; retained as history |
| `ARCHITECTUREV2.md` | "Physical Project Structure" (§12 replaces it), "Object Registry" (§4 replaces it), "Scene Boundary" (§6–§7 replace it) | resource key and group rules, `ResourceTypes`, the OBJD dependency DAG, the 16 EA singleton interfaces, the Tier 1/Tier 2 managed split, the D3D9 constant-register table |
| `SCENE_GRAPH_DESIGN.md` | §2–§8, generalised into §3, §5, §6, §7, §8 | §1 (the two-`SceneDescription` diagnosis), §9 (`IDomainRenderer`), §11 (type disposition). **§10's phase order conflicts with Part II's shortest path — see the note there** |
| `ENGINE_REFACTOR_TASKS.md` | §2, §4, §6, §7 | nothing; retained as the record of in-flight branch work |
| `CODE_ORGANISATION_AUDIT.md` | §6 "Candidate project structure", as a *project* layout | everything else — it is the only inventory of the physical tree, and its namespace clustering still applies inside the game layer |

`SCENE_BOUNDARY_CARVE_PLAN.md` is cited by `SCENE_GRAPH_DESIGN.md` but does not exist on this
branch; it was added in `accb845` and lives on `jjs-build-speedtree`.

---
---

# Part I — The Engine

## 0. What kind of engine this is

**Two layers, one direction of dependency.** The *engine* owns identity, time, threads,
resources, the world model, transforms, extraction, rendering, input, and diagnostics. A *game*
owns its data formats, its component types, its systems, its rules, and whatever scripting host
it needs. The engine never references a game; a game references the engine. The test is the
project reference graph, not intent.

The first game is a Sims-3-class reimplementation. Its requirements shaped the engine, but each
one is expressed as something a different game could also use:

| First game needs | Engine provides (generic) | Game supplies |
|---|---|---|
| A huge authored static world with a few hundred moving things | `Static` flag on transforms; extraction driven by a dirty set, so cost tracks changes, not world size | which entities are static |
| Lot detail level as gameplay-visible state, in an open world where everything keeps simulating | `EntityGroup` — the unit of membership, save, and (optionally) streaming — with a separate **render detail** axis and a proxy slot | that a group is a "lot", the imposter as its proxy, and the camera policy that flips detail |
| Per-placed-object material composition | two-tier materials: template + instance state, hashed into one handle; a texture-composer extension point | the pattern/colour composition itself |
| Gameplay as foreign precompiled managed code | an `Entity` façade, typed change feeds, an intent buffer, and a script-host extension point | the managed host, its redirection mechanisms, its scheduler, and the guest's clock, speeds and pause |
| Correctness measured against a reference build | capture, hash, and diff as engine infrastructure | the reference fixtures |

**Evidence basis.** The shipped TS3 runtime (`TS3W.exe`) is an object/component engine: heap
objects addressed externally by a 64-bit handle from a pointer→handle map; a closed set of 16
component kinds allocated separately from the object; a shared per-kind "context" holding the
transform parent relation; every hot path running over a subsystem-owned index (routing quadtree,
physics quadtree, render-side scene table) rather than the object list; render managers fed by
delayed message queues; and objects grouped into managers that are each backed by their own
database. It is not RenderWare (only RenderWare Audio Core ships) and not an ECS. This
specification keeps the shape of that design and fixes four things about it:

| Native TS3 | This engine |
|---|---|
| pointer→handle hash map, no stale-handle check | `EntityId(Index, Generation)`: O(1) lookup, stale ids fail deterministically |
| closed enum of 16 component kinds | open registry; the engine ships the components most games need, the game registers the rest on equal terms |
| components as individual heap allocations | dense per-type stores; the object is a façade, not an owner of memory |
| each subsystem invents its own message queue to stay coherent | one typed change feed per store; systems subscribe |

---

## 1. Time and the frame

Engine time and game time belong to different layers. Keeping them apart is the point of this
section.

The engine has two clocks:

- **Step time** is the engine tick. The simulation thread (the logic thread) advances the world in
  fixed steps, and every step runs every registered system (§10). No game mechanic pauses, slows,
  or speeds up the tick. Only the host-control block below does, and only for app-level reasons.
- **Real time** advances with the wall clock. Render interpolation, animation blending, and UI
  transitions read this clock only.

```csharp
readonly record struct StepTime(long Tick, float DeltaSeconds);   // DeltaSeconds is always FixedStep
readonly record struct RealTime(double Seconds, float DeltaSeconds);
```

(The first version named these `SimTime`/`SetSimSpeed`. They were renamed to `StepTime` and
`SetTimeScale` because in the first game "sim" means a character and "sim time" means the game's
own clock, which is exactly what this section must not be confused with.)

**Ownership.** The main thread publishes a monotonic timestamp. The simulation thread owns its own
accumulator and fixed-step loop derived from that timestamp. The render thread derives its own
interpolation factor. No thread reads another's clock state; each derives what it needs from the
published timestamp.

```
// simulation thread
accumulator += (now - last) * hostTimeScale;    // host control: 1 unless debugging, never game speed
while (accumulator >= FixedStep) { Step(FixedStep); accumulator -= FixedStep; }
publish(tick, accumulator / FixedStep);         // tick + alpha stamped onto the delta batch

// render thread
alpha = (now - lastBatch.RealTime) / FixedStep  // clamp; interpolate prev→curr per proxy
```

**Host control is app-level, not gameplay.** The host-control block is applied at a step boundary.
It holds three settings:

- `SetTimeScale`, a generic engine control for debug slow-motion;
- a suspend, for a debugger break or possibly a minimised window;
- shutdown.

It is not a command in the intent buffer (§2), and no game uses it to implement its own pause or
speed. Suspending it stops *every* system, which is exactly why a game's pause must not be built
on it.

**Game time is the game's.** The engine has no concept of any of the following. Each one is game
state, advanced by game systems from the ticks those systems receive:

- an in-universe clock ("Tuesday, 3:42 PM")
- speed settings (1/2/3)
- pause
- whether a given entity "thinks" this tick

A game that pauses has not stopped the engine. Its pause is game state that its own systems
consult, while everything else, such as build tools, the camera (§9), and UI-driven world edits,
keeps ticking. The engine's only obligation is to let a game hold that clock: a game-owned state
record in the world that systems read. Each game system chooses its clock explicitly. One that
must freeze with the game's pause reads the game clock; anything else reads `StepTime`. In the
first game, Sims time, the speed settings, pause, and task execution all belong to the script
host (§10), because they are decisions about which guest tasks run.

**Invariants**

- The simulation never reads real time. Rendering never reads step time.
- Every registered system runs on every step. Only host control suspends or scales the tick, and
  only for app-level reasons.
- Game speed and pause are game state inside game systems. They never reach the accumulator.
- A paused game still ticks, moves the camera, runs its tools, and renders.
- The fixed step is a constant of the build, not a tunable that changes behaviour.
- Every delta batch carries the tick and alpha it was produced at.

---

## 2. Threads and ownership

Three threads with exclusive ownership of their own state, plus a worker pool.

| Thread | Owns | Talks to |
|---|---|---|
| **Main** | window, input capture, wall clock, composition root | sim via intent buffer and host control; render via present |
| **Simulation** | world, entity stores, transform hierarchy, game systems | render via delta batches; may fork jobs to workers and joins before publishing |
| **Render** | device, render scene, frame packets, GPU residency | sim via feedback packets |
| **Workers** | nothing persistent | resource decode results into the upload queue; sim-forked jobs |

The simulation is a single *owning* thread. It may parallelise a step (propagation, extraction)
by forking jobs at defined points, but it joins before it publishes. The authority is never a pool
of peers.

**Three things cross a boundary**, all immutable once published:

```csharp
// Main → Simulation. The intent buffer: per-tick, game-defined payloads (§9). Also the replay record.
Channel.CreateUnbounded<Intent>();

// Simulation → Render. Incremental state. A dropped delta permanently desynchronises the mirror.
Channel.CreateBounded<SceneDeltaBatch>(new BoundedChannelOptions(2) {
    FullMode = BoundedChannelFullMode.Wait          // back-pressure
});

// Render → Simulation. Snapshots: picking hits, visibility answers, completed offscreen renders.
Channel.CreateBounded<RenderFeedback>(new BoundedChannelOptions(1) {
    FullMode = BoundedChannelFullMode.DropOldest    // a dropped one costs a frame of latency
});
```

The payload from simulation to render is a batch of **deltas**. It is never a "scene
description": it does not describe the scene, it describes what changed since the last batch.

Host control (§1: time scale, suspend, shutdown) is not a message in the intent buffer. It is a
small control block the simulation thread reads at a step boundary. The two share no machinery:
intents are the player affecting the world, and host control is the host telling the loop how to
run. A game's pause and speed are neither. They are game state (§1).

**Invariants**

- No shared mutable state, no locks on the frame path.
- A handle may be *held* by any thread; it may be *dereferenced* only by its owner.
- Pooled buffers are returned by the consumer, never the producer.

---

## 3. Resources

The resource system answers one question: *given an identity, give me something the GPU or the
simulation can use, without blocking.*

### Identity is opaque

```csharp
readonly record struct ResourceId(ulong Hi, ulong Lo);     // 128-bit, opaque to the engine

interface IResourceProvider {
    bool   TryLocate(ResourceId id, out ResourceLocation where);   // where, never what
    Stream Open(in ResourceLocation where);
    IReadOnlyList<ResourceId> Dependencies(ResourceId id);
}
```

The engine never interprets the bits of a `ResourceId`. A game's provider decides what an id
means, how to locate it, what precedence applies between archives, and what its resolution rules
are. (The first game's provider packs a three-part `(Type, Group, Instance)` key, implements
context-group propagation and group-zero fallback, and applies patch-over-base archive precedence
— all inside the provider.)

A provider's location index may be expensive to build and should persist to disk, validated per
archive, so a mismatch rescans one archive rather than all of them. That is the provider's
concern; the engine only requires that `TryLocate` is non-blocking once warm.

### Handles and residency

Handles are minted at load-request time, not scene-build time, and are **content-keyed** so
deduplication is a property of the API rather than a later optimisation.

```csharp
readonly record struct MeshHandle    (uint Value);   // index:24 | generation:8
readonly record struct MaterialHandle(uint Value);
readonly record struct TextureHandle (uint Value);

interface IResourceSystem {
    // any thread — non-blocking, returns an immediately usable handle
    MeshHandle     Mesh    (ResourceId id);
    TextureHandle  Texture (ResourceId id);
    MaterialHandle Material(in MaterialDescription description);
    MeshHandle     TransientMesh(MeshData data);        // procedural, UI, debug
    TextureHandle  ComposedTexture(in TextureRecipe r); // see §8

    // render thread only
    void DrainUploads(TimeSpan budget);
    bool IsResident(MeshHandle handle);
}
```

The generation field makes use-after-release detectable. Without it, a recycled slot silently
renders the wrong asset — a failure with no error and no stack.

### Streaming

Decode happens on worker threads through registered decoders (`IResourceDecoder<T>` — the game
registers one per format it owns). Upload happens on the render thread from a queue drained under
a **time budget** at frame start. A frame is never allowed to stall on residency: a drawable
referencing a non-resident handle is skipped or substituted with a fallback for that frame, and
the frame still presents.

**Invariants**

- Requesting the same id twice returns the same handle and enqueues one decode.
- The resource system has no knowledge of scenes, entities, or gameplay.
- Nothing on the frame path performs file I/O.
- The engine contains no decoder for any game's format.

---

## 4. World: entities and components

The world is the simulation's authoritative model. It is not a rendering structure and it does
not know a renderer exists.

```csharp
readonly record struct EntityId(uint Index, uint Generation);
readonly record struct ComponentTypeId(ushort Value);

sealed class World {
    EntityId Create(EntityGroup group);
    void     Destroy(EntityId id);                            // bumps generation
    bool     IsAlive(EntityId id);

    ComponentTypeId Register<T>(IComponentSerializer<T>? io = null) where T : struct;
    Store<T>        Store<T>() where T : struct;              // dense; Add / Remove / Ref / Has
    TransformHierarchy Transforms { get; }                    // §5 — engine-owned, not a store
    IChangeFeed     Changes { get; }                          // typed Added / Removed / Changed per store
}

// A façade. Holds no data. Random-access ergonomics over dense storage.
readonly struct Entity(World w, EntityId id) {
    ref T Get<T>() where T : struct => ref w.Store<T>().Ref(id);
    bool  Has<T>() where T : struct => w.Store<T>().Has(id);
}
```

**Storage is dense, per type.** Each `Store<T>` is a sparse-set: a packed `T[]`, an
`EntityId.Index → slot` map, and a `slot → EntityId` back-reference. Iteration is a linear scan
of one array. Single-entity lookup is one indirection. Removal is swap-with-last, so nothing may
hold a slot index across a step — only an `EntityId`.

**The registry is open.** The engine ships component types for the facilities most games use,
such as `Transform` (§5), `Renderable` (§6) and `Camera` (§7, §9). That set grows as engine
facilities land; it is not capped. A type belongs in the engine when the engine itself consumes
it (extraction reads `Renderable`, views read `Camera`) or when most games would otherwise each
write the same one. Everything else is registered by the game. Engine-shipped types get no
privileges: they register through the same API, and a game-registered type is a first class
citizen with the same storage, the same change feed, and the same serializer hook. Per-entity presence is a
bitset sized by the registry, so "what does this entity have" is one word per 64 types.

**Entities are created from definitions.**

```csharp
interface IEntityDefinition { void Instantiate(World w, EntityId id); }
```

A definition is resolved through the resource system and decides which components an entity is
born with. Code never hand-assembles a game object.

### Groups

An `EntityGroup` is a **membership and persistence set**: a named set of entities that are saved
together and whose lifecycle is managed together. Every entity belongs to exactly one group from
creation and may be moved between groups. A group is *not* a spatial partition and *not* a
scene-graph node; entities from every group share the same dense stores, so iteration cost is
unaffected. Spatial questions are answered by systems' own indexes, never by groups.

```csharp
enum GroupDetail { Proxy, Full }

sealed class EntityGroup {
    GroupId      Id;
    bool         Loaded;        // simulation axis: are its entities instantiated in the stores?
    GroupDetail  Detail;        // render axis: drawn as its proxy, or as its entities?
    IGroupProxy? Proxy;         // game-supplied stand-in drawn while Detail == Proxy
    IReadOnlyList<EntityId> Entities { get; }
}

void World.Move(EntityId id, GroupId target);
void World.SetLoaded(GroupId g, bool loaded);        // instantiate / serialize-and-destroy
void World.SetDetail(GroupId g, GroupDetail d);      // emits the render deltas, touches no state
```

Two axes, deliberately orthogonal:

- **`Loaded`** is the streaming axis. Unloading a group serializes its entities through their
  registered serializers into the group's save record and destroys them (generations bump, so any
  held id fails deterministically). Loading reverses it. A game whose world fits in memory never
  unloads anything; a game that streams cells does.
- **`Detail`** is purely a render/extraction concern. Flipping a group to `Proxy` emits `Remove`
  deltas for its *detail-bound* renderables (§6) and one `Add` for the proxy; flipping to `Full`
  reverses it. Simulation state is untouched either way. Entities on a `Proxy` group keep
  simulating, routing, and — if their renderable is not detail-bound — drawing.

Both transitions are explicit operations, ordered, and published on the change feed. The engine
never decides *when* to flip either axis; that is the game's policy.

**How the first game uses it.** Everything is `Loaded` for the whole session — the game is an
open world and every sim on every lot keeps living. One global group holds terrain, roads, and
everything on world terrain (the beach, the jogger, the parked car); it is always `Full` and has
no proxy. One group per lot holds the lot's build and objects; its proxy is the baked lot imposter;
`Detail` follows camera distance and the active lot. A sim belongs to the group of the lot it is
standing on, or to the global group, and moving across a lot boundary is `World.Move`. Household
identity — which sims belong together across saves — is a game-layer record that references
entities by id; the engine needs no notion of it.

Reduced simulation fidelity for distant entities is likewise a game concern (a `SimTier`
component that systems consult), not a property of the group: one lot can hold one fully simulated
sim and three background ones.

### Illustration: what the first game registers

Not part of the engine. Listed to show the boundary.

| Game component | Holds |
|---|---|
| `Location` | placement: group, level, tile |
| `Slots` | attachment points and what occupies each |
| `Footprint` | tile occupancy for placement and routing |
| `Animated` | skeleton instance, clip state |
| `Steering`, `Routing` | movement state |
| `Lighting`, `Effect`, `Audio` | per-object emitters |
| `Script` | handle to the foreign gameplay object backing the entity |

**Invariants**

- Component storage is dense and iterable; entity lookup is an array index, never a hash of a
  string.
- Destroying an entity bumps its generation so held ids become detectably stale.
- The world never holds GPU handles for its own use — only to pass through to extraction.
- The engine assembly contains no component type a second game would not also want. This decides
  whether a type is generic enough for the engine, and it does not limit how many types the
  engine ships.

---

## 5. Transform hierarchy

The hierarchy is infrastructure, not a component in a list. It exists for attachment semantics; it
is **not** traversed recursively per frame.

```csharp
sealed class TransformHierarchy
{
    // structure-of-arrays, parallel, indexed by EntityId.Index
    EntityId[]  _parent;
    Transform[] _local;      // translation / rotation / scale — never a matrix
    Matrix4x4[] _world;      // derived, recomputed only when dirty
    ushort[]    _depth;      // parents always sort before children
    NodeFlags[] _flags;      // Dirty | Visible | Static | CastsShadow
}
```

Store decomposed `Transform`, not a matrix: interpolation, per-axis editing, and rotate-in-place
placement all need the components separately. Compose to a matrix once, in the propagation pass.

Propagation is **one linear sweep over depth-sorted entries**, not recursion:

```csharp
for (int i = 0; i < _count; i++)                  // depth-sorted
    if ((_flags[i] & NodeFlags.Dirty) != 0)
        _world[i] = Compose(_local[i]) * _world[_parent[i].Index];
```

`NodeFlags.Static` marks the majority of most worlds. Static subtrees resolve once at load and
are excluded from the dirty sweep entirely.

**Invariants**

- Depth order is maintained on reparent, not recomputed per frame.
- Marking a node dirty marks its subtree dirty; the sweep does not rediscover this.
- The hierarchy is shallow by construction; a game that needs deep skeletons keeps bones in an
  `Animated` component, not in the hierarchy.

---

## 6. Extraction: the render scene

The renderer keeps its **own** retained model of what is drawable. The simulation does not hand
it the world; it hands it changes.

An entity is drawable because it has a `Renderable`, not because its definition names a model.
Adding the component is the explicit act of registering with the render scene; removing it
deregisters.

```csharp
[Flags] enum RenderFlags : byte {
    None = 0, CastsShadow = 1, ReceivesShadow = 2,
    GroupDetailBound = 4        // hidden by the group's proxy while GroupDetail == Proxy
}

readonly record struct Renderable(
    MeshHandle Mesh, MaterialHandle Material, RenderLayer Layer, RenderFlags Flags);

enum SceneDeltaKind : byte { Add, Remove, SetTransform, SetMaterial, SetVisibility }

readonly record struct SceneDelta(
    SceneDeltaKind Kind,
    EntityId       Entity,
    Matrix4x4      Transform,     // Add | SetTransform
    Renderable     Renderable);   // Add | SetMaterial

sealed class SceneDeltaBatch { long Tick; double RealTime; SceneDelta[] Deltas; int Count; }

sealed class RenderScene                      // render-owned, persists across frames
{
    Dictionary<EntityId, int> _lookup;
    RenderProxy[]             _proxies;       // dense, iteration-friendly

    void Apply(in SceneDeltaBatch batch);     // curr → prev, then apply
    void Cull(in View view, float alpha, FramePacket into);
}

struct RenderProxy {
    EntityId       Entity;
    MeshHandle     Mesh;
    MaterialHandle Material;
    Matrix4x4      PrevWorld, CurrWorld;      // two states so render can interpolate
    BoundingSphere WorldBounds;
    RenderLayer    Layer;
    ProxyFlags     Flags;
}
```

Extraction runs at the end of the simulation step: it consumes the change feeds of `Transform`
and `Renderable`, emits one delta per change, publishes the batch. A world at rest emits an
**empty batch**, and the renderer still has everything it needs because it owns the mirror.

**Group detail is applied here.** A renderable flagged `GroupDetailBound` is extracted only while
its group's `Detail` is `Full`. When a group flips to `Proxy`, extraction emits `Remove` for every
bound renderable in the group and `Add` for the group's proxy; flipping back reverses it. The
entities never notice — their components, transforms, and simulation continue. Renderables without
the flag (a sim, a car) are extracted regardless of their group's detail and are culled by distance
like anything else.

**Interpolation is the render side's job.** Because the simulation runs at a fixed step on its own
thread and the renderer free-runs, every proxy keeps its previous and current world transform.
`Apply` shifts current into previous before writing; `Cull` blends them with the frame's alpha.
Without this, motion visibly steps at the simulation rate.

Bounds are computed at extraction, once, from model-space bounds and the world transform. Culling
needs them render-side every frame; deriving them from geometry per frame would defeat the
arrangement.

**Invariants**

- Delta cost is proportional to changes, never to world size.
- A delta batch is immutable once published and carries its tick.
- The render scene is never read by the simulation.

---

## 7. Views, frame packet, and ordering

A frame is **one scene, many views**. Shadow cascades, portraits, thumbnails, and the main camera
are all views over the same render scene, culled independently and submitted in one packet.

```csharp
readonly record struct View(
    CameraData         Camera,
    RenderTargetHandle Target,          // default = backbuffer
    ViewKind           Kind,            // Main | ShadowCascade | Offscreen
    int                DrawableStart,   // slice into FramePacket.Drawables
    int                DrawableCount);

sealed class FramePacket                // pooled, one per in-flight frame
{
    ulong        FrameIndex;
    float        InterpolationAlpha;
    View[]       Views;      int ViewCount;
    Drawable[]   Drawables;  int DrawableCount;   // rented, sorted
    LightData[]  Lights;     int LightCount;
}

struct Drawable {
    ulong          SortKey;
    MeshHandle     Mesh;
    MaterialHandle Material;
    Matrix4x4      WorldTransform;      // already interpolated
    int            SliceIndex;          // which submesh of the model
}
```

Building the scene once and culling it per view is the difference between linear and
multiplicative cost as passes are added.

**Where a view's camera comes from.** The render thread does not decide where a camera is. Camera
state lives in engine `Camera` components on the logic thread, moved by the game's controller
(§9). It is published with each delta batch as batch-level data (previous and current), not as a per-entity `SceneDelta`, and the render
thread interpolates it with the same alpha it applies to proxies. Views derived from it render-side,
such as shadow cascades fitted to the main frustum, are the renderer's job. Where the camera *is*,
is the tick's job.

### Sort keys

```
[ layer:4 ][ depth:24 ][ material:20 ][ mesh:16 ]
   63-60      59-36        35-16         15-0
```

- **Opaque** — depth front-to-back, so early-Z rejects overdraw.
- **Transparent** — depth bit-inverted, yielding back-to-front from the same sort.
- **UI** — the depth field carries explicit draw order instead.

Material before mesh within a depth bucket minimises state changes.

**Invariants**

- The renderer never dereferences a handle it has not confirmed resident.
- A view's drawables are a contiguous slice; no view owns its own array.
- Sorting is stable with respect to submission order within an identical key.

---

## 8. Render backend

### The mesh / material split

Geometry and appearance are separate resources with separate handle spaces. A model is geometry
plus a list of slices; each slice names a material slot. Nothing about appearance is stored in
geometry.

```csharp
sealed class MeshData {
    Vector3[] Positions; Vector3[] Normals; Vector2[] Uvs; uint[] Indices;
    MeshSlice[] Slices;
    BoundingBox Bounds;
}
readonly record struct MeshSlice(int IndexStart, int IndexCount, int MaterialSlot);
```

### Material templates and instances

```csharp
// Template: what the artist authored. Shared by every instance of the model.
readonly record struct MaterialTemplate(
    uint ShaderId, TextureSlots Textures, MaterialParams Defaults, RasterState Raster);

// Instance: template + this object's own state. Value type; hashes on ids and numbers only.
readonly record struct MaterialDescription(
    MaterialTemplate Template,
    TextureSlots     TextureOverrides,   // per-slot TextureHandle, incl. composed textures
    MaterialParams   Overrides);         // tint, opacity, any per-instance scalar
```

Everything that varies per instance — including tint and opacity — lives in the description so
it hashes into the handle. Instances that compose to identical state collapse to one
`MaterialHandle`, one set of uniform binds, and one sort bucket, automatically. A per-instance
value that bypasses the description breaks that property and the sort key with it.

### Texture composition is an extension point

```csharp
readonly record struct TextureRecipe(ResourceId Composer, ReadOnlyMemory<byte> Parameters);
interface ITextureComposer { TextureHandle Compose(in TextureRecipe recipe, IResourceSystem rs); }
```

A game registers composers. The engine addresses the result by the content hash of the recipe, so
recomposition is skipped when state is unchanged, and it accepts either a CPU path (bytes,
cacheable to disk) or a GPU path (render target). The first game's pattern/colour compositor is
one such composer; the engine has no notion of "pattern".

### Passes and the device

A pass consumes a `View` and a slice of drawables and issues draw calls. The minimum set: depth
prepass, shadow cascades, opaque, transparent, UI. Passes are ordered data, registered, not a
hardcoded call sequence.

The renderer is written against a narrow device interface — buffers, textures, programs, render
targets, draw, state blocks — so a second backend is an implementation of one interface rather
than a rewrite.

**Invariants**

- Shader source lives in files, not string literals.
- Raster state is data on the material, not ambient device state that leaks between draws.
- The backend knows meshes, materials, and views; it knows nothing about any game's objects.
- Any object wrapping a device resource is constructed after device creation, never in a field
  initialiser.

---

## 9. Input

Input is captured on the main thread as device state and resolved through bindings. What reaches
the logic thread is the **intent buffer** (§2): a per-tick list of game-defined payloads that
systems consume inside the step. The engine owns the transport and the delivery guarantee. The
game owns the payload types, because "place object" is not an engine concept. Game code never
polls a device directly.

```csharp
readonly record struct InputEvent(InputKind Kind, int Code, bool Down, Vector2 Position, Vector2 Delta);
```

Bindings map physical inputs to **named actions**, so game code expresses intent (`camera.pan`,
`tool.place`) rather than key codes, and rebinding is data. Continuous state (held keys, pointer
position) is separate from discrete events. The intent buffer is serializable, because it is also
the replay record and the input to the capture-and-diff harness. It lives in `Kiln.Core`, so
driving the simulation from a test is the same operation as driving it from a player.

The first version specified an `IInputSink` consumption stack and a `SimCommand` channel. Neither
is an engine primitive. `SimCommand` was the intent buffer with host control smuggled into the
same channel. If UI needs consumption ordering, that is the UI system's own focus policy.

**The camera is a logic-side system.** Camera actions reach the camera the way any intent reaches
any system: on the tick. The camera moves *in the world*. It is constrained by terrain, walls, lot
bounds and the active mode, and it can follow an entity, so it needs the world, which only the
logic thread may read (§2). Its resulting state is published to the renderer (§7) and
interpolated like anything else that moves. The renderer never invents a camera position, and no
client-side controller feeds it one.

Because a game's pause does not stop the engine tick (§1), the camera keeps working while the
game's clock is stopped.

The split follows the usual engine pattern. The engine ships the `Camera` component (projection,
near/far, target view) and the path that publishes it to the renderer. The game supplies the
behaviour that moves the camera: a controller component and system on a player entity. In the
first game that is a "Player" entity carrying a `PlayerController` and a `Camera`. The shape of
that entity, and whether any controller type is general enough to ship with the engine, is still
open. The cost of this arrangement is up to one fixed step of
latency between input and visible camera motion, so the fixed step must be short enough that this
is acceptable.

---

## 10. Scripting host (extension point)

The engine does not run game logic. It exposes what a host needs and defines how a host must
behave.

**Provided by the engine:** the `World`/`Entity` API; typed change feeds; the intent buffer (§9);
a per-step hook (`ISystem`, receiving `StepTime`; registration order is step order); and an
id-mapping utility for hosts that must present entities under a foreign identity.

**Guest time belongs to the host.** A host owns its guests' clock, speed settings, pause, and
which guest tasks run on a given tick. The host is a step participant like any other: it is
called on every engine tick and decides for itself whether its guests advance and by how much.
Pausing the game means the host declines to advance guest time. It never means the engine stops
stepping, because other systems (build tools, camera, UI) must keep running.

**Required of a host** (invariants any game's host must meet):

- Engine systems never call host code directly; they publish changes the host observes.
- Host code runs on the simulation thread inside the step, or on workers it owns — never on the
  render thread.
- A host that presents a foreign identity for an entity maintains that mapping as a lifecycle
  (mint on create, drop on destroy), never as a one-time seeding.
- A host that fails to answer a query returns a defined neutral value into its guest, never an
  exception.

The first game's host loads precompiled managed assemblies written against another engine's API
and redirects them three ways (native entry points, service-interface singletons, static call
rewriting). It also owns the guest's task/yield scheduler, and with it Sims time, the 1/2/3
speeds, and pause. All of that lives in the game layer.

---

## 11. Diagnostics

Diagnostics are engine infrastructure, not debugging leftovers.

- **Logging** — one factory, levelled, with console, rolling file, and in-memory ring sinks.
- **Profiling** — hierarchical scoped timers per thread, double-buffered so the render thread
  reads last frame's tree without locking.
- **Metrics** — named counters and gauges: draw calls, triangles, resident resources, upload queue
  depth, delta batch size, entities per group.
- **Capture** — dump a frame's packet, a resource's decoded bytes, or a render target to disk,
  hashed, for diff against a reference.

The overlay presenting these is a render pass like any other and must be excludable from capture
output.

---

## 12. Layering and the composition root

The engine is a set of libraries. A game is another set that references them. An application
references both and wires them.

The engine is called **Kiln**. The name matters only in that it is not the game's: an assembly
called `Kiln.Core` cannot quietly accumulate Sims-3 knowledge the way one called `OpenTS3.Runtime`
did.

```
Tools ─────▶ Game.* ─────▶ Kiln.Runtime ─────▶ Kiln.Render ─────▶ Kiln.Core
                 │                                                    ▲
                 └────────────────────────────────────────────────────┘

Kiln.Core            EntityId, World, stores, TransformHierarchy, clock, ResourceId,
                     IResourceProvider, handles, math, diagnostics.  No device, no window.
Kiln.Render          RenderScene, views, FramePacket, passes, IRenderDevice. Describes
                     rendering; performs none of it. No graphics API.
Kiln.Render.<api>    one project per backend — the only place GL/Vulkan/D3D calls appear.
Kiln.Runtime         the host: window, device creation, backend selection, input capture,
                     threads, channels, the frame loop, IGame, app lifecycle.
Game.*               an IGame implementation: resource provider + decoders, component
                     registrations, systems, definitions, scripting host.
Tools                entry points only. Main builds a game and hands it to the host.
```

**The host owns the window; the game never touches a device.** A game does not create a window,
select a backend, write a frame loop, or reference a windowing library. It implements `IGame` and
is handed to the host. Everything a game may legitimately say about presentation — title, initial
size, vsync, whether it wants a device at all — it declares as *data* in its manifest, which the
host reads and acts on.

```csharp
interface IGame {
    GameManifest Manifest { get; }                  // name, window title, initial size, vsync
    void ConfigureResources(IResourceRegistry r);   // provider + decoders
    void ConfigureWorld(World w);                   // component registration
    void ConfigureSystems(ISystemRegistry s);       // step participants, in order
    void OnStart(GameContext ctx);                  // ctx: World, resources, input, time
    void OnShutdown();
}
```

`Tools`, in its entirety, is then:

```csharp
static int Main(string[] args) => KilnHost.Run(new OpenTS3Game(), args);
```

A tools project that grows past that shape is doing composition the host should be doing.

**Headless is the same composition minus the device.** `KilnHost.RunHeadless(game)` builds
`Kiln.Core`, the game's provider, decoders and systems, and steps the simulation without ever
loading a windowing library. Headless CLI commands go through it, so they exercise the same
wiring the windowed path does rather than a parallel one.

**The rule:** no `Kiln.*` project references any `Game.*` project; `Kiln.Core` references no
device, window, or imaging library; graphics-API calls appear only inside a `Kiln.Render.<api>`
project. Enforce it in the build, not in review — `tools/guard-layering.ps1` does, and runs as
part of `tools/repo-preflight.ps1`.

---
---

# Part II — What Already Exists

> **Superseded for planning, 2026-09-23.** This part is the 2026-08-26 inventory of the old
> OpenTS3 tree, and it is kept as history. `KILN_MIGRATION_PLAN.md` replaces it for planning:
>
> - its §2 is the current state of both tracks;
> - its §4 phases and §6 requirements register set Kiln's build order;
> - where the shortest path below disagrees with the plan, the plan wins.
>
> **First-game requirements Part I does not yet specify.** These are recorded in the plan's
> register. The requirement lives there until Kiln specifies it here:
>
> - KR-10, a per-group save record without `SetLoaded`;
> - KR-18, game-registered passes, shader programs and material types;
> - KR-19, skinned renderables;
> - KR-20, per-proxy dynamic geometry;
> - KR-22, a UI rendering path;
> - KR-24, audio device ownership in the host.
>
> KR-12 asks for §3 to be split: resource identity, provider and decoder registration land early
> (they gate the plan's phase 2), while handles and residency land late.

Mapping the specification above onto the current codebase. Assessed across
`jjs-build-speedtree` and `origin/main` together, since the engine work landed on trunk and
the data work did not. Assessment date 2026-08-26; the 2026-09-05 revision of Part I does not
change any row's status, but it does move several "Have" items — `GameResourceKey`, the package
index, the ~65 loaders, the TXTC compositor — from "engine" to "the first game's provider,
decoders and composer". They stay done; they change assembly.

## Coverage

| § | Subsystem | State | What exists |
|---|---|---|---|
| 1 | Time and the frame | **Missing** | No clock, no fixed step, no accumulator. Game-speed key handling is logged but unwired. |
| 2 | Threads and ownership | **Partial** | `Channel<SceneDescription>` seam and a logic/render thread split exist. One direction only; no feedback channel; no command queue; overflow policy not differentiated. |
| 3 | Resources — identity | **Have (game layer)** | `GameResourceKey`, both group rules implemented. Becomes the first game's `IResourceProvider`. |
| 3 | Resources — location | **Have (game layer)** | `GamePackageIndex`, `GamePackagePrecedence`, `PersistentPackageCache`. Full install layout, patch precedence, validated disk cache. |
| 3 | Resources — decode | **Have (game layer)** | ~65 loaders in Core: world, lot, terrain, roads, walls, floors, roofs, stairs, openings, catalogs, meshes, textures, SpeedTree. Become registered decoders. |
| 3 | Resources — handles | **Missing** | No handle types, no generations. `MeshRegistry.GetOrQueueUpload` mints a fresh id per call and never deduplicates. |
| 3 | Resources — streaming | **Missing** | No budgeted drain, no residency query, no fallback substitution. |
| 4 | World: entities | **Missing** | No entity id, no component storage, no registry. `WorldObjectInstance` carries a component *id list* mirroring the archive format, but no runtime component system reads it. |
| 4 | World: authoritative state | **Missing** | `WorldObjectRegistryEntry` is an immutable `record` deserialised once from a `.worldctx` snapshot of OBJN. `OpenTS3RuntimeProxyRegistry` exposes only `GetOrCreateProxy` / `GetOrCreateTarget(s)` — **no Add, Remove, Update or Destroy**. It cannot represent a world that changes. |
| 4 | Groups / lot detail state | **Partial** | The imposter/detail concept is fully realised in data — `WorldLotDrawSpan`, `WorldLotImposterLoader`, `WorldLotDetailCompiler`, `LotDetailDrawPlan`, `LotDetailResidencyState` — but as buffer spans on one combined mesh, not as an `EntityGroup` render-detail flip over live entities. No group membership, no world/global group, no `Move`. |
| 5 | Transform hierarchy | **Missing** | No hierarchy, no transform propagation. Transforms are baked into vertices at assembly time. |
| 6 | Extraction / render scene | **Missing** | `RenderNode` / `ISceneProvider` types are declared in `OpenTS3.Engine/Runtime/Scene/` with zero callers. No render-side mirror, no interpolation state. |
| 7 | Views and frame packet | **Partial** | `Drawable`, `CameraData`, `LightData` and a pooled per-frame description exist. Single render target, no view list, no sort keys. |
| 8 | Mesh / material split | **Missing** | `SubMeshRange` fuses index range, ~40 material fields, and decoded textures. No material handle space; materials bake at upload. |
| 8 | Material instancing | **Have, in data (game layer)** | Per-instance pattern state is decoded and understood — `WorldObjectInstance.InlineComplatePreset`, the whole `PresetCompositor` namespace, `SurfaceTxtcCpuCompositor`, `TxtcMaterialArtifact`. Becomes the first game's `ITextureComposer`; not yet a runtime material instance. |
| 8 | Raster state | **Have** | `SubMeshRasterState` is already an API-independent cull/winding/depth policy with evidence-backed presets. |
| 8 | Passes | **Missing** | Draw order is inline in the renderer. No pass registration, no depth prepass, no shadow cascades. |
| 8 | Backend abstraction | **Partial** | `IRenderer` exists with one implementation. GL calls are widespread outside it. |
| 9 | Input | **Have** | `Keyboard`, `Mouse`, `KeyBinding`, `MovementInput` — thread-safe queues, named bindings, held-state separated from events. |
| 10 | Scripting host — mechanism | **Have (game layer)** | ~50 `*HostBridge` implementations, `ManagedGameLoadContext`, `ManagedSimulationBridges`, `ManagedAppDomainSeeder`, generated native exports. All three redirection mechanisms present. EA assemblies load and the state machine advances 120 post-InWorld ticks. |
| 10 | Scripting host — the world it hosts | **Missing** | The mechanism is complete; there is nothing behind it. ~33% of host methods (621 of ~1,901) return neutral `default`/`null`/`0`. `OpenTS3RuntimeLotManagerSeeder` is **1,508 lines of reflection seeding EA's static lot tables** because no lot state exists to project. Objects materialise via `GetUninitializedObject` — no constructor runs. |
| 10 | Task / yield scheduling | **Partial** | `ManagedTaskScheduler` intercepts `Simulator.AddObject` correctly, but execution is an opt-in allow-list. The native runtime's `MonoScriptHost` keeps `TaskMap`, `TaskStacks`, `SleepingObjects`, `WakeRequests` — the structures a general scheduler needs to model. |
| 11 | Logging | **Have** | `Core/Logging` — factory with console, file, and in-memory sinks. |
| 11 | Profiling | **Have** | `Core/Profiler.cs` — hierarchical, dual-thread snapshots. |
| 11 | Metrics | **Partial** | `Core/Diagnostics/MetricRegistry` exists; not wired to render counters. |
| 11 | Capture | **Have, extensive** | Screenshot capture, hashing, pixel diff, and dozens of fixture probes. This is the project's strongest correctness asset. |
| 12 | Layering | **Partial** | Core / Runtime / Tools.Cli exist with the right direction of dependency. Core is device-free. `OpenTS3.Engine` is a fourth project holding renderer, labs, UI, and ~200 CLI commands. No engine/game split exists yet; today "Core" is the game's data layer. |
| 12 | Composition root | **Missing** | Entry points construct subsystems inline. |
| — | Reference-runtime evidence | **Have** | `docs/native_notes/ts3w_runtime_symbol_inventory.md`, `ts3w_object_model_inferred.md`: component classes, object managers, render-side scene, threading and messaging evidence from `TS3W.exe`. |

## Where the weight actually is

**The hard half is done.** Everything in §3 below the handle layer — identity, precedence,
location, decode — is built, and it is the part of a reimplementation that cannot be shortcut,
guessed, or borrowed. Sixty-five loaders covering terrain through to per-species flora, backed by a
capture-and-diff harness, is years of the work. Under the revised layering all of it is *game*
code, which is exactly where it should be.

**The engine half is largely absent.** §1, §4, §5, §6 have no implementation at all. There is no
clock, no entity, no hierarchy, no render-side mirror. What plays the role of a scene today is a
single combined mesh assembled per world load.

**The gameplay host is a complete mechanism hosting nothing.** The three redirection mechanisms
work, EA's assemblies load, and `GameStates.mStateMachine.Update(dt)` advances. But what it
advances against is a frozen snapshot. `WorldObjectRegistryEntry` is immutable and deserialised
once from `.worldctx`; the proxy registry has no mutation surface at all. An object EA moves still
reports its load-time position forever; an object EA creates at runtime has no entry and resolves
to `default(T)` exactly as it did before the registry existed. This is a boot demo, not a
foundation — and reading it as one leads directly to the mistake of designing the engine's entity
model to mirror the registry, which would bake the dead end into the new work.

`OpenTS3RuntimeLotManagerSeeder` is the clearest evidence: 1,508 lines whose entire purpose is
making EA's `LotManager` return non-null. In an engine with a world model that file does not
exist, because `LotManager`'s data would be a projection of real lot state.

**The bridge stalled because it ran out of things it could fake.** The last managed-runtime
commits are 2026-05-25/26, every one titled "placeholders", "null returns", "footholds", or
"ABI rows". The files were last touched 2026-06-06 in a checkpoint commit. Everything in the
three months since — SpeedTree, flora, world render tier, Lab, Launcher — is visualisation.
The remaining 33% of host methods cannot be filled with neutral returns; they need real state
to answer from. §4 and §5 are therefore a **prerequisite** for the managed runtime advancing
further, not a parallel track.

**Three position representations, none reconciled.** Baked vertex positions inside the
combined `MeshGeometry`, frozen entries in `.worldctx`, and EA's own object state.
`SetObjectPosition` writes outward to EA while the viewer separately nudges vertex ranges —
two writes to two representations that never meet. This is why the current build works as a
demo and cannot become a game.

**Diagnostics and capture (§11) is genuinely complete for this stage**, because the
correctness bar demanded it. It is the one subsystem that is both finished and load-bearing.

**Three things exist as data but not as runtime concepts.** Lot detail state, per-instance
material composition, and per-object spatial identity are all fully decoded and
evidence-backed, but expressed as spans, artifacts, and records rather than as world state.
The knowledge is there; the runtime model to hold it is not.

**The native reference confirms the shape.** The inventory of `TS3W.exe` shows the shipped engine
already had a render-side scene table, per-kind component classes with serializers, object
managers as persistence units, and message-fed render managers. The specification is not a
departure from how the game was built; it is that design with its identity, registry, storage,
and change-propagation mechanisms modernised.

## The shortest path to a running engine

> **Superseded by `KILN_MIGRATION_PLAN.md` §4.** The Kiln track there is: §1 clock (KR-01/02) →
> §5 hierarchy (KR-06) → host loop, simulation thread and intent buffer (KR-03/04) → §3 identity
> and provider (KR-12, KR-14) → §4 remainder (KR-07 to KR-11) → §6/§7/§8 with handles (KR-05,
> KR-13, KR-15 to KR-20, KR-23) → input and UI (KR-21, KR-22, KR-24). The goal stated below,
> running the simulation before improving the viewer, still holds.

Ordered by what unblocks the most, from the specification rather than from the current
shape. **The goal is running the simulation, not improving the viewer** — an ordering that
front-loads handles and the material split is the visualisation bias that produced the
current state, and is explicitly rejected here.

1. **§12 Split the assemblies.** ✅ *Done 2026-09-05.* Create `Kiln.Core` empty and move nothing into it yet; add
   the build check that no `Engine.*` references `OpenTS3.*`. Everything below lands on the
   correct side from the first commit.
2. **§1 Clock and frame loop.** Nothing above it can be correct without a defined step, and
   the sim/real split has to exist before anything ticks.
3. **§4 Entities, registry, groups.** The single largest unblock in the project. It ends the
   frozen registry, gives the managed bridge something real to answer from, and retires
   `OpenTS3RuntimeLotManagerSeeder` rather than growing it. OBJN becomes a loader that populates
   a group.
4. **§5 Transform hierarchy.** Required for slots, attachment, and anything a sim carries.
5. **§10 Invert the bridge onto that state.** `GetProxy` and `IQueries` read live entities;
   inward object creation arrives through the world; the guid ↔ `EntityId` map is maintained
   as a lifecycle, not seeded once. Widen `ManagedTaskScheduler` from allow-list to general
   execution — this is what turns 120 ticks of a state machine into sims doing things. Sims
   time, the 1/2/3 speeds, and pause land here, in the scheduler, not in the §1 clock.
6. **§3 Handles and residency.** Every later subsystem addresses resources by handle; adding
   it later means rewriting all of them. `GameResourceKey` becomes the first provider's private
   key type behind `ResourceId`.
7. **§6 + §7 Extraction and views.** Both are mechanical once the world model exists, and
   they finally give the renderer a single source of truth for object position.
8. **§8 Mesh/material split**, then the renderer behind a device interface.

Steps 3, 5, and 7 have a natural verification: the existing capture-and-diff harness makes
each one provable rather than assumed.
