# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Kiln is a general-purpose C#/.NET game engine built on Silk.NET. It is early: most of the tree is
scaffolding whose *shape* is fixed by a specification, with bodies still to land. The first game
built on it (a Sims-3-class reimplementation, `OpenTS3.*`) lives in a separate repo and is never
referenced from here.

OpenTS3 consumes Kiln as a **sibling checkout**, through `ProjectReference`s rooted at
`$(KilnPath)`, pinned to a Kiln commit by a `kiln.ref` file in the OpenTS3 repo. Project names and
paths here are therefore an interface: renaming or moving a `Kiln.*` project breaks the game
build.

**No Sims 3 concept in Kiln.** Kiln code, names and comments never mention a lot, a Sim, a TGI,
DBPF, OBJD, or a household. Name the generic concept, and let the game supply the Sims 3 meaning:
a lot is an `EntityGroup`.

## Commands

```powershell
dotnet build Kiln.slnx          # build (net10.0)
./Utils/guard-layering.ps1      # layering guard - run before committing
```

CI (`.github/workflows/layering-guard.yml`) runs the guard, then the build, on push to `main`
and on every PR. There are no test projects yet; do not invent a `dotnet test` invocation.

The guard currently fails on `main`: rule 2 below rejects the Silk.NET imports in `Kiln.Runtime`.
Rule 2 is over-broad and due to be reworked. Treat those failures as known; do not remove
legitimate Silk imports to satisfy it.

`Utils/guard-layering.ps1` must stay **ASCII-only** — it is invoked through Windows PowerShell 5.1,
which reads the file as ANSI, and an em dash or a section sign is a parse error, not a warning.

## The specification

`docs-local/ENGINE_FROM_SCRATCH.md` is the authoritative design document and is gitignored
(local-only, not shared). Read it before changing subsystem shape. Source XML docs cite it by
section number (`§4`, `TODO(§6)`); those references are load-bearing — when you add a type,
say which section it implements. Section map: §1 time/frame, §2 threads, §3 resources,
§4 world/entities, §5 transforms, §6 extraction, §7 views/frame packet, §8 render backend,
§9 input, §10 scripting host, §11 diagnostics, §12 layering.

`docs-local/KILN_MIGRATION_PLAN.md` (also local-only) **sets the order of work.** It is the plan
for moving OpenTS3 onto Kiln, and its §6 is the Kiln requirements register (`KR-xx`): the engine
capabilities the game needs, each with the migration phase it gates. The build order in
`ENGINE_FROM_SCRATCH.md` Part II is superseded by it. `docs-local/DESIGN_SESSION_NOTES.md` records
design decisions and the open ones, with the plan's deadline for each.

The Kiln track, in order:

1. §1 clock (KR-01) and the `ISystem` signature (KR-02);
2. §5 transform hierarchy bodies (KR-06);
3. host loop and `IGame` contracts (KR-03), then the simulation thread, the intent buffer and
   host control (KR-04);
4. §3 resource identity and provider, *ahead of* handles (KR-12), plus a worker pool (KR-14);
5. the rest of §4 (KR-07 to KR-11);
6. §6/§7/§8 and handles;
7. input and UI.

§4 has largely landed. When game work needs an engine capability, it becomes a KR entry and the
game waits. So when working here, build the KR, not a game-specific shortcut. Mention the KR id
in commit messages and in the scaffold notes of the types that implement it.

## Layering — the one hard rule

```
Tools ──▶ Game.* ──▶ Kiln.Runtime ──▶ Kiln.Render ──▶ Kiln.Core
                                                          ▲
Kiln.Render.<api> ────────────────────────────────────────┘
```

| Project | Owns | Must not have |
|---|---|---|
| `Kiln.Core` | entities, stores, transforms, time, resource identity, math | any device, window, or imaging dependency; an OS-specific TFM |
| `Kiln.Render` | retained render scene, extraction, views, passes, `IRenderDevice` | any graphics-API call |
| `Kiln.Render.<api>` | one project per backend — the **only** place a GL/Vulkan/D3D symbol may appear | — |
| `Kiln.Runtime` | window, device creation, backend selection, input capture, threads, channels, frame loop, `IGame` | a graphics-API dependency |

The guard enforces four rules mechanically: (1) a `Kiln.*` project references only `Kiln.*`
projects; (2) no source file in a `Kiln.*` project imports a namespace rooted outside `System`,
`Microsoft`, or `Kiln`. It is meant to catch game code leaking in via linked files or global
usings, but as written it also rejects legitimate library imports (see Commands). The intended
constraint is that `Kiln.Core` takes no device, window or imaging dependency, and that nothing
imports game code;
(3) device/window/imaging packages appear only in `Kiln.Runtime` and `Kiln.Render.*`;
(4) `Kiln.Core`'s target framework carries no OS suffix, whether declared locally or inherited
from `Directory.Build.props`.

