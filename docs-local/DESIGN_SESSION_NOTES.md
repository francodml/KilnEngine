# Design Session Notes — 2026-09-06

Consolidated code sketches and decisions from a working session with Claude. **None of this is
landed code** except where marked, and several items were revised mid-session — read the status
markers before using anything here.

Companion to `ENGINE_FROM_SCRATCH.md`, which remains authoritative. Where this document
contradicts it, that is flagged as a proposed spec change, not a fait accompli.

**Revised 2026-09-23.** A decision on engine tick vs game time has been applied to the spec
(§1, §2, §7, §9, §10). It rewrites §6 below, withdraws parts of §5 and §7, and resolves two open
decisions. The affected sections are marked **DECIDED** or **SUPERSEDED**.

**Aligned to `KILN_MIGRATION_PLAN.md` (2026-09-23).** That plan now drives Kiln's work order and
holds the requirements register (`KR-xx`). The plan treats these notes as authoritative over
Part I where the two disagree. Its KR-00 asks for the notes' renames to be written back into
Part I, and that has been done. §9 carries the plan's deadlines for each open decision. The plan
still describes the camera render-side; that is being corrected in the plan's own session (§9 item 5).

| Marker | Meaning |
|---|---|
| **LANDED** | in the repo now |
| **PROPOSED** | suggested, not written |
| **SUPERSEDED** | proposed earlier in the session, then withdrawn — recorded so it isn't re-derived |
| **[§N]** | quoted from `ENGINE_FROM_SCRATCH.md`, not invented here |

---

## 1. World partial-class split — LANDED

`World` is now `sealed partial` across three files. State stays in `World.cs`.

| File | Holds |
|---|---|
| `World.cs` | type doc, **all fields**, properties, Groups section, Internals |
| `World.Entities.cs` | `CreateEntity` ×2, `DestroyEntity`, `IsAlive`, `GroupOf`, `Entity(id)` |
| `World.Components.cs` | `RegisterComponent`, `IsRegistered`, `Store<T>`, `HasComponent`, `AddComponent`, `RemoveComponent`, `Touch` |

**Why fields did not follow their methods:** `_presence` is written from both partials,
`EnsureEntityCapacity` resizes four arrays as a unit, and `DestroyEntity` reads `_presence` to
know which stores to clear. Keeping state in one file makes the "one type enforces the invariant"
property visible — that invariant being that presence mask, stores, and change feed never
disagree.

**Rejected:** splitting into real classes (`World.Entities.Create`, `World.Components.Add`) where
each owns state. Would produce bidirectional coupling — `Components.Add` calling
`Entities.IsAlive`, `Entities.Destroy` calling `Components.RemoveAll` — over a shared mutable
array. Also collides with existing names: `EntityGroup.Entities` is `IReadOnlyList<EntityId>`,
and `World.Entity(id)` is the façade.

---

## 2. Systems — PROPOSED

**Interface, not base class.**

```csharp
public interface ISystem
{
    void Step(World world, in StepTime time);
}
```

Reasoning:

- Systems are game types [§0: "a game owns ... its systems"]. C# gives one inheritance slot;
  the engine shouldn't spend it. Same argument as the open component registry.
- **There is no shared implementation to inherit** — the test that actually settles base-vs-
  interface. A base class would hold `protected World World;` and nothing else.
- Cross-cutting concerns (§11 per-system timing) belong to the **runner**, which wraps each
  `Step` call. That can't be defeated by a system forgetting `base.Step()`.

`World` passed per-step rather than via constructor, because `ConfigureSystems(ISystemRegistry)`
doesn't receive the world — constructor injection would force that signature to change. Systems
still hold persistent indexes as fields; only the world reference arrives per step.

