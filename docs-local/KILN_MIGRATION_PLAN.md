# Kiln Migration Plan

Last updated: 2026-09-23 (first version; revised the same day for Kiln's camera decision and the
dropped two-component rule)

Sources:

- OpenTS3: `CODE_ORGANISATION_AUDIT.md` (2026-09-22, `JJs-Build-V02` at `517f960`).
- Kiln: the Kiln status extract of 2026-09-23 and the Kiln design-session notes of 2026-09-06.
- Target: `ENGINE_FROM_SCRATCH.md` Part I.

Where the Kiln notes and Part I disagree, the notes win. Their renames and design changes are
listed in §5.

This plan moves OpenTS3 from its current shape to a game built on Kiln, as `ENGINE_FROM_SCRATCH.md`
§12 describes. It covers both tracks, because they depend on each other:

- **Kiln track:** the engine, developed in its own repository. OpenTS3 consumes it as a sibling
  checkout.
- **OpenTS3 track:** this repository, the first game.

It is written for contributors and agents on either track. Each phase has a gate. A phase is done
only when its gate is met, measured by the means the gate names.

---

## 1. Rules

These hold for the whole migration and override convenience in any single phase.

**R1. One direction of dependency.**

- Kiln never references OpenTS3.
- Kiln code, names and comments carry no Sims 3 concept: no lot, Sim, TGI, DBPF, OBJD or
  household.
- A concept the engine needs is named generically; the game supplies the Sims 3 meaning. A lot
  is an `EntityGroup`, for example.

**R2. Missing engine capability goes to Kiln.**

- A game subsystem sometimes needs something Kiln does not have or does not plan: a pass type,
  a skinning path, a worker pool.
- That need becomes an entry in the Kiln requirements register (§6), and the game work that
  depends on it waits for it.
- OpenTS3 does not write a stand-in for an engine capability, even a temporary one.
- The old path keeps running in the meantime (R4). That is the only stopgap.

**R3. Move knowledge, rebuild shape.**

- Keep and move: format decoders, package resolution, EA host bridges, animation, locomotion and
  routing maths, and the native-address evidence behind them. They are the hard-won part.
- Rebuild against Kiln: the loop, the composition, the world state and the renderer. Those are
  the parts with the wrong shape.

**R4. Strangle, do not switch.**

- The current viewer path stays runnable until the new path passes the same gate.
- Every feature crosses over behind a parity check. That check is the existing fixture probes
  and capture-and-diff harness.
- Old code is deleted in the same change that proves its replacement.

**R5. Freeze the wrong shape now.** From phase 0 on:

- no new `Program` partials;
- no new `MeshViewerApp` partials;
- no new `OPENTS3_*` flags;
- no new static mutable fields;
- no new files in the bare `OpenTS3.Core` namespace;
- no new render-side reads of `OpenTS3RuntimeProxyRegistry` or `OpenTS3RuntimeQueries`.

A ratchet in preflight enforces this (§4, phase 0).

**R6. The game owns game time** (decision D1, §5).

- Kiln's step runs at a constant rate and never stops because the Sims are paused.
- Sims time, speed tiers and pause belong to the script host.

---

## 2. Where both tracks start

### OpenTS3 (`JJs-Build-V02`, `517f960`)

| Area | State |
|---|---|
| Size | 1,262 C# files, 564k lines, 9 projects |
| Data layer | ~65 loaders, package index + precedence + persistent cache, TXTC / preset compositor: complete and evidence-backed |
| Script host | EA assemblies load; 105 host bridges; task scheduler with fibers; simulation clock publication; `SimulationTickHooks` |
| World state | `RuntimeCreatedObject` (engine-owned, revisioned snapshots); residents are frozen records plus a live-pose overlay; one write path (`RuntimeObjectPose`); all static |
| Loop | the gameplay slice ticks on its own thread with a wall-clock step; the other views tick from the window `Update` |
| Renderer | `MeshViewerApp`: 54 partials, 31k lines; GL in 60 files; 56 shader literals |
| Organisation | `Program` 144 partials; 686 files in the flat Core namespace; 252 env flags; 328 static fields; 132 probe files in shipping assemblies; no test project |
| Verdicts | logic/render: separated by convention; world runtime: mutable, unticked |

### Kiln (2026-09-23)

| Spec § | State |
|---|---|
| §12 Assembly split | **Done.** `Kiln.Core`, `Kiln.Render`, `Kiln.Render.OpenGL`, `Kiln.Runtime`, references in the right direction. `Kiln.Render.OpenGL` does not yet reference `Kiln.Render`. |
| §1 Clock, fixed step | **Not started.** `Time/` is empty. This is the front of Kiln work. |
| §4 Entities, groups | **Mostly landed:** `EntityId` with generations, sparse-set `Store<T>`, open registry with presence mask, `Entity` façade, `IEntityDefinition`, change feed drained by its consumer, `World.Touch<T>`, groups with `CreateGroup` / `Move` / `SetDetail`. **Missing:** built-in `Transform` / `Renderable`, `SetDetail` publishing, `IGroupProxy`, `SetLoaded` (deferred on purpose). |
| §5 Transform hierarchy | Scaffold only; `World` already calls into it. |
| §3 Resources | Placeholders; no `ResourceId`, no handles. |
| §2 Threads, channels | Empty classes. |
| §6–§8 Extraction, views, backend | Not started. |
| §9 Input | `InputManager` wired to the window with empty handlers. |
| §11 Diagnostics | Nothing yet. |
| Host | `KilnHost` opens a GL 4.5 window. No `Run` / `RunHeadless`; does not read `GameManifest` (fields private) or call `IGame`; `GameContext._world` is never assigned; `IGame` registry parameters are commented out. |