`Kiln.Core`'s and `Kiln.Render`'s csproj `ItemGroup`s are deliberately empty and carry comments
saying so. Do not add a package there to make something compile — solve it at the right layer.

## Threading model

`Kiln.Core` and `Kiln.Render` are **single-threaded libraries**. Nothing in them starts a thread
or takes a lock. That is what lets a headless tool or a test step the same simulation
synchronously on the calling thread. `Kiln.Runtime` is the only project that knows concurrency
exists; it owns the three threads (main / simulation / render) and the channels between them.
When writing in Core or Render, assume exclusive single-threaded access and say so in the doc
comment if it matters.

The simulation→render payload is a batch of **deltas**, never a scene description: it describes
what changed since the last batch, not what the scene is.

## Time: engine tick vs game time (§1)

The engine tick is a fixed step that runs every registered system, every step. In-universe time,
speed settings (1/2/3), pause, and whether an entity "thinks" are **game** state. In the first game
they belong to its script host (§10), which runs every tick and decides for itself whether guest
time advances. A paused game still ticks: build/buy, the camera, and other systems keep running.
Game systems pick their clock explicitly. They read `StepTime`, or a game-owned clock record in
the World if they must freeze with the game.

The host-control block (`SetTimeScale`, suspend, shutdown) is **app-level only**: debug
slow-motion, a debugger break, possibly a minimised window. It is read at a step boundary, and it
never carries a game's pause or speed. Suspending it stops every system, which is exactly why a
game's pause must not use it. Never add a game-speed or game-pause factor to the engine loop.

Input crosses to the simulation as the **intent buffer**: game-defined payloads, per tick,
serializable (it is the replay record), living in `Kiln.Core`. Host control is separate from it.

The camera lives on the logic side, on a "Player" entity with a game-supplied `PlayerController`
and an engine-shipped `Camera` component. The controller consumes intents on the tick and moves
the camera in the world. Camera state is published to the renderer and interpolated there. It is
not a render-side controller. (The migration plan still describes the older model; it is being
corrected from the plan's own session.)

## World model conventions (§4)

- `EntityId(Index, Generation)` — generations start at 1, so `default(EntityId)` is never live.
  Destroy bumps the generation; a stale id fails `IsAlive` rather than addressing its successor.
- Storage is a dense sparse-set per component type (`Store<T>`). Removal is swap-with-last, so
  **nothing may hold a dense index across a step** — hold an `EntityId`.
- Component add/remove goes through `World`, so the presence mask and the change feed stay
  consistent. `Store<T>.Add` is `internal`. `Store<T>.Remove` is currently `public` because
  `IStore` exposes it, which is known drift (KR-09). Do not call it outside `World`. `Store<T>.Ref` is public because mutating
  in place changes no membership — but the world cannot observe that write, so a system that
  mutates and wants extraction to notice calls `World.Touch<T>`.
- The component registry is **open**. The engine ships component types for facilities most games
  use (`Transform`, `Renderable`, `Camera`, and more as facilities land); there is no cap. A type
  belongs in the engine when the engine consumes it or most games would each write the same one.
  Engine types get no privileges: a game registers everything else through the same API and gets
  the same storage, feed, and serializer hook.
- `EntityGroup` is a membership/persistence set, not a spatial partition and not a scene-graph
  node. Its two axes are orthogonal: `Loaded` is streaming (simulation), `Detail` is rendering
  only. The engine never decides *when* to flip detail; that is the game's camera policy.
- The change feed accumulates across a whole publish cycle (which may span several fixed steps)
  and is cleared only by its owning consumer via `Drain`.

## Code style

- `Directory.Build.props` sets `net10.0`, implicit usings, nullable, and unsafe blocks for every
  project. Project files keep those properties commented out as a reminder of where they come
  from — leave them that way rather than re-declaring.
- File-scoped namespaces; `using` directives are usually unnecessary given implicit usings.
- XML doc comments carry the *reasoning*, not a restatement of the signature: why an id is
  structured the way it is, what invariant a caller must not break, which spec section owns the
  unimplemented body. Match that density.
- Scaffold types state that they are scaffolds and what is fixed about them versus what lands
  later (`/* §5 */` bodies, `TODO(§6)` notes). Keep that honesty when adding placeholders.