**Do not add `int Order { get; }`.** Registration order is step order [§12: "step participants,
in order"]. Keeping it in the registry means step order reads in one place instead of being
scattered across game types.

**Escape hatch if shared behaviour ever appears:** ship `public abstract class SystemBase : ISystem`
as an *optional* convenience. The contract stays the interface.

**Status (2026-09-23):** `ISystem` is now `public`, and `System.cs` is deleted. The signature is
still `Initialise(World)` + `OnStep(World)`, not the one above. Settling it is KR-02 in
`KILN_MIGRATION_PLAN.md`, which gates the plan's phase 3.

~~**Fix needed:** `ISystem` was `internal` — games can't implement an internal type.~~

~~**Delete:** `Kiln.Core/Simulation/System.cs`.~~ Beyond the CS0513 error (`abstract` member in a
non-abstract type), a class named `System` inside `Kiln.Core.Simulation` shadows the `System`
namespace for every file in that namespace.

---

## 3. Hosting and composition — PROPOSED

### Folder split by audience

- **`Kiln.Runtime/Hosting/`** — the contract a game implements plus the entry point:
  `IGame`, `GameManifest`, `GameContext`, `KilnHost`. Everything a game author reads.
- **`Kiln.Runtime/Composition/`** — the machinery that consumes it. A game author never opens this.

`IGame` and `KilnHost` are mutually referential (`KilnHost.Run(new OpenTS3Game(), args)` is all of
`Tools`), so splitting them across folders fragments the front door.

The name `Composition` is slightly a trap: §12's own revision note records that the composition
root *moved out of* `Tools` into the host. What's left in that folder is wiring, not contract.

### The contract [§12]

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

Interface, not `BaseGame`, for the same reason as `ISystem`. Members a game leaves empty are
covered by default interface members without spending its inheritance slot.

### What Composition actually contains

```csharp
sealed class GameComposer
{
    public ComposedEngine Compose(IGame game, CompositionOptions options);
}
```

Composition owns exactly two responsibilities: **construction order** and **implementation
choice**. The order is not arbitrary:

1. Diagnostics first (§11) — log factory/profiler/metrics must exist before anything that logs
   during construction.
2. `World`, then `game.ConfigureWorld(w)` — component registration completes before any system
   resolves a `Store<T>`.
3. Resource system, then `game.ConfigureResources(r)`.
4. Device, **if wanted** — §8 invariant: "any object wrapping a device resource is constructed
   after device creation, never in a field initialiser."
5. Render scene + extraction.
6. `Simulation`, then `game.ConfigureSystems(s)`.
7. Build `GameContext`, call `game.OnStart(ctx)`.

Teardown walks this in reverse.

Other types:

- **`ComposedEngine`** — the assembled graph as a value: `World`, `Simulation`, `IResourceSystem`,
  `RenderScene`, nullable `IRenderDevice`, diagnostics handles.
- **`CompositionOptions`** — chiefly whether a device is wanted, plus backend override from argv.
- **`IRenderDeviceFactory`** + backend selection — §8's "a second backend is an implementation of
  one interface" only pays off if exactly one place chooses.

**One composer with a device-optional switch, never a `HeadlessComposer` alongside a
`WindowedComposer`** — §12: "headless is the same composition minus the device ... so they
exercise the same wiring the windowed path does rather than a parallel one." Writing the second
type means the invariant is already broken.

### What does NOT go in Composition

- `ISystemRegistry` / `IResourceRegistry` **and their implementations** → `Kiln.Core`. A headless
  test must be able to compose a simulation without referencing `Kiln.Runtime`, or `RunHeadless`
  stops being "the same wiring minus the device" and becomes a second path.
- Channels and threads → `Threading/`.
- Window and input capture → `Hosting/` and `Input/`.
- `IGame`, `GameManifest`, `GameContext` → `Hosting/`. Composition *constructs* `GameContext`;
  it doesn't define it.

### Open decision

How does `Kiln.Runtime` obtain a backend? §12 says the game never selects one, so the host must —
meaning Runtime references `Kiln.Render.OpenGL`. Legal under the guard (rule 1 permits
`Kiln.*` → `Kiln.*`; rule 3 only bars Runtime from a graphics-API *package* like
`Silk.NET.OpenGL`). But Runtime then gains a reference per backend that ever ships. The
alternative — `Tools` registers the factory — puts selection back in the entry point §12 wants
to be one line.

**Blocker:** `Kiln.Render.OpenGL.csproj` has no `ProjectReference` to `Kiln.Render`, so it cannot
implement `IRenderDevice` yet.

---

## 4. IEntityDefinition — REFERENCE

### Engine side, in full — LANDED

```csharp
public EntityId CreateEntity(GroupId group, IEntityDefinition definition)
{
    var id = CreateEntity(group);       // engine owns identity, group, transform
    definition.Instantiate(this, id);   // game decides what it's made of
    return id;
}
```

By the time `Instantiate` runs the entity is fully live — slot allocated, generation resolved,
`_alive` set, group membership recorded, transform attached. The definition's only authority is
**composition**: which components, with what initial values.

### Resource ≠ definition

The distinction that makes it click:

| | The resource | The definition |
|---|---|---|
| What | bytes on disk — OBJD/OBJN, or a prefab listing components + init data | a live C# object |
| Made | once per **kind**, by the game's `IResourceDecoder<T>` | at decode time, holds the decoded result |
| Used | never directly at runtime | once per **entity**, via `Instantiate` |

This is why `Instantiate` only needs `World`: handles were minted at decode time, once per kind
[§3: "requesting the same id twice returns the same handle and enqueues one decode"].

**Implied constraint not stated in the interface:** one definition instance instantiates many
entities, so definitions must be immutable and stateless. Per-instance data belongs in the
components they add. Worth writing into the doc comment.

### Game side — PROPOSED (illustrative; these types don't exist yet)

```csharp
sealed class ObjectDefinition : IEntityDefinition        // one per OBJD, built by the decoder
{
    readonly MeshHandle _mesh;                            // minted at decode, shared by every instance
    readonly FootprintData _footprint;

    public void Instantiate(World w, EntityId id)
    {
        w.AddComponent(id, new Renderable(_mesh));
        w.AddComponent(id, new Footprint(_footprint));
        w.AddComponent(id, new Slots(_slotCount));
    }
}
```

Caller — the lot loader, i.e. §12's "OBJN becomes a loader that populates a group":

```csharp
foreach (var placed in lot.Objects)
{
    var def = _definitions.Get(placed.ObjectId);                      // cached per kind
    var id  = world.CreateEntity(lotGroup, def);
    world.AddComponent(id, new Location(placed.Level, placed.Tile));  // per-placement
}
```

**The division:** the definition supplies what's true of the *kind*; the call site supplies what's
true of this *placement*. Every chair shares a mesh and footprint; only this chair is at (14, 9).

### The prefab question — UNRESOLVED

A data-driven prefab is just one implementation:

```csharp
sealed class ComponentListDefinition(IReadOnlyList<(ComponentTypeId Type, byte[] Data)> parts)
    : IEntityDefinition
{
    public void Instantiate(World w, EntityId id)
    {
        // loop, deserialize via the registered IComponentSerializer<T>
    }
}
```

The serializer hook already exists on `RegisterComponent`. So the interface only earns itself if
some kind's construction needs *code* rather than data.

Checking §4's illustration table: `Location`, `Footprint`, `Slots` are plain data. `Script` looks
like it needs code, but §10 says the host observes changes rather than being called — so the
script host watches the change feed for `Added` and creates its own object, with no definition
involvement.

**If that holds for every kind, collapse `IEntityDefinition` to a concrete `EntityPrefab`.**
Nothing implements it today, so this is free to decide. Easier to reverse now than once games
depend on it.

### What it is not

- **Not the reload path.** `SetLoaded` restores from a group's save record — mutated state. A Sim
  played for ten hours cannot be reconstructed from its definition.
- **Not a prefab tree.** No nesting or inheritance in the contract.

### Open edge

`Instantiate` receives the whole `World`, not the `Entity` façade, so an implementation can create
*additional* entities — presumably how slot children work (§5 attachment). That grants more
authority than "decides which components an entity is born with" implies. If definitions should
be confined to single-entity composition, `Entity` is the tighter parameter.

---

## 5. Camera and views — DECIDED 2026-09-23 (supersedes the original proposal)

### Decision

**The camera is a logic-side system.** It takes input on the engine tick and moves *in the world*:
constrained by terrain, walls, lot bounds and the active mode, and able to follow an entity. It is
not a client-side controller feeding the renderer an arbitrary position. This works because a game
pause never stops the engine tick (§6): the camera keeps running while the game's clock is stopped. Spec
§9 and §7 now say this.

- Camera *behaviour* is game code, a system in the game's step order.
- The engine provides input delivery on the tick and the path that publishes the camera to the
  renderer.
- The camera travels to the renderer as **batch-level** data on `SceneDeltaBatch` (previous and
  current), not as a per-entity `SceneDelta`, and is interpolated with the batch alpha. The
  shape-mismatch argument below still holds: a camera is not a drawable.
- The accepted cost is up to one fixed step of input→motion latency. The fixed step has to be
  short enough for that.

### Original proposal — SUPERSEDED

The session originally argued that camera state must never leave the render side, because
"camera smoothing reads real time only" and a fixed-step camera costs latency. That rested on
treating the camera as presentation. It isn't: it navigates the world. The table below is kept
as the record of what was withdrawn.

| Thing | Proposed home | Now |
|---|---|---|
| `CameraData` — view + projection matrices, frustum, near/far | `Kiln.Render/Views/`, per frame | Still `Kiln.Render/Views/`; now *derived* render-side by interpolating published camera state |
| The controller — orbit, pan, smoothing, follow | game side, called by frame loop, real time | **Withdrawn.** A game system on the logic tick |
| Camera *intent* — "follow this Sim", "locked to this lot" | game-registered component, sim step | Unchanged; the camera system reads it |

`CameraData` keeps the home the spec chose: §7's `View(CameraData Camera, RenderTargetHandle
Target, ViewKind Kind, ...)`. It is pure data with no behaviour, and its shape is still open.

**SUPERSEDED: a game-supplied `IViewProvider.BuildViews(FramePacket, in RealTime)`** on the render
side. The main camera no longer needs game code render-side: its state arrives in the batch.
Render-derived views such as shadow cascades are the renderer's own job. Whether any view ever
needs game code on the render thread is open, and nothing currently requires it.

**SUPERSEDED: following an entity by reading the render proxy.** That existed only because a
render-side controller could not read the `World`. The camera system runs on the tick and reads
the world directly.

### Camera as an entity — DECIDED 2026-09-23

The camera lives on a **"Player" entity** carrying a `PlayerController` component and a `Camera`
component, much like Unity or Unreal. The entity has a `Transform`, so hierarchy attachment
(bolting the camera to a vehicle or a head) comes for free.

- **`Camera` is engine-shipped.** It is a facility most games use, and the engine consumes it
  (views read it, extraction publishes it).
- **The controller is game behaviour.** Whether `PlayerController`, or a generic base for one,
  ships with the engine is open.
- **The rest of the Player entity's shape is open**, e.g. whether it holds selection state.

**SUPERSEDED: "avoid a `Camera` component in `Kiln.Core`; the engine ships exactly two component
types."** The two-component rule is removed from the spec. The engine ships component types for
the facilities most games use, and registers them through the same API as game types, with no
privileges. See spec §4.

---

## 6. Time model — DECIDED 2026-09-23, applied to spec §1

### The decision

**The engine tick and game time are separate, and must not be conflated.** The engine advances
the world in fixed steps and runs every registered system on every step. That is the whole of
the engine's time concern.

The following are **game** concerns:

- Sims time
- the 1/2/3 speeds
- pause
- whether things in the world "think"

More specifically, they are **script host** concerns, because they decide which Tasks emitted by
EA's managed code run.

A paused game is not a paused engine. Build/buy mode, the camera (§5), and many other systems keep
ticking while the Sims stand still.

| Layer | Owner | What it is |
|---|---|---|
| Fixed step + accumulator | **Engine** | how often the world ticks. Constant, never paused or scaled by the game |
| Sims time, speeds, pause | **Game → script host** | how far guest time advances per tick, and whether it advances at all |
| Which entities think / which Tasks run | **Game → script host** | the scheduler's decision, made each tick |
| Other in-universe rates | **Game** | animation playback, alarms, needs decay: game systems reading game time |

**Host control is app-level only** (per `KILN_MIGRATION_PLAN.md` D1). The engine keeps a
host-control block with three settings, read at a step boundary:

- `SetTimeScale`, a generic debug slow-motion;
- a suspend, for a debugger break or possibly a minimised window;
- shutdown.

Suspending stops *every* system, which is exactly why no game builds its pause on it. OpenTS3's
pause button is a game intent handled by the script host.

**Systems choose their clock explicitly.** The game holds its clock as a game-owned state record
in the World.

- A system that must freeze with the Sims reads `gameDelta` from that record. Routing,
  locomotion and Sim animation do this.
- Everything else reads `StepTime`: placement ghosts, UI tasks, Build/Buy, lot presentation
  policy.

### SUPERSEDED in this section

- **"The engine owns one opaque scalar" as the way a game pauses or changes speed.** Withdrawn.
  Pausing the game through it would freeze build mode and the camera. The scalar survives only as
  host control's debug time scale.
- **"Which layer does TS3's speed 3 scale? … expect mostly scaling the tick rate."** Withdrawn.
  Game speed never touches the engine tick. How the script host realises speed 3 (more guest
  time per tick, more Tasks run per tick, or a mix) is the host's design and is invisible to the
  engine.
- **An interim 2026-09-23 wording, "the engine has no time scale and no pause at all".** It went
  further than the decision and is withdrawn in favour of the app-level host control above.

### Naming — applied to spec §1

- `SimTime` → **`StepTime`**. This matters more now than when it was proposed: "sim time" is
  literally the game's Sims clock, the thing the engine must not own.
- `SetSimSpeed` → **`SetTimeScale`**, as a host-control debug setting only.

---

## 7. Input and the player — CURRENT MODEL

This section replaces everything said about input earlier in the session. See §8 for what was
withdrawn.

> **Revised 2026-09-23.** The original model said camera input never crosses to the logic thread.
> That is withdrawn: the camera is a logic-side system (§5), so its actions must reach the tick.
> The intent buffer stays the one engine crossing, as `KILN_MIGRATION_PLAN.md` (KR-04) requires.
> An interim wording that replaced it with an "input frame" is withdrawn.

### The question that partitions everything

**What must be recorded to replay this tick and get the same world back?**

From RTS/netcode lineage; it's why lockstep replays work, and it's not a matter of taste. What
crosses to the tick is exactly that: camera actions (the camera moves in the world), tool actions,
"place object X at tile (14,9)", "queue interaction Y on Sim Z". UI hover and tooltips that no
system reads do not cross.

### Four concepts, one job each

1. **Device state** (main thread, real time) — poll the window: held keys, pointer position,
   axes, plus an event list for edges. No queue, no stack, no subscription.
2. **Bindings** (main thread, data) — raw key/button → named action. Rebindable, serialized.
   Genuinely engine (Unity, Unreal, Godot all ship it). Systems see `camera.pan`, not key codes.
3. **Intent buffer** (the one crossing) — a per-tick list of game-defined intent structs. The
   engine owns the transport and the delivery guarantee. The game owns the payload types, because
   "place object" is not an engine concept. It is serializable, because it is also the replay
   record and the capture/diff harness input.
4. **Host control** (not input) — debug time scale, suspend (debugger break, possibly minimised
   window), shutdown. App-level only, never the game's pause or speed (§6). It shares no
   machinery with intents; a small control block read at a step boundary suffices.

### Placement

| | Where | Why |
|---|---|---|
| Device state, bindings, actions | `Kiln.Runtime/Input/` | platform-facing, main thread |
| Intent buffer | `Kiln.Core/Simulation/` | the sim consumes it, and a headless test must feed it |
| Host control | with the step accumulator | governs stepping, not the world |

The intent buffer in Core is load-bearing: driving the simulation from a test becomes *the same
operation* as driving it from a player, which is what makes the capture-and-diff harness possible.
The plan's gate G3 relies on this: the same intents and the same step count must give the same
result across runs.

### Picking — decided by the migration plan

The plan's phase 5, feature 11, uses the render→sim feedback channel (KR-05):

```
click → pick request → render thread rays against RenderScene
      → RenderFeedback { EntityId hit } → simulation
      → game system turns it into selection or an interaction