The two tracks meet at three points:

- the resource provider (§3);
- the world model (§4 and §5);
- the loop (§1, §2 and §12).

The renderer comes last, on both sides.

---

## 3. Target layout

### Kiln (its own repository, consumed as a sibling checkout)

```
Kiln.Core            ids, World, stores, groups, hierarchy, StepTime, intent buffer,
                     system + resource registries, ResourceId, provider/decoder contracts
Kiln.Render          render scene, views, frame packet, passes, device interface
Kiln.Render.OpenGL   the only place GL calls appear
Kiln.Runtime         KilnHost: window, backend acquisition, input devices, threads, loop
```

### OpenTS3 (this repository)

```
OpenTS3.Resources    DBPF provider: GameResourceKey ⇄ ResourceId, package index, precedence,
                     group rules, persistent package cache                       → IResourceProvider
OpenTS3.Formats      the loaders, as registered decoders                         → IResourceDecoder<T>
OpenTS3.Materials    preset / TXTC / CAS skin composition                        → ITextureComposer
OpenTS3.World        game components, entity definitions (OBJD/catalog), OBJN → lot groups,
                     spatial indexes, placement, footprints, slots, valuation
OpenTS3.Simulation   game systems: routing, locomotion, animation, physics, lot presentation
OpenTS3.ScriptHost   EA managed host: load context, host bridges, native exports, task
                     scheduler, in-universe clock (D1), EA object ⇄ EntityId lifecycle map
OpenTS3.Rendering    extraction inputs the engine cannot know: renderable definitions,
                     game pass / material registrations (KR-18), group proxies (imposters)
OpenTS3.UI           EA UI host (LAYO, CSS, HUD, pie menus) drawn through Kiln's UI path
OpenTS3.Game         OpenTS3Game : IGame — composes all of the above
OpenTS3.Tools        Main = KilnHost.Run(new OpenTS3Game(), args); CLI commands as ICliCommand
OpenTS3.Tests        fixture probes + a runner (from all current *FixtureProbe files)
OpenTS3.Launcher     unchanged; launches Tools with manifest options, not env flags
```

```
Tools ─▶ Game ─▶ UI, Rendering, ScriptHost, Simulation ─▶ World ─▶ Materials, Formats ─▶ Resources
                                                                                              │
   every OpenTS3 assembly ─────────────────────────────────────▶ Kiln.Core (+ Render, Runtime where needed)
```

`Rendering` and `UI` are the only game assemblies that reference `Kiln.Render`. `Tools` is the only
one that references `Kiln.Runtime`.

### Where today's code goes

| Today | Goes to | How |
|---|---|---|
| `Core/GameResourceKey`, `GamePackageIndex`, `GamePackagePrecedence`, `PersistentPackageCache`, `GamePackageMounts` | `OpenTS3.Resources` | move |
| `Core/*Loader`, `*Decoder`, `*Parser`, `Spt*`, `Sacs/`, `Symbols/` | `OpenTS3.Formats` | move, then wrap as decoders |
| `Core/PresetCompositor/`, `SurfaceTxtc*`, `CasSkinToneCompositor` | `OpenTS3.Materials` | move, then wrap as composers |
| `Core/WorldLot*`, `Runtime*Buy*`, `*Footprint*`, `*Slot*`, `*Valuation`, `ObjectCatalog*` | `OpenTS3.World` | move; state becomes components (phase 4) |
| `Core/Animation*`, `Locomotion*`, `*Routing*`, `RuntimeObjectPhysics` | `OpenTS3.Simulation` | move; ticking becomes `ISystem` |
| `Core/Managed*`, `OpenTS3RuntimeProxyRegistry*`, `OpenTS3RuntimeQueries`, `Engine/ScriptBridge/` | `OpenTS3.ScriptHost` | move; static state becomes instance state (phase 4) |
| `Core/RuntimeCreatedObject*`, `RuntimeObjectPose`, `ResidentLivePose` | `OpenTS3.World` components and systems | rebuild on `World` (phase 4) |
| `Core/*FixtureProbe*`, Engine and Launcher probes | `OpenTS3.Tests` | move |
| `Engine/Cli/`, `Program` partials | `OpenTS3.Tools` | convert to `ICliCommand`; delete dead probes |
| `Engine/Runtime/OriginalSleepSliceSession*` | `OpenTS3.ScriptHost` session + `OpenTS3.Game` | extract from `Program` (phase 1), then onto the Kiln loop (phase 3) |
| `Engine/MeshViewerApp*`, `BridgeWorldSceneRenderer`, loose GL at the Engine root | deleted | replaced by `Kiln.Render` + `OpenTS3.Rendering`, feature by feature (phase 5) |
| `Engine/UI/` | `OpenTS3.UI` | rebuild the drawing on Kiln's UI path (phase 6); keep the layout/state logic |
| `Engine/Animation`, `Routing`, `Audio` | `OpenTS3.Simulation` / KR-24 | move |
| `Engine/Runtime/Labs/`, `SpeedTreeLab` | `OpenTS3.Tools` apps on the Kiln host, or retired | decide per lab in phase 6 |
| `Engine/Runtime/Scene/SceneDescription.cs` (dead types) | deleted | phase 0 |
| `OpenTS3RuntimeLotManagerSeeder` | deleted | replaced by a projection of lot-group state (phase 4) |

