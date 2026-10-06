# Nitrogenesis: build plan

A Windows desktop sandbox where small AI cars learn to drive a track by evolution, fully offline.
Racing (top-down) comes first. A platformer comes later as a second mode on the same core.

Written 2026-10-04 from the owner's decisions. Revised the same day after an independent three-lens review
(tech, design, completeness). **This file is the source of truth.** Every milestone is written so an
implementing model can follow it without re-deciding the design. If something here is wrong or missing,
stop and ask the owner. Do not improvise.

---

## 0. Owner decisions (fixed, do not re-open)

| Topic | Decision |
|---|---|
| Platform | **Windows only**, offline. Network only for the update check. |
| Modes | **Racing, top-down** first. Platformer later (M10) on the shared core. |
| Track | **Point A → point B** (start flag, finish zone). **No laps.** |
| Track style | **Pixel grid painted with brushes** of different sizes (Minecraft-like, 2D): road, grass, wall, danger. |
| Surfaces | Grass **slows** the car (not death). Walls **block** (solid). Danger cells **destroy** the car. |
| Timer | Each generation has a time limit. Cars not finished by then stop. A faster finish scores higher. |
| Learning | Neuroevolution (no LLM, no GPU). Fixed-size neural net. NEAT maybe later. |
| Speed | 0.01× … 100× via fixed-timestep sim; plus MAX and "train without visuals to generation N". |
| Tech | **Godot 4 .NET (C#)** app; simulation is pure C# with no Godot dependency. |
| Sound | Simple UI sounds. Engine sound only for the followed car and the player's car. |
| FPS | Cap 24 / 30 / 45 / 60 / 75 / 120 / unlimited / match monitor; **default 60**. |
| Resources | User can cap CPU use (threads, priority) and the memory used for history. |
| Release | Follows `~/coding_projects/APP-STANDARDS.md` (Setup.exe install/update, update banner, GitHub releases). |

Out of scope for v1: laps, online features, car-to-car collisions (cars are ghosts to each other),
champions library, deep RL (PPO etc.), Android.

---

## 1. How it works (short)

1. A **generation** has N cars (default 200). Each has its own **genome**: the weights of a small neural net.
2. All cars start at the start flag together. Every tick (1/60 game-second), each car does this:
   **sensors** → brain → **outputs** (throttle, steer) → physics moves it.
3. A car stops when it touches danger, makes no progress for the stall time, or the generation timer runs out.
   Walls block it; they do not kill it by default.
4. **Score**: progress toward the finish along the real drivable path (distance field, §3.2). Any car that
   finishes beats any car that did not, and faster finishers score higher.
5. **Next generation**: the best few are copied unchanged (elites). The rest are bred from good parents with
   small random mutations. Then the loop repeats.

---

## 2. Architecture

```
Nitrogenesis/
  VERSION               single version source, e.g. 0.1.0 (build writes it everywhere, fails on mismatch)
  src/Sim/              pure C# class library (net10.0), NO Godot references; all logic lives here
    Core/               IAgentMode, GenerationRunner, Simulation scheduler, SimConfig (+hash), SimVersion
    Map/                Grid, CellType, Clearance, DistanceField (pluggable neighbour fn), TrackHash
    Racing/             RacingMode : IAgentMode, CarPhysics, Sensors, RacingFitness, RacingSettings,
                        ReferenceDriver (finishability + estimated time)
    Brain/              Mlp (flat float[]), FastMath (lookup-table tanh/sin/cos)
    Evolution/          Genome, Population, Selection, Mutation, Crossover, Stagnation
    History/            GenerationRecord, Recording (trajectory), HistoryStore (memory-capped), Branches
    Gen/                TrackGenerator + templates
    Io/                 TrackFile, SessionFolder, SettingsFile (formats in §5.1)
    Rng/                xoshiro128** with SplitMix64 seeding
  tests/Sim.Tests/      xUnit; headless on Linux: `dotnet test`
  tools/SimBench/       console: `bench`, `train --track X --gens N`, `learn-suite` (§9)
  tracks/               bundled hand-made + fixed-seed generated .track files
  app/                  Godot 4 .NET project (rendering, UI, editor, input, audio); references src/Sim
  installer/            Setup.exe (Lockdown pattern, built via Wine)
  build/                versions.txt (pinned tool versions), export + installer + self-test scripts
```

### 2.1 Rules
- **All game logic lives in `src/Sim`**, testable without Godot. The app only draws, takes input and calls Sim.
  This is what lets a cheaper model implement safely: every Sim change has a test.
- **Mode interface** (`IAgentMode`): agent state arrays, `Reset(agentRange)`, `Step(agentRange, ticks)`,
  `Sensors`, `Score`, `IsDone`, plus a snapshot writer. Evolution, history, the speed controller, replay,
  recording, the HUD and the editor shell are generic. Racing implements it in M1; the platformer in M10.
- **Determinism (bit-exact on Linux and Windows, across app updates within one SimVersion):**
  - Fixed 60 Hz tick. No `MathF`/`Math` transcendental functions in the tick path: use `FastMath`
    (lookup-table tanh, sin/cos table; heading kept as an angle index or a unit vector). Scalar loops with a
    fixed summation order. No `Vector<T>`, no FMA.
  - Each child gets its own RNG stream, `seed = hash(generationSeed, childIndex)`. Sorting, averages and
    selection run single-threaded after the parallel step.
  - `SimVersion` (an int constant) goes up whenever physics/sensor/brain math changes. It is saved with every
    session and recording.
  - A test checks a trajectory hash against a constant. It runs in `dotnet test` (Linux) and in the packaged
    app's `--selftest` under Wine (Windows runtime).
- **Recordings:** each generation's best car is **recorded** (quantized x, y, heading per tick, delta +
  deflate, about 10–20 KB). Ghosts and "replay best" play recordings, so they never break. Re-simulating
  genomes is only used for "watch whole generation" and fork, and only when SimVersion matches.