```

It is pixel-exact against what was drawn, at one frame of latency. Terrain raycasts for placement
are separate: they become a game-side query on terrain data, not a renderer call.

**The camera is how the player points, not what the player is.** It supplies the ray; that's the
whole relationship.

### How the player exists in the world

The engine has no player *concept*: it ships the `Camera` component (§5), and a game composes its
player from components. The player is three separable things, and games bind them differently —
an FPS fuses all three onto one entity; an RTS has no avatar at all:

1. **A command source** — intent entering the simulation
2. **Zero or more entities** — a body, a cursor, an owned household
3. **A viewpoint** — where the camera is

"Which entity does the player control?" is a game-registered component:

```csharp
world.RegisterComponent<PlayerControlled>();   // avatar games
world.RegisterComponent<PlayerOwned>();        // RTS / TS3 — household membership
```

The engine ships neither. For an avatar game that store has one entry; for TS3 it has the active
household's Sims.

The consumer is an ordinary game system, first in step order — it needs no special engine support:

```csharp
sealed class PlayerIntentSystem : ISystem       // game assembly
{
    public void Step(World world, in StepTime time)
    {
        while (_intents.TryRead(out var intent))
            switch (intent)
            {
                case MoveIntent m:
                    ref var steering = ref world.Store<Steering>().Ref(_avatar);
                    steering.Desired = m.Direction;
                    break;

                case QueueInteraction q:
                    world.Entity(q.Target).GetComponent<Script>().Enqueue(q.Interaction);
                    break;
            }
    }
}
```

**For OpenTS3 specifically:** the player is a command source, a `PlayerOwned` tag on the active
household's Sims, a selection (sim-side, since it gates which commands are legal, mirrored to
render as a material change for the highlight), and a "Player" entity with a `PlayerController`
and a `Camera` that is not attached to any Sim (§5). Sims stay autonomous — the player appends to a queue, the
AI executes. Whether they execute *this tick* is the script host's decision (§6).

---

## 8. Superseded during this session

Recorded so they aren't re-derived.

| Withdrawn | Replaced by | Why |
|---|---|---|
| `SimTime`, `SetSimSpeed` | `StepTime`, `SetTimeScale` (host-control debug setting only) | "Sim" means a character in this project |
| Engine time scale as the way a game pauses or changes speed; "speed 3 scales the tick rate" (2026-09-23) | Sims time, speeds, pause and Task execution are the script host's; host control is app-level only | A paused game still ticks: build/buy, camera and other systems keep running. Scaling or halting the tick for game reasons conflates engine and game |
| Camera controller on the view side, real time; follow-by-render-proxy (2026-09-23) | camera as a logic-side game system; state published batch-level and interpolated | The camera moves *in the world* and needs it. It is not a client-side position fed to the renderer. Now a Player entity with `PlayerController` + engine `Camera` (§5) |
| "Camera input never crosses" (2026-09-23) | camera actions ride the intent buffer like any other intent | The camera consumes its actions on the tick |
| Interim "input frame replaces the intent buffer" (2026-09-23, same day) | the intent buffer, as before | The migration plan builds on the intent buffer (KR-04, phase 3) |
| `IInputSink` stack as an engine primitive | nothing — it's UI focus policy | Not foundational. If the UI needs consumption ordering, the UI system owns it. Treating it as foundational is what made the camera look like a "player controller" |
| `SimCommand` as its own concept | the intent buffer (§7) | It was the intent buffer under another name, with host control smuggled into the same channel — two unrelated things sharing a pipe |
| `sealed class PlayerController : IInputSink, IViewProvider` | camera system + `PlayerIntentSystem`, both logic-side, unrelated (camera moved logic-side 2026-09-23) | Conflated "input" and "commands" as two layers when they're one thing counted twice |
| `Kiln.Core/Input/` folder | **delete it** | The simulation has no input. An input abstraction in Core guarantees a second one grows to translate into it |

---

## 9. Open decisions

Each decision stays with Kiln. `KILN_MIGRATION_PLAN.md` §5 records what OpenTS3 needs from each
one and the phase it must be decided by, reproduced here.

1. **`IEntityDefinition` vs a concrete `EntityPrefab`** — does any object kind need construction
   *code* rather than data? Nothing implements the interface yet.
   *Decide before phase 4 (KR-11).* OpenTS3 needs definitions resolved through the resource system
   from game data (OBJD, catalog), with per-instance overrides: design material, pattern preset,
   outfit. Either shape works if both hold.
2. **Backend acquisition** — does `Kiln.Runtime` reference every `Kiln.Render.<api>` project, or
   does `Tools` register a factory?
   *Decide before phase 5.* OpenTS3 needs a headless composition with no backend loaded (phase 3
   relies on it). It is otherwise indifferent.
3. ~~**Which layer TS3's speed 0–4 scales**~~ — **resolved 2026-09-23** (plan D1): never the
   engine tick. It is script-host state (§6).
4. **`Instantiate(World, EntityId)` vs `Instantiate(Entity)`** — how much authority a definition
   should have.
   *Decide before phase 4.* OpenTS3 needs definitions that create attached children (slotted
   parts, CAS accessories), so it needs hierarchy access: `World` or an equivalent.
5. ~~**Camera as entity or not**~~ — **decided 2026-09-23** (§5): a "Player" entity with a
   game `PlayerController` and an engine-shipped `Camera` component. Still open: whether a
   controller type ships with the engine, and the rest of the Player entity's shape. **The
   migration plan still describes the older render-side model.** That is being corrected from the
   plan's own session, not edited here.
6. ~~**Picking**~~ — **resolved by the plan**: render→sim feedback (KR-05, phase 5 feature 11). §7.
7. **`ISystem` signature** — *decide before phase 3 (KR-02).* OpenTS3 needs a step-time
   parameter, with the game adding its own clock on top (plan D1), and registration order as
   step order. §2's `Step(World, in StepTime)` meets this. The code's
   `Initialise(World)` + `OnStep(World)` does not.
8. **Core's "no device/window/imaging library" rule** — *decide before phase 2.* It only needs to
   hold for `Kiln.Core`. OpenTS3's decoders use imaging, and those are game assemblies.

## 10. Known gaps in the spec

- ~~**§2 enumerates three boundary crossings; a fourth, main→render carrying views, exists.**~~
  Obsolete 2026-09-23: camera state travels sim→render in the batch (spec §7).
- ~~**§9 routes input to the simulation yet names `camera.pan` as its example.**~~ Resolved
  2026-09-23: both are true. The camera consumes `camera.pan` on the tick (spec §9).
- ~~**`GameContext` has no hook for a view provider.**~~ Obsolete: no render-side view provider
  is needed for the camera.
- ~~**No in-universe clock anywhere; §1's "can be scaled" implies the engine owns it.**~~ Resolved
  2026-09-23: spec §1 now limits host control to app-level use, and game time is the game's.
- **First-game requirements Part I does not specify.** These are recorded in the plan's register
  and listed at the top of spec Part II: KR-10, KR-18, KR-19, KR-20, KR-22, KR-24.

## 11. Repo issues noted in passing

Cross-referenced to the migration plan's register.

- ~~`Kiln.Core/Simulation/ISystem.cs` — `internal`, must be `public`.~~ Fixed.
- ~~`Kiln.Core/Simulation/System.cs` — does not compile; delete.~~ Deleted.
- `Kiln.Render.OpenGL.csproj` — no `ProjectReference` to `Kiln.Render`, so it cannot implement
  `IRenderDevice` (KR-17). It is also missing the layering comment header the other three
  projects carry.
- `Kiln.Core/Simulation/Simulation.cs` — `world` field unused, non-nullable, never assigned
  (CS8618 + CS0169). Lands with KR-01/KR-02.
- `Store<T>.Remove` is `public`, so it bypasses the presence mask and the change feed (KR-09).
- **R1 (no Sims 3 concepts in Kiln code, names or comments):** `World.CreateGroup`'s doc comment
  names lots and imposters, and `World.Move`'s says "a sim crossing a lot boundary".