---

## 4. Phases and gates

Phases 0–2 need little or nothing from Kiln and can start now. From phase 3 on, each phase waits on
the Kiln requirements it names. Sizes are relative (S < M < L < XL), not calendar estimates.

```
Kiln     ── §1 clock ── §5 hierarchy ── host loop ── §3 identity ─ ... ─ §6/§7/§8 + handles ── input/UI
              │            │               │             │                        │                │
OpenTS3  P0 ─ P1 ──────────┼───────────────┼─ P2 ◀──────┘                        │                │
                           │               └─▶ P3 ─▶ P4 ◀── (§4 rest, KR-06..11)  │                │
                           └──────────────────────────▶ P4                         └─▶ P5 ──────────┴─▶ P6 ─▶ P7
```

### Phase 0: Freeze and wire. Size S. Kiln needs: none.

**Work.**

1. **Ratchet.** Extend `tools/guard-layering.ps1`, or add a sibling `guard-ratchet.ps1`, to
   compare against a committed baseline. It should use the counts that
   `.claude/skills/arch-audit/scripts/collect-metrics.ps1` already produces:
   - `Program` partials;
   - `MeshViewerApp` partials;
   - distinct `OPENTS3_*` flags;
   - static mutable fields;
   - bare-namespace Core files;
   - viewer partials referencing the registry or queries.

   It fails preflight when any count rises. Counts may only go down; lower the baseline in the
   change that lowers the count.
2. **Linkage.**
   - Move `Directory.Build.props` from `tools/` to the repository root, so MSBuild imports it.
   - `KilnPath` defaults to a sibling `../kiln` checkout.
   - Add the Kiln projects to a solution filter so both open together.
3. **Layering guard.**
   - Fails if `KilnPath` does not resolve.
   - Fails if any Kiln project references an OpenTS3 project.
   - Fails if GL appears outside `Kiln.Render.OpenGL` in any *new* assembly. The legacy
     `OpenTS3.Engine` is allowlisted and shrinks under the ratchet.
4. **Test project.**
   - Create `OpenTS3.Tests` with a runner that calls the existing probes' `Run` entry points.
   - Tag each probe *offline* (no game install) or *installed*.
   - CI runs the offline set.
5. **Delete dead code.** Remove `SceneDescription`, `RenderNode`, `ISceneProvider` and
   `IInputSink` from `Engine/Runtime/Scene/SceneDescription.cs`. They have zero consumers.
6. **Record decisions.** D1–D3 (§5) go into this file, and the Kiln-side renames are written
   back into `ENGINE_FROM_SCRATCH.md` Part I. The latter is a Kiln task, done 2026-09-23 (KR-00).

**Gate G0.**

- Preflight fails on a deliberate +1 in each ratchet metric, and passes on the baseline.
- `dotnet build` resolves Kiln through `KilnPath`.
- `dotnet test OpenTS3.Tests` runs every offline probe green.
- Installed probes run with an install path and are green.

### Phase 1: Mechanical reorganisation. Size L. Kiln needs: none.

Moves and renames only. No behaviour changes, so every step is proven by the same probes and
captures before and after.

**Work.**

1. **Namespaces first, assemblies second.** Give every Core cluster a sub-namespace matching the
   target assembly: `OpenTS3.Resources`, `.Formats`, `.Materials`, `.World`, `.Simulation`,
   `.ScriptHost`. Then split the assemblies along those lines. The IDE's move-type refactors do
   most of it.
2. **Namespace fixes.**
   - `Engine/Cli/` gets a Cli namespace.
   - `OpenTS3.Runtime.*` inside Engine is renamed to `OpenTS3.Tools.Labs.*` and
     `OpenTS3.Rendering.Scene`. That ends the collision with trunk's assembly of the same name.
3. **Take `Program` apart.**
   - Each command becomes an `ICliCommand` in `OpenTS3.Tools`, grouped by area.
   - `OriginalSleepSliceSession` and its partials become a non-nested type in
     `OpenTS3.ScriptHost`. It keeps its thread for now.
   - The `Runtime*` helpers become ordinary types.
4. **Narrow references.**
   - `Bridge.Native` references `OpenTS3.ScriptHost`, not the whole Engine assembly.
   - `SpeedTreeLab` references only what it uses.
5. **Move the probes.** Probe files move into `OpenTS3.Tests`. Shipping assemblies keep only the
   seams the probes need, made `internal` with `InternalsVisibleTo`.
6. **Retire the old tick path.** `runtime-world-view`'s window-`Update` tick is retired in favour
   of the gameplay slice's session, or explicitly labelled a legacy viewer. That leaves one
   gameplay tick path, the simulation thread.