- **Performance layout:** agents stored as arrays (`float[] posX, posY, ...`), brains as one flat `float[]`.
  **Zero allocations** inside the tick loop.
- **Threading:** the sim runs on its own scheduler thread plus W workers. Agents never interact, so each
  worker owns a slice of agents and runs **K ticks with no barrier** (up to the next snapshot or frame
  budget). Workers sync only to publish a snapshot and at generation end. Slices are rebalanced from the
  alive agents every 60 ticks (dynamic chunks of 32 agents via an atomic counter). Pad slice data to 64 bytes.
  - Default W with visuals on = **physical cores − 1** (3 on the owner's laptop).
  - Default W headless = **logical threads − 1** (7).
- **Snapshot:** double-buffered `prev` and `curr` arrays plus a tick index, generation id and alive flags.
  The render thread reads only the latest snapshot.
- **Renderer:** Godot **Compatibility** (OpenGL 3.3), the lightest option on Intel Iris Xe. Cars use
  **one MultiMesh**, filled from a pre-allocated `float[]` and pushed with one
  `RenderingServer.MultimeshSetBuffer` call per frame. Dead or hidden cars use `VisibleInstanceCount`.

---

## 3. Racing mode: detailed spec

### 3.1 Map
- Grid of cells, `byte[] cells`. Sizes: S 192×108, **M 256×144 (default)**, L 384×216. One cell = 6 px at
  100 % zoom, drawn with nearest filtering (pixel look).
- Cell types:

  | Type | Effect |
  |---|---|
  | `Road` | full grip, normal drag |
  | `Grass` | low grip, high drag (values in §3.3) |
  | `Wall` | solid |
  | `Danger` | destroys the car |
  | `Finish` | road that ends the run when the car centre enters |
  | later | `Boost`, `Ice` |

  Outside the map counts as Wall.
- **Start:** position + angle (free angle, snapped to 15° in the editor). The editor and the generator require
  at least one car length of free road behind the start.
- The map renders as **one texture** (1 px per cell, scaled up). Editing updates only the changed pixels.

### 3.2 Distance field, clearance, finishability
- **Clearance:** a distance transform from blocking cells (Wall, Danger, map edge). A cell is passable for the
  car if its clearance ≥ the car half-width (§3.3). The editor highlights "too narrow for the car here".
- **Distance field:** 8-neighbour Dijkstra (orthogonal 1, diagonal √2, no diagonal step between two blocking
  cells) from all Finish cells over passable cells. Grass cost = `roadTopSpeed / grassTopSpeed` (derived from
  physics, not a guess). Binary heap; M map < 5 ms.
- **Progress lookup:** bilinear interpolation of the 4 surrounding cells at the car's sub-cell position.
- **Finishable (live in the editor):** the start is reachable in the distance field. Debounced 150 ms.
- **Reference driver** (scripted, no learning): steers down the distance-field gradient with real physics and
  slows before sharp turns. It is used for:
  - the **estimated time** (its finish time);
  - "finishable ✓" for the generator and for the editor's **Test** button: the car really completes it;
  - the auto time limit.

  If the reference driver fails but the field says reachable, the editor shows
  "finishable, but very tight". The live editor estimate between Test presses =
  `pathLength / (0.6 × maxSpeed)`; the `0.6` is a named constant.
- **Grass-shortcut check:** compare the road-only route against the route through grass. If grass is more than
  10 % faster, the editor warns ("cars will cut across the grass here") and the generator rejects the track.

### 3.3 Car physics (arcade, top-down)
- State: position, heading, velocity (2D), angular speed.
- Outputs from the brain: `throttle ∈ [-1,1]` (negative = brake, then slow reverse) and `steer ∈ [-1,1]`.
- Per tick:
  - accelerate along the heading;
  - steering rate scales with speed (none while stopped);
  - surface grip removes lateral velocity (drifting on grass);
  - apply surface drag;
  - clamp to max speed.
- **Sub-stepping:** `n = ceil(speed × dt / 0.5 cell)` sub-steps per tick, so nothing tunnels.
- **Collision:** sample points along the car's outline at most 0.5 cell apart.
  - **Wall (default "block"):** remove the velocity component along the wall normal and keep 50 % of the
    tangential speed. Setting: block / bounce / kill.
  - **Danger:** kill.
  - **Car-to-car:** none.
- **Constants** (starting values; tuned in M1, final values recorded here):

  | Constant | Start value | Final (M1) | Hard limits (settings) |
  |---|---|---|---|
  | Car size | 3.0 × 1.8 cells (half-width 0.9) | same | fixed |
  | Max speed (road) | 18 cells/s | 18 cells/s | 5…60 |
  | Acceleration | 14 cells/s² | 14 cells/s² | 2…60 |
  | Brake | 30 cells/s² | 30 cells/s² | — |
  | Reverse cap | 4 cells/s | 4 cells/s | — |
  | Turn rate at full speed | 200 °/s | 200 °/s | 60…400 |
  | Grass top speed / grip | 45 % / 0.45 | 45 % / 0.45 | 10…100 % |

  M1 result (**provisional** until M5): the `learn-suite` passes on the 7 bundled tracks, including a labyrinth
  and an obstacle field that random search cannot solve (control cases), with the start values (and the
  §3.4/§3.7 defaults) unchanged, so nothing was retuned; details in NOTES.md. The tuning target below (20–40 s on
  generated tracks) can only be checked in M5, so the "Final (M1)" column may still change then.

  Tuned so the reference driver takes 20–40 s on generated M tracks.

### 3.4 Sensors (inputs normalized to about [-1,1])
- **Rays:** default 7, spread 180° in front (count 3–15 and spread 90–360° are settings). Range **30 cells**
  (setting 10–80). One DDA pass per ray gives 2 values:
  - distance to the first non-road cell / range;
  - distance to the first wall or danger cell / range.
- **Body:** speed, lateral slip, angular speed, on-grass flag.
- **Memory inputs** (on by default): previous tick's throttle and steer, elapsed time / time limit, and
  distance driven / track length. These let the net tell apart forks that look the same, as on Labyrinth and
  Wrong-turns tracks.
- Default total: 7×2 + 4 + 4 = **22 inputs**.
- **Direction hint** (setting, off by default): the angle to the downhill direction of the distance field.
  It makes learning much easier. If a template's learning test (§9) fails without it, that template turns it
  on automatically and the UI shows "hint on".

### 3.5 Brain
- MLP 22 → 12 (tanh) → 2 (tanh). Hidden size (4–32) and a second hidden layer are settings
  ("new training only", §6.4).
- Genome = flat `float[]` of weights + biases. Generation 0 is initialized N(0, 0.5), with a **+0.3 bias on
  the throttle output** so cars start moving.

### 3.6 Score (fitness)
- `progress = 1 − bestDist / startDist`, clamped to 0..1, using the bilinear lookup.
- **Not finished:** `score = progress − 0.01 × ticksToBestProgress / timeLimitTicks`. The small tie-breaker
  means reaching the same point sooner is better.
- **Finished:** `score = 2 + (timeLimit − finishTime) / timeLimit`, with the finish time interpolated to a
  sub-tick. Any finisher beats any non-finisher.
- **Start grace:** 1.5 s before the stall clock runs.
- **Stall:** no improvement of bestDist by more than 0.25 cell for 3 s (setting) → the car stops.
- **Generation time limit:** default `auto = referenceTime × 2.5` (editable).

### 3.7 Evolution
- **Every genome is evaluated every generation, elites included.** No stale scores.
- Defaults (all settings):
  - population 200;
  - elitism 5 %, copied unchanged;
  - tournament selection, size 3;
  - uniform crossover 50 %;
  - per-weight mutation chance 10 %, Gaussian σ 0.2;
  - 2 % new random genomes per generation.
- **Stagnation**, checked over a 15-generation window (setting):
  - before any car has finished: best progress improved by less than 1 cell (absolute);
  - after a finish: best time improved by less than max(1 tick, 0.2 %).

  It then triggers an **auto mutation boost** (σ ×2, chance ×2) for 5 generations, on non-elite offspring
  only, and shows a banner. It can be turned off.
- The manual mutation slider and the boost take effect **at the next generation boundary**. The effective
  parameters are stored in each GenerationRecord.

---

## 4. Speed, FPS and resource caps

- **Speed controller:** `ticksDue += realDelta × 60 × speed`. It runs whole ticks within a frame budget
  (14 ms of sim time per frame with visuals). If it can't keep up, the HUD shows
  **"100× (actual 64×)"**. It never blocks the UI.
- Speed steps: 0.01, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 25, 50, 100, MAX.
  Keys: `-` / `+` change speed, Space pauses, `.` advances one tick while paused.
- **Interpolation (≤ 1×):** render alpha = the fractional part of ticksDue, clamped to 0..1, drawn between
  `prev` and `curr`. It never extrapolates.
  - Heading lerps along the shortest arc.
  - Cars whose alive flag or generation id changed are not interpolated.
  - Above 1× only `curr` is drawn.
- **Train without visuals:** "Train to generation N", "until a car finishes" or "until best time < X s".
  - The map rendering stops. A progress screen (10 FPS) shows the live graph and generations/s.
  - The sim runs at MAX with the headless thread count, then switches back to the view.
- **FPS cap:** 24/30/45/60/75/120/unlimited/**match monitor**, default 60, plus a VSync toggle.
  - If the cap doesn't divide the monitor refresh rate (`DisplayServer.ScreenGetRefreshRate()`), Settings
    hints: "75 Hz monitor: choose 75 or turn VSync off for smooth motion".
  - Unfocused window → 15 FPS.
- **CPU cap:** "Simulation threads" (1…logical cores) and "Process priority" (Normal / Below normal; Below
  normal is the default while training without visuals). Windows does not cap an app by itself; these two
  settings are how it's done.
- **Memory cap:** the app normally needs < 1 GB. RAM only grows with **history**. Setting:
  "History memory limit" (default 1 GB, max 75 % of RAM). When full, the oldest full-population snapshots are
  thinned. Best genomes, recordings and stats of every generation are always kept (they are small). The setting
  text says plainly: more RAM does not make training faster.

---

## 5. History, segments, replay, fork, save

- **SimConfig hash** covers everything that affects a trajectory or a score: map, physics, sensors, brain
  shape, walls mode, stall time, time limit, fitness constants, SimVersion. Changing it starts a new
  **segment**. Best-ever time, the stagnation counter and the graph baseline reset per segment (the graph shows
  a divider line).
- Per generation, keep a **GenerationRecord**:
  - seed, effective evolution parameters, stats (best/avg score, best time, finished count);
  - best genome;
  - **best-car recording**;
  - a full-population snapshot (thinned when memory is full).
- **Any generation can be rebuilt** deterministically from the nearest earlier snapshot plus the stored
  seeds/parameters. So **every** generation is forkable, even after thinning (it just takes longer).
- **Fork:**
  - "Fork from generation X": continue from the full population, optionally with new evolution settings.
  - "Fork from best only": a new population of mutated copies of that generation's best.

  Branches appear as a list/tree in the Training panel; one is active.
- **Replay:** "Watch best" plays the recording. "Watch whole generation" re-simulates (only when SimVersion
  matches). Replay opens its own view with its own speed control and a Back button. Training pauses, or keeps
  running headless if "keep training while watching" is ticked.

### 5.1 File formats (written in M1, with round-trip tests)
- Location: `%APPDATA%\Nitrogenesis\` (outside the program folder, per APP-STANDARDS). Tracks in `tracks\`,
  sessions in `sessions\`, settings in `settings.json`.
- **`.track`** (JSON):
  `{formatVersion:1, name, width, height, start:{x,y,angleDeg}, cells: base64(deflate(byte[] cells)), meta:{template?, params?, seed?}}`.
  Finish is a cell type. `trackHash = SHA-256(width, height, cells, start)`.
- **Session = a folder** `sessions\<name>\`:
  - `header.json`: formatVersion, SimVersion, current branch, the segment list (each with SimConfig + track
    hash + track copy), the branch table and the current population. Written atomically: write a temp file,
    then `File.Replace`.
  - `history\<branch>\gen-<from>-<to>.bin`: append-only chunks of 25 GenerationRecords, never rewritten.
    Thinning rewrites a chunk without its snapshots.
  - Autosave every 2 min and on exit, on a background thread from a copied snapshot. It writes only new chunks
    plus the header.
- **`settings.json`**: `{formatVersion, values:{key:value}}`, with missing keys falling back to defaults.
- A **SimVersion mismatch** on load means: recordings still play; re-simulation, fork and resume of that
  population are disabled with a clear message ("made with an older physics version").

---

## 6. Screens and UI

### 6.1 Main menu
New training · Continue (last session) · Sessions · Track editor · Race (play yourself) · Settings.
Update banner per APP-STANDARDS §2.

### 6.2 Training view
- **Map and cars.** The best car has a gold outline. Colours: random (HSV, S 0.6–0.9, V 0.8–1) / per family
  (inherited from the parent with a small hue shift) / single. Car model: one of 3, cosmetic only (same hitbox).
- **HUD** (top-left): generation, alive / total, generation time / limit, total training time, best progress %
  this generation, best time ever (this segment), speed (target/actual), FPS. "Hint on" and "Boost active"
  badges when they apply.
- **Graph:** best and average score per generation; click to enlarge; segment dividers.
- **Buttons:**
  - **Pause**, **Step** (one tick).
  - **Mode Auto/Step.** Auto = the next generation starts at once. Step = pause at the end of each generation
    with a summary and a "Next" button.
  - **Next generation** = end the current one now and score the cars where they are.
  - **Train to…**, **Replay best**, speed slider.
- **Click a car** → follow it, show its rays and the live brain view (neurons and connections lighting with
  activation). Camera: zoom/pan, follow best / follow selected / free. Route trail for followed cars.
- **Training panel:** history list (each row: Watch best · Watch whole generation · Fork), branches, segments.

### 6.3 Editor
- Tools: brush (sizes 1–15 cells, round/square), line, rectangle, fill bucket, eraser.
- **A new map starts as wall**; the eraser paints wall. You paint road (and grass verges) into it.
- Paint types: road, grass, wall, danger, finish. Start flag with a rotate handle.
- Undo/redo (≥ 100 steps), grid toggle, zoom/pan.
- **Status bar:**
  - "Finishable ✓" / "Not finishable — finish is blocked" (the unreachable area is highlighted);
  - "too narrow" highlights;
  - grass-shortcut warning;
  - estimated time, with a warning if it is under 20 s.
- Buttons:
  - New / Open / Save;
  - Generate (§7);
  - **Test** (reference driver runs it and shows its time and route);
  - **Drive** (you drive it; player controller from M2).

### 6.4 Settings
Every setting has a tooltip and "reset to default". Presets: Easy / Default / Chaos. Each setting has a
**kind**, and the UI enforces it:

| Kind | Settings | Behaviour during a session |
|---|---|---|
| Live | mutation σ/chance, elitism, random %, stagnation on/off, speed, graphics, sound, threads, priority, history memory | applies now (evolution ones at the next generation) |
| New segment | physics constants, walls mode, time limit, stall time, population size (trim worst / fill with mutants) | confirm dialog → new segment |
| New training only | ray count/spread/range, memory inputs, direction hint, hidden size/layers | greyed out, tooltip "start a new training to change" |

- **Graphics quality:**
  - Low: no trails, no ray drawing, max cars drawn 200.
  - High: trails, anti-aliased lines, max cars drawn 1000.
  - Also: FPS cap, VSync, car model, colour mode.
- **Performance:** threads (visual / headless), priority, history memory.
- **Sound:** UI on/off, engine on/off, volumes.
- **Updates:** per APP-STANDARDS §2.

### 6.5 Race yourself
- Pick a track. Pick ghosts from **any session/segment whose trackHash matches**: current, older generations,
  several at once, or none.
- You drive with the physics of the chosen ghosts' segment. If the chosen ghosts come from different physics,
  a warning appears and the first ghost's physics is used.
- Countdown. Your car is highlighted; everyone gets a route line; a live timer runs.
- **Splits** when progress first crosses 25 / 50 / 75 % (ahead/behind each ghost).
- Walls block, as in training. Danger kills you, followed by an instant-restart key (R).
- **Results screen:** times plus a route overlay of you vs the ghosts.
- **Input:**
  - Keyboard: WASD/arrows set throttle/steer to ±1 with a 0.1 s ramp.
  - Gamepad: left stick steers, triggers for throttle/brake.

### 6.6 Sound
- CC0 files in `app/audio/` (listed in `CREDITS.md`) for UI clicks.
- Engine: a looped CC0 sample with `pitch = 0.8 + 1.2 × speed / maxSpeed`, only for the followed or player car.
  Silent above 5× speed.

---

## 7. Track generator and templates

- Every template has parameters + a seed. **"Generate similar"** = same template and parameters, new seed.
  The result opens in the editor and can be changed freely. The template, parameters and seed are stored in
  the track's `meta`.
- Templates:

  | Template | What it makes |
  |---|---|
  | **Sprint** | mostly straight, gentle curves, wide road |
  | **Twisty** | many turns, medium width |
  | **Labyrinth** | maze on a coarse grid (recursive backtracker) carved to road width; many dead ends |
  | **Wrong turns** | main route plus N dead-end branches that look like shortcuts |
  | **Obstacle field** | wide road with wall blocks and danger patches |
  | **Mixed** | random mix of the above segments |

- Method:
  1. Start from a full-wall map.
  2. Build a route on a coarse grid (random walk / maze) and smooth it.
  3. Carve road with a brush (width ≥ 2 × car width).
  4. Add a grass verge 1–3 cells wide (parameter).
  5. Add the template's features.
  6. Place the start (with run-off behind it) and the finish zone.
- **Validation (every track):**
  - the reference driver finishes;
  - its time is within the target (default 20–40 s, setting);
  - no grass shortcut more than 10 % faster.

  On failure, retry with the next seed, up to 50 times, then report.

---

## 8. Milestones

Each milestone ends with: tests green, `SimBench` numbers recorded in `NOTES.md`, a Windows build the owner can
run, and a short "what to try" list. **Milestone builds** are uploaded as a zip on a GitHub **pre-release**
(the `/releases/latest` updater ignores pre-releases). They are never put in `~/shared`.

Model per milestone follows the owner's rule (~/.claude/CLAUDE.md, 2026-10-05): pick a **lane** by the hardest part
of the change — S small, T2 standard, T3 hard (unclear bugs, data, cross-cutting, anything that can lose data),
T4 high-value (architecture, final judgements). Coding is **Opus 5.5** with effort by lane (S medium, T2 high,
T3 xhigh, T4 max). **Sonnet** only for lookups and mechanical edits. **No Fable** unless a task is clearly beyond
Opus 5.5 at max, and then only after asking the owner. **No Codex reviews.** Each milestone gets an Opus 5.5 review
at its lane's effort before the owner sees it.

\* Owner's exception, for the chat that builds Nitrogenesis from 2026-10-06 only (not a global rule): the
well-specified T2 milestones M4, M6 and M7 are built by Sonnet 5.5 at high effort from this plan, then reviewed and
fixed by Opus 5.5 before the owner sees them.

| # | Milestone | Content and acceptance | Lane → model |
|---|---|---|---|
| M0 | Setup | Ask the owner about the GitHub repo (Husarp per rules; public/private). Install the .NET 10 SDK and **one pinned Godot .NET stable (pinned: 4.7.2, SDK 10.0.401)** + matching export templates (by script, checksum), recorded in `build/versions.txt`. Repo skeleton (§2), `VERSION` file, headless export `godot --headless --path app --export-release "Windows Desktop"`, rcedit via Wine for icon/version. **Accept:** the zip unpacks and runs on the EliteBook; the window opens at the 60 FPS cap; exe Properties show the icon and version. | done (S) |
| M1 | Sim core | Grid, clearance, distance field, reference driver, car physics, sensors, MLP + FastMath, evolution, IAgentMode, scheduler/threading, recordings, RNG, file formats (§5.1), 5 bundled hand-made tracks. **Accept:** all §9 unit tests; `learn-suite` passes on bundled tracks; bench targets met on the Linux box (re-measured on the laptop in M2). | done (T3) |
| M2 | Viewing | Map texture, MultiMesh cars, camera, speed controller + interpolation, pause/step, HUD, follow/click car, rays, **player controller** (keyboard/gamepad → CarPhysics), track picker. **Accept:** 300 cars at 100× on the laptop with visuals, FPS steady at the cap; 0.01× looks smooth. | T2 → Opus 5.5 high |
| M3 | Training control | Auto/Step/Next generation, Train-to (3 stop conditions) without visuals, graph, stagnation + boost, history, segments, replay views, fork (both kinds), session folder save/resume/autosave. **Accept:** kill the app mid-autosave → it resumes; fork from a thinned generation works. | T3 → Opus 5.5 xhigh (saves can lose data) |
| M4 | Editor | Tools, paint types, start/finish, undo/redo, live finishable / too-narrow / grass-shortcut / estimate, Test (reference driver), Drive, save/load. **Accept:** walling off the road shows "Not finishable" within 0.2 s. | T2 → Sonnet 5.5 (high) builds, Opus 5.5 (high) reviews * |
| M5 | Generator | 6 templates, generate similar, validation. **Accept:** 50 seeds per template → ≥ 90 % valid on the first try, and `learn-suite` passes on the fixed-seed tracks. | T3 → Opus 5.5 xhigh |
| M6 | Race yourself | Ghost picker (trackHash match), splits, results, route overlay, restart. | T2 → Sonnet 5.5 (high) builds, Opus 5.5 (high) reviews * |
| M7 | Settings & polish | All settings with kinds/tooltips/presets, resource caps, FPS cap + 75 Hz hint, car models/colours, brain view, sounds. | T2 → Sonnet 5.5 (high) builds, Opus 5.5 (high) reviews * |
| M8 | Release | Checklist below. | T3 → Opus 5.5 xhigh (installer/updater) |
| M9 | Review pass | Full review of performance, determinism and UX before 1.0. | T4 → Opus 5.5 max |
| M10 | Platformer | Side view, gravity/jump physics, block collisions, sensors, editor palette. **Reachability = Dijkstra over a jump graph** (standable cells; edges for walk, fall and jump arcs simulated with the real jump physics), used for progress and finishability. | T3 → Opus 5.5 xhigh |
| later | Maybe | NEAT brains, champions library, training on several tracks, boost/ice surfaces. | — |

### M8 release checklist (from APP-STANDARDS; copy each item, tick it)
- [ ] `NitrogenesisSetup-X.Y.Z.exe`: one exe that installs, updates and uninstalls. Per-user, no admin. Copies the
      whole export (exe + pck + `data_*` folder) and removes the old `data_*` folder on update.
- [ ] Setup window per §5: welcome, "app is running", progress page with details, finish, `--update` mode,
      failure with TRY AGAIN, uninstall ("also delete my data" unticked). The app's own look.
      Built via Wine, reusing Lockdown's installer approach (steps and threading only, not its look).
- [ ] Version from `VERSION` written into project.godot, export_presets and the installer; the build fails
      on any mismatch.
- [ ] **Self-test:** `wine Nitrogenesis.exe --headless -- --selftest` runs 5 generations on a bundled track, checks
      the determinism hash and a save/load round trip, and exits 0. The build fails otherwise.
- [ ] **Update check:**
  - at launch and on window focus-in (`NOTIFICATION_APPLICATION_FOCUS_IN`), at most every 5 min;
  - numeric version compare;
  - silent when an automatic check fails; a manual check gives up after ~10 s and explains why in words.
- [ ] **Banner:** version + UPDATE + ✕ (hidden until the next start). Settings: switch, CHECK NOW, GITHUB,
      GET UPDATE.
- [ ] **In-app update:** downloads `NitrogenesisSetup-X.Y.Z.exe` with progress, runs it with `--update`, and closes
      itself. Deletes the downloaded installer at the next start. On failure: TRY AGAIN + GITHUB.
- [ ] **GitHub release** marked Latest, with the version in the file name.

---

## 9. Verification

- **Unit tests** (Sim):
  - distance field on hand-made maps (diagonals, no corner cutting, grass cost);
  - clearance / "too narrow";
  - DDA rays with range;
  - tunnelling: 1-cell walls at 0°, 45° and shallow angles at the **highest allowed** max speed;
  - wall block/bounce/kill;
  - **determinism:** same seed twice → identical; 1 thread vs 7 threads → identical; trajectory-hash
    constant;
  - mutation/crossover shapes;
  - file round trips;
  - the segment/SimConfig hash changes on every relevant setting;
  - gen-0 sanity: ≥ 20 % of cars move more than 5 cells.
- **`learn-suite`** (default settings, fixed seeds). Each needs a finishing car within its budget:

  | Track | Generation budget |
  |---|---|
  | Sprint | ≤ 30 |
  | Twisty | ≤ 100 |
  | Wrong turns | ≤ 150 |
  | Labyrinth | ≤ 200 |
  | Obstacle field | ≤ 100 |

  Plus a **grass-shortcut regression** track where the best route must stay on the road. Record the
  generations-to-first-finish in NOTES.md. Tune the defaults until everything passes.
- **Performance targets**, measured on the owner's EliteBook 830 G8 **plugged in, after 5 min of sustained
  load**:

  | Mode | Target |
  |---|---|
  | Headless, 7 threads | ≥ 2.5 M agent-ticks/s (200 cars, 25 s track → ≥ 3 generations/s) |
  | Visuals on, 3 sim threads | 300 cars at 100× (1.8 M agent-ticks/s) with FPS steady at the 60 cap |
  | Rendering | 500 cars drawn, no GC pauses (zero-allocation check in a debug overlay) |

  If a target is missed, the HUD's "actual speed" makes it visible and the defaults are lowered. The owner
  can always reduce the population.

---

## 10. Open items (decide when reached)

- App name: **Nitrogenesis** (chosen 2026-10-04; web check found no game/app with this name).
- GitHub repo, public or private: at M0.
- Car model looks (3 models): designed in M7.