**Gate G1.**

- Bare `OpenTS3.Core` namespace: 0 files.
- Namespace/folder mismatches: 0.
- `Program` partials: 0.
- Shipping assemblies contain 0 `*FixtureProbe` types.
- A headless package dump links no `Silk.NET`, `Vortice` or `SixLabors` assembly (checked from
  its `deps.json`).
- Every probe and the reference capture set diff to zero against pre-phase captures.
- The audit's logic/render verdict is unchanged or better.

### Phase 2: The DBPF provider on Kiln's resource system. Size M. Kiln needs: KR-12, KR-14.

**Work.**

1. **`DbpfResourceProvider : IResourceProvider`.**
   - `ResourceId(Hi, Lo)` packs `(Type, Group, Instance)`. Only the provider unpacks it.
   - `TryLocate` uses `GamePackageIndex` with patch-over-base precedence, context-group
     propagation and group-zero fallback.
   - `PersistentPackageCache` becomes the provider's persisted location index, validated per
     archive, as §3 of the spec already describes.
   - `Open` returns the package stream.
   - `Dependencies` exposes the OBJD dependency DAG from `ARCHITECTUREV2.md`.
2. **Decoders.** Register the loaders as `IResourceDecoder<T>`, one per format the game owns.
   Loaders that assemble a whole world (`WorldSceneLoader`) are not decoders. They become
   phase-4 world loaders that *call* decoders.
3. **Composers.** Register `PresetCompositor`/TXTC and CAS skin composition as
   `ITextureComposer`, addressed by recipe hash. If Kiln's §8 composer contract is not yet
   available, this step waits (R2); the CPU compositor keeps running on the old path.
4. **The live cache.** The in-memory caches scattered through Core are package handles, decoded
   objects and composed textures (`SurfaceMaterialDependencyResolver` statics and similar).
   - The ones that cache *location* stay in the provider.
   - The ones that cache *decoded or composed results* are removed in favour of Kiln's
     content-keyed dedupe, once KR-13 lands.
   - Until then they stay where they are, unchanged. They are not re-implemented in the new
     assembly.

**Gate G2.**

- A headless Tools command resolves and decodes the reference resource set through Kiln's
  resource system: meshes, textures, OBJD, OBJN, terrain, one lot. It produces byte-identical
  hashes to the current loaders.
- The package-precedence fixtures are green through the provider.
- A second request for the same id enqueues no second decode, once KR-13 lands.
- Re-run the audit: `OpenTS3.Resources` has no reference to `OpenTS3.Engine`.

### Phase 3: Headless simulation on the Kiln loop. Size M. Kiln needs: KR-01 to KR-04.

**Work.**

1. **`OpenTS3Game : IGame`.** Declare its manifest as data, register systems, and compose through
   Kiln's `GameComposer` with the device switched off.
2. **`ScriptHostSystem : ISystem`,** registered first, runs every engine step. Each step it:
   - drains the intent buffer: GoHere, RequestMenu, PerformInteraction, LiveUi actions, Buy/Build
     tool actions;
   - advances the **in-universe clock** by its own rules (D1). The clock yields `gameDelta` from
     pause and speed tier, and publishes EA's `sSimTickCnt` through today's
     `ManagedSimulationClock`;
   - runs EA's task queue with `(stepDelta, gameDelta, allowGameplay)`, exactly as
     `FrameTaskQueue.Tick` does today;
   - runs the game-time hooks. Today's `SimulationTickHooks` become ordinary `ISystem`s that read
     `gameDelta` from a game-owned clock record in the World.
3. **Loop and commands.**
   - `OriginalSleepSliceSession`'s thread, `Stopwatch` delta and `Thread.Sleep(8)` are deleted.
     Kiln's simulation thread and fixed step replace them.
   - Its request methods (`RequestGoHere`, `RequestInteractionMenu`, ...) become intent writers.
   - Its published `SimSnapshot`, menu and lot presentation are read by the old viewer through
     the same accessors as before. The viewer remains the renderer until phase 5.
   - `InvokeLiveUi`'s blocking 10 s cross-thread call is replaced by an intent plus a published
     result.
4. **Headless checks.** The interaction-slice checks currently in `SimSleepViewCommand`
   (headless menu, perform, scripted walk) become `OpenTS3.Tests` cases. They advance by *step
   count*, not wall-clock sleeps.

**Gate G3.**

- The headless interaction slice runs under `KilnHost.RunHeadless`: walk to a clicked point,
  open a menu, perform Sleep. The run is deterministic: the same intents and the same step count
  give the same Sim position and posture across ten runs.
- With the game paused through the script host, EA UI tasks and Build/Buy intents still process.
  A test proves this (D1).
- There is no wall-clock read in any `ISystem` (audit wall-clock scan).
- The windowed interaction slice still runs, on the old viewer, fed from the Kiln simulation
  thread.
- Two gameplay tick paths → one.

### Phase 4: Game object state into the World. Size XL. Kiln needs: KR-06 to KR-11.

This is the phase that ends the frozen registry. It begins with a spike, because it carries the
project's largest open risk (§7, risk 1).

**Spike (entry condition).** Move one Sim and one bed of the interaction slice onto Kiln entities:

- `Transform` in the engine hierarchy.
- A `Script` component holding the EA proxy.
- EA's position getters and setters answered from `Transform`, as `RuntimeObjectPose` already
  does for created objects.

If EA's objects cannot be made projections of entities without breaking G3, stop. The finding
goes to Kiln as a requirement before the bulk move.

**Work.**

1. **Components** (registered by the game):

   | Component | Holds |
   |---|---|
   | `Location` | lot, level, room, tile |
   | `Footprint` | tile occupancy |
   | `Slots` | attachment points, via the Kiln hierarchy |
   | `ObjectVisual` | model key, geometry state, design material, hidden and visibility flags |
   | `Animated` | rig, clip state |
   | `Routing` / `Steering` | movement state |
   | `Physics` | rigid state |
   | `CasOutfit` | the outfit model |
   | `Script` | the EA proxy handle |
   | `Lifetime` | lifecycle state |
   | `PlayerController` | camera and tool control for the Player entity: consumes camera intents on the tick and moves the camera in the world, constrained by terrain, walls, lot bounds and the active mode (Live / Build / Buy), optionally following an entity |

   Their fields come from `RuntimeCreatedObject` and `WorldObjectRegistryEntry`, which already
   enumerate them. `PlayerController` is new game code in `OpenTS3.Simulation`.

   The **Player entity** carries `PlayerController`, `Transform` (KR-06) and Kiln's
   engine-shipped `Camera` component (KR-16). `Camera` is not a game component. The Player
   entity can be created in this phase with `PlayerController` and `Transform`. `Camera` attaches
   when KR-16 lands, and must be in place before phase 5's first crossover. Until then the old
   viewer keeps its own camera (R4). The Player entity's shape is still open (§5).
2. **Definitions.** Catalog products and OBJD resolve through the resource system to entity
   definitions (KR-11). A created object is `World.CreateEntity` plus its definition.
3. **Load.**
   - OBJN rows become entities at load: one group per lot, plus a global group for world
     terrain, roads and lot-less objects.
   - A Sim changing lots is `World.Move`.
   - Lot detail policy (camera distance, active lot) calls `SetDetail`.
   - `LotPresentationStateBridge` becomes the game's policy system; the engine executes the flip.
4. **Script host lifecycle.** `OpenTS3RuntimeProxyRegistry` becomes the script host's EA-object ⇄
   `EntityId` map, minted on create and dropped on destroy (spec §10), as instance state.
   - `OpenTS3RuntimeQueries` splits into the systems' own spatial indexes (placement, routing,
     room lookup), built from stores.
   - `ResidentLivePose` and the second static registry copy are deleted.
   - `OpenTS3RuntimeLotManagerSeeder` is deleted. `LotManager` answers from lot-group state.
5. **Persistence.** Game components register serializers (KR-10), and saves carry them.
   - EA's own object graph still persists through EA's `LoadSaveManager`, bound to `Script`
     components.
   - A moved resident and a bought object now survive a save/load round trip, which they cannot
     today.

**Gate G4.**

- Static world-state fields: 0 (ratchet).
- Readers of `WorldObjectRegistryEntry` outside the OBJN loader: 0.
- Every position write goes through `Transform`.
- `OpenTS3RuntimeLotManagerSeeder`: deleted.
- The reflection-write count in `ScriptHost` falls below the phase-3 baseline.
- Save/load round trip of a moved resident, a bought object and a placed household: green.
- G3's determinism test still green.
- Audit world-runtime verdict: **Mutable, ticked**.

### Phase 5: Extraction and rendering on Kiln. Size XL. Kiln needs: KR-05, KR-07, KR-13, KR-15 to KR-20, KR-23.

Kiln owns the renderer. OpenTS3 supplies what an engine cannot know:

- renderable definitions;
- mesh and material decoders into `MeshData` and material templates;
- texture composers;
- the lot imposter as `IGroupProxy`;
- the Player entity's `Camera` state, from which the renderer derives the main view (KR-16);
- registrations for the game's own passes and material types (KR-18).

The Player entity's `Camera` (phase 4) must be in place before feature 1 crosses over. The main
camera is scene information: extraction carries it in the scene delta batch, and the renderer
interpolates it with the batch alpha. The renderer never decides where the camera is.

**Work: feature by feature, in this order.** Each feature crosses over when it reaches parity. At
that point its `MeshViewerApp` partials are deleted in the same change.

1. Static world geometry: terrain, roads, lot build (walls, floors, roofs, stairs) on the global
   and lot groups.
2. Objects: residents and created objects. This replaces `RuntimeObjectGpuProjection`.
3. Lot detail and imposters through `SetDetail` and `IGroupProxy`.
4. Sims: CAS materials and skinning (KR-19, KR-20).
5. Lighting, sky, fog and the exterior light probe.
6. Water: pool, standing, sea (KR-18).
7. Flora and SpeedTree: cards, RT4 wind (KR-18).
8. Shadows (Kiln cascades).
9. Mirrors and wall cutaway (KR-18).
10. Portraits and thumbnails as offscreen views (KR-16).
11. Picking through render→sim feedback (KR-05). This replaces the static pick hooks and
    `TryIntersectWorldTerrain` calls from the script host. Terrain raycasts for placement become
    a game-side query on terrain data, not a renderer call.

`BridgeWorldSceneRenderer` is deleted when feature 1 lands.

**Gate G5, per feature.**

- A capture-and-diff of the Kiln render against the reference fixture set, within the tolerance
  the existing harness uses for that feature.
- Frame-time p95 no worse than the old viewer on the same fixture.
- The feature's partials deleted.

**Gate G5, final.**

- `MeshViewerApp`: deleted.
- GL calls outside `Kiln.Render.OpenGL`: 0.
- Shader string literals in OpenTS3: 0. Game shaders ship as files registered through KR-18.
- Audit logic/render verdict: **Separated by thread**.

### Phase 6: Input, UI, tools, launcher. Size L. Kiln needs: KR-21, KR-22, KR-24.

**Work.**

1. **Input.** Device state and bindings stay on the main thread, in Kiln's host. Actions,
   including camera actions, cross to the simulation as intents in the intent buffer (KR-04). They
   are consumed on the tick, the same way Build/Buy tool actions are. The six static callbacks on
   `MeshViewerApp` are already gone with it.
2. **UI.**
   - `UIHostBridge`'s render snapshot draws through Kiln's UI path: HUD, pie menu, live UI, CAS
     editor, Build/Buy overlays.
   - Layout and state logic stay game code.
3. **Audio.** Audio moves onto the device Kiln's host owns (KR-24).
4. **Tools and labs.**
   - `OpenTS3.Tools` is one line of `Main` plus `ICliCommand`s.
   - Labs either become Tools apps on the Kiln host or are retired, decided per lab.
5. **Launcher and flags.**
   - The launcher passes manifest options.
   - Env flags are reduced to a documented allowlist of diagnostics.

**Gate G6.**

- `Main` is `KilnHost.Run(new OpenTS3Game(), args)`.
- Every launcher mode works through manifest options.
- `OPENTS3_*` flags ≤ the allowlist.
- No game assembly references a windowing, input or audio library.

### Phase 7: Remove the legacy assembly. Size S.

**Work.** Delete `OpenTS3.Engine`, drop its allowlist entries from the guards, and re-run the audit.

**Gate G7.** The audit scorecard reads:

- Project boundaries: Good.
- God types: none.
- Test separation: Present.
- Logic/render: Separated by thread.
- World runtime: Mutable, ticked.

---

## 5. Decisions

### D1. Game time is a script-host concern (decided 2026-09-23)

TS3 has two clocks, and they are not the engine's two clocks.

| Clock | Owner | Advances when | Read by |
|---|---|---|---|
| Kiln `StepTime` | Kiln simulation loop | every fixed step, always, while the game runs | every `ISystem`: camera (`PlayerController`), Build/Buy, placement preview, UI tasks, lot presentation policy |
| Kiln `RealTime` | Kiln host | wall clock | UI transitions, render interpolation |
| **In-universe (Sims) time** | `OpenTS3.ScriptHost` | only while gameplay runs; scaled by speed tier; zero while paused or in Build/Buy | EA tasks, `SimClock`, routing and locomotion, Sim animation |

Consequences:

- **Pausing the Sims is not pausing the game.** Kiln's host-control pause stops the whole
  simulation loop. It is for app-level states only: a debugger break, shutdown, possibly a
  minimised window. OpenTS3's pause button is a game intent handled by the script host.
- **Speed tiers never change Kiln's tick rate or `SetTimeScale`.** `SetTimeScale` stays a generic
  engine control (debug slow-motion) that OpenTS3 does not use for gameplay.
- **Game systems choose their clock explicitly.** A system that should freeze with the Sims reads
  `gameDelta` from the script host's published clock record. Anything else reads `StepTime`.
  - Routing, locomotion and Sim animation read `gameDelta`, as `SimulationTickHooks` effectively
    does today.
  - Placement ghosts, UI and the camera read `StepTime`. Because the Sims' pause never stops the
    engine tick, the camera keeps working while the Sims are paused.
- **Kiln's open question "which layer TS3's game speed scales"** is answered: the game layer.
  Kiln only has to make it possible for a game to hold its own clock. The world holds game-owned
  state records, and systems read them. That is already in scope.

### D2. Linkage (decided 2026-09-23)

- Kiln is a sibling checkout. OpenTS3 projects use `ProjectReference` through `$(KilnPath)`, set
  in a root `Directory.Build.props`.
- CI checks out Kiln at the commit pinned in a `kiln.ref` file in this repository. Bumping the pin
  is an ordinary reviewed change.

### D3. Kiln requirements live in this plan (decided 2026-09-23)

- Entries are added to §6 by whoever finds the need, with the phase it gates.
- Moving an entry into Kiln's own backlog is a manual step. Mark the entry *filed* with a link when
  that happens.

### Open Kiln decisions this plan depends on

The decision stays with Kiln. What OpenTS3 needs is recorded here so Kiln can decide with it.

| Kiln question | Decide by | What OpenTS3 needs |
|---|---|---|
| `ISystem` signature | before phase 3 | a step-time parameter; the game adds its own clock on top (D1); registration order = step order |
| `IEntityDefinition` vs data-only `EntityPrefab` | before phase 4 | definitions resolved through the resource system from game data (OBJD / catalog), with per-instance overrides (design material, pattern preset, outfit). Either shape works if both hold. |
| `Instantiate(World)` vs `(Entity)` | before phase 4 | definitions that create attached children (slotted parts, CAS accessories) need hierarchy access, so `World` or an equivalent |
| Backend acquisition | before phase 5 | a headless composition with no backend loaded (phase 3 relies on it); otherwise indifferent |
| Camera as an entity | **decided 2026-09-23** | The camera is a game system that processes input on the engine tick, not a render-side controller. It lives on a "Player" entity with a game `PlayerController` and Kiln's engine-shipped `Camera` component (KR-16). It moves in the world, constrained by terrain, walls, lot bounds and the active mode, and can follow an entity. Camera input arrives as intents (KR-04). Its state reaches the renderer as batch-level prev/curr data on the scene delta batch, not as a per-entity delta, and is interpolated with the batch alpha. Accepted cost: up to one fixed step of input latency, so the fixed step must be short enough for that. Picking is unchanged (KR-05). Portraits, thumbnails, shadows, mirrors and picture-in-picture are offscreen views with their own non-main cameras (KR-16). |
| Generic controller type in Kiln | before phase 4 | Whether Kiln ships a generic player-controller component. OpenTS3 needs nothing from it: `PlayerController` is game code in `OpenTS3.Simulation` either way. If Kiln ships one, OpenTS3's type builds on it or replaces it. |
| Shape of the Player entity | before phase 4 | Open on both sides. One entity or several; whether it holds household or selection state; whether the camera attaches to the transform hierarchy for follow-cam. OpenTS3 needs follow-cam on a Sim and mode-dependent constraints; either shape works if both hold. |
| Core's "no device/window/imaging library" rule | before phase 2 | OpenTS3 decoders use imaging (`System.Drawing` in 11 Core files, ImageSharp). Those are game assemblies; the rule only needs to hold for `Kiln.Core`. |

---

## 6. Kiln requirements register

Status is Kiln's state as of 2026-09-23. **In spec** marks whether Part I (plus the Kiln notes)
already calls for it. Items marked *no* are new asks created by OpenTS3's needs (R2).

| KR | Capability | Spec | In spec | Kiln status | Gates | Needed because |
|---|---|---|---|---|---|---|
| KR-00 | Write the design-notes renames back into Part I: `StepTime`, `SetTimeScale` as an app-level host control, intent buffer, host-control block, `IInputSink` withdrawal. The view provider was withdrawn rather than written back. | all | — | **done 2026-09-23** | P0 | Both tracks must build against one text |
| KR-01 | `StepTime` / `RealTime`, fixed-step accumulator, time-scale control in `Simulation` | §1 | yes | not started | P3 | Every tick in the game |
| KR-02 | Final `ISystem` signature with `StepTime`; system registry in Core; order = registration | §10, §12 | yes | partial (`Initialise` + `OnStep(World)`) | P3 | Script host, routing, locomotion as systems |
| KR-03 | `KilnHost.Run` / `RunHeadless`; real `IGame` contracts (registries, readable `GameManifest`, `GameContext.World` assigned); one `GameComposer` with device switch | §12 | yes | scaffold | P3 | Headless gameplay tests; one composition |
| KR-04 | Simulation thread; intent buffer with game-defined payloads; host-control block | §2 | yes | empty classes | P3 | Replaces the slice's hand-made thread and mailboxes |
| KR-05 | Sim→render delta channel with back-pressure; render→sim feedback channel (picking hits, visibility) | §2 | yes | empty | P5 | Picking, and removing render-thread reads of sim state |
| KR-06 | Transform hierarchy bodies, built-in `Transform` component, `Static` flag | §5 | yes | scaffold | P4 | Every position; slots and attachment |
| KR-07 | Change feed safe for a second thread (double-buffered); decision on `Touch` vs dirty flags for hot components | §4, §6 | yes (deferred) | deferred | P5 | Extraction runs on the render side |
| KR-08 | More than 64 component types | §4 | yes | capped at 64 (TODO) | P4 (watch) | The game starts with ~12 components; not blocking unless it grows past 64 |
| KR-09 | Close `Store<T>.Remove` bypass of presence mask and change feed | §4 | yes | known drift | P4 | Game correctness depends on the feed |
| KR-10 | Component serializer hook and a per-group save record **without** `SetLoaded` | §4 | partly (serializer yes, save without unload no) | not started | P4 | Saves must persist moved residents and created objects; the game never unloads groups |
| KR-11 | Entity definitions resolved through the resource system; per-instance overrides | §4 | yes | interface only; shape undecided | P4 | Catalog products and OBJD become definitions |
| KR-12 | `ResourceId`, `IResourceProvider`, decoder registration and the resource registry in Core, **ahead of** handles and residency | §3 | yes, but ordered after §10 | placeholders | P2 | The DBPF provider can land early; handles can wait. Asks Kiln to split §3 into *identity and provider* (early) and *handles and residency* (late) |
| KR-13 | Content-keyed handles with generations; dedupe; residency; budgeted upload; fallback | §3 | yes | not started | P5 (P2 dedupe) | Replaces the game's ad-hoc decoded-result caches |
| KR-14 | Worker pool / job API for decode and sim-forked jobs | §2 | mentioned, no API | not started | P2 | The game decodes in parallel today with ad-hoc `Parallel.For` and tasks |
| KR-15 | Extraction, render scene with prev/curr interpolation; `SetDetail` publishing; `IGroupProxy` | §4, §6 | yes | not started | P5 | Lot detail and imposters; moving objects without re-upload |
| KR-16 | Views, frame packet, sort keys; offscreen views to render targets, each with its own non-main camera; the engine-shipped **`Camera` component** and its publication: main-camera state as batch-level prev/curr data on the scene delta batch, interpolated with the batch alpha, from which the renderer derives the main view | §4, §7 | yes: Kiln dropped the two-component rule on 2026-09-23 (the engine ships component types it consumes itself or that most games would each write) | not started | P5 (`Camera` before feature 1) | Main camera on the Player entity; shadows, mirrors, portraits, thumbnails, picture-in-picture, CAS preview |
| KR-17 | Mesh/material split, templates and instances, `ITextureComposer`, device interface, `Kiln.Render.OpenGL` referencing `Kiln.Render`, shaders from files | §8 | yes | not started | P5 (P2 composer contract) | All game materials |
| KR-18 | **Game-registered passes, shader programs and material types**, with ordering relative to engine passes | §8 | **no**: the spec lists a minimum pass set and says passes are data, but not that a game adds its own | not planned | P5 | Water, sky, SpeedTree wind, terrain blending, planar mirrors, wall cutaway, retail light probe |
| KR-19 | **Skinned renderables**: per-proxy bone palette (or equivalent) from an `Animated`-style component | §4, §6 | **no**: the spec keeps bones in a game component, with no render path for them | not planned | P5 | Sims and animated objects; today they are CPU-skinned on the render thread under a sim lock |
| KR-20 | **Per-proxy dynamic geometry**: CPU-deformed meshes updated per step without new handles | §3, §6 | **no**: `TransientMesh` exists; per-step updates of an entity's mesh are unspecified | not planned | P5 | CAS morphs, resident animation vertex ranges, placement ghosts |
| KR-21 | Input device state and bindings on the main thread; actions, camera actions included, delivered to the simulation as intents in the intent buffer and consumed on the tick (revised §9) | §9 | yes (revised) | handlers empty | P6 | Camera, tools, hotkeys |
| KR-22 | **UI rendering path**: 2D pass, text and fonts, textured quads, clipping, ordered draw | §7, §8 | **partly**: a UI pass and a sort-key rule exist; no 2D API | not planned | P6 | EA HUD, pie menus, CAS editor, Build/Buy UI |
| KR-23 | Diagnostics: logging factory, per-thread profiler, metrics, **frame capture / hash / diff** | §11 | yes | nothing | P3 logging; P5 capture | Parity gates G5 need Kiln-side capture |
| KR-24 | **Audio device ownership in the host** | none | **no**: the spec has no audio section | not planned | P6 | The game plays sound through XAudio2 today; under §12 the game may not own a device |

Things the game keeps and does **not** ask Kiln for:

- routing, locomotion and physics (game systems, as spec §4's illustration assigns them);
- the in-universe clock (D1);
- EA task fibers on threads the script host owns (spec §10 allows workers the host owns);
- terrain height queries (game data).

---

## 7. Risks

| # | Risk | Where it bites | Mitigation |
|---|---|---|---|
| 1 | EA objects cannot become projections of entities; EA code holds authority the World must defer to | phase 4 | Phase-4 entry spike on one Sim and one bed. `RuntimeObjectPose` already routes EA `SetPosition` into engine state, which is evidence it works. A negative result becomes a KR before bulk work. |
| 2 | Kiln's order (§1 → §5 → host → §3) leaves phase 2 idle | phase 2 | KR-12 asks for identity and provider ahead of handles. Phases 0 and 1 fill the wait either way. |
| 3 | Parity regressions during the renderer crossover | phase 5 | Per-feature capture gates. The old path stays until each gate passes. The fixture corpus is the project's strongest asset. |
| 4 | Growth continues in the old shape while migration runs | all | Ratchet (phase 0). The audit re-runs at every gate. |
| 5 | Two sources of truth during phase 4 (entities and the static registry) | phase 4 | Migrate per component with the registry reading *from* the World, never the reverse. Delete each registry field in the change that moves it. |
| 6 | Kiln decisions stall a phase | phases 3–5 | The open-decision table (§5) names the deadline and OpenTS3's requirement for each. |
| 7 | The EA UI and CAS editor depend on renderer internals more than the audit shows | phase 6 | Inventory `UI/` GL and `MeshViewerApp` references at the end of phase 5, before starting phase 6. |

---

## 8. Tracking

- **Audit at every gate.** Re-run `/arch-audit` at each gate. Gates G1, G4, G5 and G7 cite its
  metrics directly.
- **Ratchet baseline.** Its file is updated only by changes that lower a count.
- **Status line.** Each phase heading gets a status line when work starts: *not started /
  in progress / gated / done (date, commit)*.
- **Register updates.** KR statuses are updated from Kiln's side when an item lands. A KR that
  lands changes its gated phase from *waiting* to *startable*.
