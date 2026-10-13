# Milestone notes

## M0 — Setup (2026-10-04)

- Pinned tools (build/versions.txt): Godot 4.7.2-stable .NET (editor + export templates, SHA-512 checked) in
  ~/tools/godot-4.7.2 and ~/.local/share/godot/export_templates/4.7.2.stable.mono; .NET SDK 10.0.401 in ~/.dotnet;
  rcedit 2.0.0 in ~/tools/rcedit; Wine 10.0 (apt) with prefix ~/.wine-nitrogenesis.
- Build: `build/export-windows.sh` → out/Nitrogenesis-X.Y.Z-win64.zip (72 MB: exe with embedded pck + data_ folder
  with the .NET runtime). It writes VERSION into project.godot and export_presets.cfg, runs the Sim tests, fails on
  any export ERROR, and stamps icon + version with rcedit.
- Checked on Linux: tests green; exe starts under Wine headless (.NET module loads, no errors); version resource
  0.1.0.0 / Nitrogenesis / Husarp.
- Placeholder icon (orange N with speed streaks) in app/icon.png/.ico; final look in M7.

What to try on the laptop: unzip, run Nitrogenesis.exe → dark window "NITROGENESIS", version line, FPS ≈ 60 (cap 60);
right-click the exe → Properties → Details shows version 0.1.0.0 and the icon.

## M1 — Sim core

### Stage A — foundations (2026-10-04)

Done: `Rng/` (SplitMix64, xoshiro128**, `SeedHash.Derive(seed, index)`), `Brain/FastMath`, `Map/` (CellType, Grid,
Track/TrackStart/TrackMeta, Clearance, IReverseGraph + Dijkstra, RacingGraph, DistanceField, Solvability, GrassShortcut,
TrackHash, TrackBuilder), `Io/TrackFile`, `Io/SettingsFile`, `Racing/CarSize`, 5 bundled tracks. 99 tests green.

- Bundled tracks are made by code: `dotnet run --project tools/SimBench -c Release -- make-tracks tracks`
  (definitions in `tools/SimBench/BundledTracks.cs`; it validates each track and prints its hash). Their hashes are
  pinned in `BundledTracksTests`; change them only on purpose.

  | Track | Road radius / verge | Start→finish (road cells, grass cost 1/0.45) |
  |---|---|---|
  | sprint | 6 / 2 | 221.5 |
  | s_curve | 5 / 2 | 279.9 |
  | hairpins (3 × 180°, radius 12) | 4 / 2 | 578.7 |
  | wrong_turn (2 dead ends) | 4 / 2 | 299.1 |
  | grass_shortcut (U loop, wall inside) | 5 / 3 | 386.2 (road only: same) |

- Cross-platform pins (each cross-checked with an independent Python implementation): FastMath table checksum
  `0x4CE623DF13DDE839`, SeedHash values, the TrackHash of a sample map and of the 5 bundled tracks.
- Measured on the Linux box (Release, warm): clearance 0.2–0.4 ms and distance field 0.9–1.6 ms on the bundled
  M tracks; worst case (an all-road 256×144 map) clearance 3.6 ms, field 4.7 ms. Target < 5 ms (§3.2) met.

### Stage B — racing mode (2026-10-04)

Done: `Core/` (IAgentMode, IAgentPolicy, AgentRange, AgentStatus, AgentSnapshot, SimConfig + ConfigHashWriter +
IModeSettings), `Brain/BrainShape`, FastMath fixed-point angles, `Racing/` (RacingSettings + WallMode, CarState,
CarPhysics, RacingTrack, Sensors, RacingFitness, RacingMode, ReferenceDriver), `History/Recording`,
`SimBench reference [dir]`. 183 tests green (Debug and Release).

- **Reference driver on the bundled tracks** (default settings; `dotnet run --project tools/SimBench -c Release -- reference tracks`).
  Physics constants are still the plan's start values; the 20–40 s tuning target is for generated tracks (M5). *(SimVersion 1 values; Stage E has the current table.)*

  | Track | Reference time | Auto time limit (× 2.5) | Field path (cells) |
  |---|---|---|---|
  | sprint | 12.77 s | 31.9 s | 221.5 |
  | s_curve | 16.57 s | 41.4 s | 279.9 |
  | hairpins | 33.99 s | 85.0 s | 578.7 |
  | wrong_turn | 17.68 s | 44.2 s | 299.1 |
  | grass_shortcut | 22.44 s | 56.1 s | 386.2 |

  It also finishes wrong_turn with the fastest (60 cells/s, 400 °/s) and slowest (5 cells/s, 2 cells/s², 60 °/s) physics.
- **Cross-platform pins:** trajectory hash of the scripted 6-car run on sprint.track `0xEC896791D02BFE84`
  (`RacingModeTests`), default SimConfig hash `38c865c4…e2ee95` (`SimConfigTests`). The same run built as a
  win-x64 self-contained app and run under Wine (Windows .NET runtime) gave the identical trajectory hash,
  FastMath checksum and bit-identical reference times for all 5 tracks.
- **Speed (Linux box, Release, 1 thread, sensors + physics, no brain yet):** 1.23 M agent-ticks/s (200 cars on
  sprint). `Step` allocates nothing (tested with `GC.GetAllocatedBytesForCurrentThread`).

### Stage C — brain, evolution, generation runner (2026-10-04)

Done: `Brain/Mlp` (+ `BrainShape.WeightCount/OutputBiasIndex`), `Rng/Gaussian`, `Evolution/` (EvolutionSettings +
EvolutionParams, Genome, Population, Selection, Crossover, Mutation, Breeder, StagnationTracker), `Core/AgentScheduler`,
`Core/SnapshotBuffer`, `Core/GenerationRunner`, `Racing/RacingTraining`, `History/GenerationRecord`, `Io/HistoryChunk`,
`Io/SessionHeader`, `Io/SessionFolder`, `SimBench train <track> [gens] [threads] [seed]`. 266 tests green (Debug and Release).

- **Cross-platform pins:** Gaussian stream (seed 42, 1000 samples) `0xBE546455D1BB9612`, cross-checked with an
  independent Python implementation; population after 3 generations on sprint.track, seed 12345, defaults
  `0x5D0180A8CE12C663` (`GenerationRunnerTests`). The same run built as a win-x64 self-contained SimBench and run
  under Wine (Windows .NET runtime) printed the identical population hash.
- **Threads:** identical records and population hashes for 1, 2, 3, 7 and 11 threads (tests: 1 vs 7 for the runner, 1/2/7 for the scheduler).
- **Speed (Linux box, Release, 200 cars, sprint, 30 generations, full loop incl. brain, recording and breeding):**

  | Threads | Generations/s | Agent-ticks/s |
  |---|---|---|
  | 1 | 7.1 | 1.05 M |
  | 3 | 15.1 | 2.25 M |
  | 7 | 19.5 | 2.90 M |
  | 11 | 17.5 | 2.60 M |

  7 threads on the other tracks: s_curve 2.72 M, hairpins 2.74 M, wrong_turn 2.36 M, grass_shortcut 2.96 M.
  Scaling stops near 7 workers because 200 cars make only 7 chunks of 32, and fewer once cars stop (the tail of a
  generation runs on 1–2 workers). Target ≥ 2.5 M with 7 threads is met here; the laptop is re-measured in M2.
- **First finish (SimBench train, seed 1, defaults):** generation 0 on sprint, s_curve, hairpins and grass_shortcut,
  generation 3 on wrong_turn. The bundled tracks are easy; the real `learn-suite` budgets use generated tracks (M5).

### Stage D — SimBench and tuning (2026-10-04)

Done: `Racing/LearnTest` (the §9 learning test + `GrassShare` of a recording), SimBench `bench`, `learn-suite` and
`train --track X --gens N [--pop P] [--threads T] [--seed S]` (replaces the positional form; `--track` takes a path
or a bundled track name). Faster `Mlp` and ray casting (bit-identical: every pinned hash unchanged). 271 tests green
(Debug and Release).

- **learn-suite** (`dotnet run --project tools/SimBench -c Release -- learn-suite`): default settings, seeds 1, 2 and 3
  on every track; all 15 cases pass in ~20 s.

  | Track | Budget | First finish (seeds 1 / 2 / 3) | Best time at first finish | Notes |
  |---|---|---|---|---|
  | sprint | 30 | 0 / 0 / 0 | 12.9–13.1 s | |
  | s_curve | 100 | 0 / 0 / 0 | 16.6–16.9 s | |
  | hairpins | 100 | 0 / 1 / 1 | 33.7–41.4 s | |
  | wrong_turn | 150 | 3 / 3 / 3 | 24.6–29.6 s | seeds 4–13: 1–7 |
  | grass_shortcut | 100 | 0 / 0 / 0 | 22.0 s after 100 gens | best route on grass 0.5–0.6 % of ticks (limit 5 %) |

- **Tuning: nothing changed.** Every case passes with the plan's start values (§3.3 physics, 30-cell rays, §3.6
  fitness constants, §3.7 evolution defaults, +0.3 throttle bias), with wide margins, so no default was retuned and
  the direction hint stays off everywhere (§3.4 auto-enable not needed). PLAN §3.3 now lists the final values
  (= start values). The bundled tracks are easy; generated tracks get the real test in M5.
- **Bench** (`... -- bench`; s_curve, seed 1, generations 3–12 timed, full loop incl. brain, recording, breeding;
  Linux box, Release):

  | Cars | 1 thread | 3 threads | 7 threads |
  |---|---|---|---|
  | 200 | 1.21 M agent-ticks/s, 7.9 gens/s | 2.86 M, 18.7 gens/s | **3.39 M, 22.1 gens/s** |
  | 300 | 1.28 M, 5.0 gens/s | 2.52 M, 9.8 gens/s | 3.80 M, 14.8 gens/s |

  Target ≥ 2.5 M with 7 threads met (was 2.89 M on s_curve before the optimizations below). Parts of one
  agent-tick, single-threaded: rays 472 ns (67 ns per ray), all sensors ~480 ns, brain 137 ns; whole agent-tick in
  training ~820 ns. Repeat runs vary by about ±10 %.
- **Generations/s while training** (`train --gens 30`, 7 threads, 200 cars, incl. generation 0 and the reference
  run): sprint 20.8, s_curve 18.4, hairpins 8.9, wrong_turn 15.4, grass_shortcut 13.1 (2.6–3.1 M agent-ticks/s).
- **Optimizations** (both keep results bit-identical, checked by the pinned trajectory and population hashes):
  - `Mlp`: four neurons summed side by side, each in its own bias-then-input-order chain, so the CPU overlaps the
    add latencies (brain 216 → 137 ns).
  - Rays: two tight DDA loops (over road until the first non-road cell, then until the first wall) instead of one
    general loop with bookkeeping (74 → 67 ns per ray). A branch-free DDA step was tried and was slower (104 ns),
    so it was dropped.
- Scaling past ~3× stays limited by 200 cars making only 7 chunks of 32 (§2.1) and by the tail of a generation;
  more cars scale better (300 cars: ×3.0).

### Stage E — review fixes (2026-10-05)

An Opus review of M1 found 16 problems; all high and medium ones and the cheap low ones are fixed. **SimVersion is
now 2** (physics clamp, passability and the reference driver changed trajectories, fields and time limits).
300 tests green (Debug and Release).

- **Top-speed clamp** (was broken above ~35 cells/s² acceleration: speed grew without limit, e.g. 289 cells/s at
  the fastest settings): now a hard clamp against this tick's acceleration; only speed already over the cap is
  bled. Test sweeps acceleration 2…60 × max speed 5…60, forward/reverse, road/grass.
- **Passability** = best of a cell's 9 half-cell lattice points (was: its centre), so the field sees every corridor
  the car can drive (a 2-cell corridor was +∞ while cars drove through it). Clearance is now an exact integer
  Euclidean distance transform on the lattice (Meijster): 2.1–2.7 ms on the bundled tracks, 2.9 ms worst case
  (all-road M map; was 3.6 ms), 6.3 ms for an all-road L map. Distance field 0.6–2.6 ms. Target < 5 ms (§3.2) met.
- **Start pose overlapping a wall** is now `SolvabilityResult.StartOverlapsWall`, and `RacingTrack.IsFinishable`
  is false for it (such a car can never move).
- **Reference driver:** un-jam rule (reverse with mirrored steering when the nose is stuck on a wall), a steering
  field that keeps the path off walls, aim points on the midline of corridors too narrow at cell centres, and a
  line-of-sight check so pure pursuit does not cut inside corners into the wall. It now finishes a U track from a
  start facing away (180°/270°), 2-cell corridors and a 30° corridor painted with a 3.5-cell brush.
- **Scheduler:** chunks of 4 alive agents (was 32; see Deviations), `Run` returns the ticks really run (elapsed
  ticks and the snapshot tick no longer overshoot the last car by up to 59 ticks), and `RunAlongside` lets the
  best-car recording (+ population copy) run on a worker while the caller breeds: end of generation 1.0–2.4 ms at
  7 threads (was 2.8–4.6 ms). Ticking per generation at 7 threads: s_curve 41 ms (was 47), hairpins 75 ms (was
  127), wrong_turn 46 ms (was 79).
- **Tests:** zero allocation on all threads (7 workers, process-wide counter, in a non-parallel collection);
  `TestTracks` finds the repo by `VERSION` + `tracks/` instead of PLAN.md.
- **New bundled tracks** (hand-made, so the learn-suite covers every §9 row): `labyrinth` (maze on a 16-cell grid,
  8 turns, dead ends on both sides of the route, some pointing at the finish) and `obstacle_field` (wide U road,
  12 danger-lined wall blocks leaving 4-cell lanes to weave through). `grass_shortcut` redesigned as a sharp V
  (legs 70° apart) whose inside is grass: over grass the route is 23 % shorter in distance (324 vs 420 cells with
  grass costed like road), but at the real grass cost it is no shortcut (the best route uses no grass). The old
  design had a wall inside the U, so it could not catch grass cutting (1.3 % shorter even on distance).

  | Track | Reference time | Auto time limit | Field path (cells) |
  |---|---|---|---|
  | sprint | 12.77 s | 31.9 s | 221.5 |
  | s_curve | 16.59 s | 41.5 s | 279.9 |
  | hairpins | 33.74 s | 84.4 s | 578.7 |
  | wrong_turn | 17.89 s | 44.7 s | 299.1 |
  | grass_shortcut | 23.62 s | 59.1 s | 419.6 |
  | labyrinth | 26.20 s | 65.5 s | 407.5 |
  | obstacle_field | 35.53 s | 88.8 s | 590.2 |

  Track hashes are pinned in `BundledTracksTests` (the 3 new/changed ones cross-checked in Python).
- **learn-suite** (`... -- learn-suite`, default settings, seeds 1/2/3, ~75 s) now has **control cases that must
  fail**: random search (a fresh random population every generation, same budget) on labyrinth and obstacle_field,
  and the road check on grass_shortcut with grass as fast as road. All 21 cases pass and all 9 controls fail:

  | Track | Budget | First finish (seeds 1 / 2 / 3) | Random search (control) |
  |---|---|---|---|
  | sprint | 30 | 0 / 0 / 0 | — (solves it too: an easy track) |
  | s_curve | 100 | 0 / 0 / 0 | — |
  | hairpins | 100 | 1 / 1 / 1 | — |
  | wrong_turn | 150 | 3 / 3 / 3 | — |
  | labyrinth | 200 | 151 / 38 / 42 (seeds 4–15: 23–104) | no finish in 200 (×3 seeds) |
  | obstacle_field | 100 | 33 / 21 / 26 (seeds 4–12: 14–47) | no finish in 100 (×3), nor in 400 (×3) |
  | grass_shortcut (road check) | 100 | 0 / 1 / 0, grass 0.9–1.3 % | with grass speed 100 %: grass 37–69 % → fails ✓ |

  Generation 0 finishes on neither hard track. The defaults still need no retuning; PLAN §3.3's "Final (M1)"
  column is marked provisional until the generated-track check in M5.
- **Bench** (`... -- bench`; 200 cars, 7 threads, every bundled track, 5 runs × ≥ 10 s each; Linux box shared
  with other users, load average 8–9 during the runs):

  | Track | Median | Min | Pinned to 4 cores (`taskset -c 0-3`), median / min |
  |---|---|---|---|
  | s_curve | 4.07 M | 3.97 M | 3.38 / 3.19 M |
  | grass_shortcut | 4.38 M | 4.13 M | **3.03 / 2.84 M** |
  | hairpins | 5.00 M | 4.27 M | 3.45 / 2.93 M |
  | labyrinth | 4.32 M | 4.23 M | 3.38 / 2.73 M |
  | obstacle_field | 4.23 M | 3.92 M | 3.21 / 3.04 M |
  | sprint | 4.60 M | 4.25 M | 3.23 / 2.90 M |
  | wrong_turn | 4.65 M | 4.29 M | 3.74 / 3.51 M |

  Worst track 4.07 M (s_curve) unpinned, 3.03 M (grass_shortcut) pinned to 4 cores: the §9 headless target
  (≥ 2.5 M agent-ticks/s) is met on every track here, also on the laptop proxy; the real laptop is re-measured in
  M2. Before the fix the weakest tracks measured 2.2–2.6 M unpinned. Scaling on s_curve: 200 cars 1.28 / 2.49 /
  4.52 M at 1 / 3 / 7 threads (×3.5), 300 cars 1.35 / 2.60 / 5.16 M (×3.8). Parts: rays 384 ns (55 ns per ray),
  sensors 424 ns, brain 130 ns, whole agent-tick 783 ns. Run-to-run spread on this shared box is up to ±20 %.
- **Cross-platform:** the new pins — trajectory hash `0xB683C7A9FE4FD406`, population hash `0x0307CFF1F139A13F`,
  default SimConfig hash `6d5d2b17…66ec954a` — and all 7 reference times are identical in a win-x64
  self-contained SimBench under Wine (population hash and reference times printed by `train` / `reference`).
- **Not changed:** the per-generation population copy (241 KB, Large Object Heap) stays until the history store
  exists (M3): pooling needs the store to hand thinned snapshots back, and only the store knows which generations
  keep one. Noted for M3.

**What to try (M1):** the app is unchanged (the M0 window). On Linux:
`dotnet run --project tools/SimBench -c Release -- learn-suite` (all cases ok, controls fail),
`... -- train --track labyrinth --gens 60 --seed 2` (progress climbs; first finish at generation 38),
`... -- bench --seconds 3 --repeats 1` (quick speed check).

### Decisions (gaps in the plan, filled closest to its intent)

- **Clearance** is the distance to the nearest point of a blocking *square* (Wall, Danger, outside), not to its
  centre. *(Changed in Stage E:)* a cell is passable when the best of its 9 half-cell lattice points (corners,
  edge midpoints, centre) has clearance ≥ the half-width, so a 2-cell corridor is passable (midline 1.0 ≥ 0.9)
  and a 1-cell one is not; the centre value is kept for the physics broadphase. Exact integer distance transform
  on the lattice (Meijster), IEEE sqrt only.
- **Distance field sources** are the Finish cells that are themselves passable. Grass edges cost
  base × mean(factor of both cells) (symmetric; road→grass with factor g costs (1+g)/2). Diagonal is
  forbidden only when both side cells are blocking, as written.
- **Bilinear sample** leaves out unreachable corners and renormalises, so a car centre near a wall (between a
  passable and a too-narrow cell) still gets a finite value; +∞ only if all 4 corners are unreachable.
- **Grass shortcut** (§3.2): road-only = the same field with grass cost +∞. "More than 10 % faster" is
  roadOnly > 1.1 × withGrass. A finish that needs grass counts as a shortcut. Added in Stage A because the
  grass_shortcut track needs it as a check.
- **FastMath:** tanh table on [−8, 8] (4096 steps, linear interpolation, error < 2e-6, NaN → 0); sin/cos table of
  4096 per turn (error < 2e-6 for |x| ≤ 4π), plus exact `SinIndex/CosIndex` for an angle index. Both are built
  from our own double Taylor series, never libm. Atan2 (for later direction hints) is a fixed-order minimax
  polynomial (error < 4e-6), pure arithmetic.
- **TrackHash bytes:** width, height (int32 LE), cells, start x, y, angleDeg (float32 LE); lowercase hex. Name and
  meta are not hashed, so renaming a track keeps ghosts matching.
- **Start angle:** 0 = +x, positive turns towards +y (clockwise on screen).
- **CellType bytes:** Road 0, Grass 1, Wall 2, Danger 3, Finish 4 (fixed forever; stored in files).
- settings.json is written via temp file + rename (the plan only requires this for header.json; it costs nothing).
- `ImplicitUsings` turned on in the Sim, test and SimBench projects.

**Stage B decisions:**

- **Heading** is an `int` in fixed-point angle units, 65536 per turn (`FastMath.SinUnits/CosUnits`, linear
  interpolation between the 4096-entry table steps). Turning per tick is rounded to whole units (0.0055°).
  The start angle converts with `DegreesToUnits` (nearest unit).
- **Turn rate** "at full speed": the rate grows linearly with forward speed up to 0.3 × max speed, then stays at
  the setting (200 °/s → turning radius 5.2 cells at 18 cells/s). Signed by forward speed, so it reverses when
  rolling backwards, like a car. A rotation that would push the outline into a wall is refused (the heading
  stays); into danger (or a wall in kill mode) it destroys the car.
- **Throttle:** > 0 accelerates forward (brakes at 30 cells/s² first if rolling backwards); < 0 brakes while
  rolling forward, then reverses at the acceleration up to the reverse cap (4, or the grass top speed if lower).
- **Grip** = share of lateral velocity removed per tick: road 1 (no drift), grass 0.45 (setting).
- **Drag** is a constant deceleration towards standstill as a share of the acceleration: road 0.15, grass 0.5
  (2.1 / 7 cells/s² at defaults). A fixed 2 cells/s² drag would have equalled the lowest allowed acceleration
  (2), so that car could not move at all (found by the slowest-physics reference test).
- **Top speed clamp** *(changed in Stage E)*: this tick's acceleration never takes the car past the surface's top
  speed (road max, grass max × 45 %) or the reverse cap: a hard clamp for every acceleration setting. Only speed
  that was already over the cap at the start of the tick (e.g. running onto grass) is bled at 30 cells/s² (the
  brake rate), so the car slows over ~20 ticks instead of stopping dead.
- The **surface** for a tick (grass grip/drag/top speed, on-grass input) is the cell under the car centre at the
  start of the tick.
- **Collision:** 20 outline points (7 per long side, 5 per short side incl. corners; gaps 0.5 / 0.45). Broad
  phase: if the centre cell's clearance is > 2.46 (farthest outline point 1.749 + half cell diagonal 0.707) the
  check is skipped (exact, never changes a result). Wall normal: try the x and y parts of the sub-step move
  alone; a blocked axis is the normal, the free one slides. Convex corner tip (only the diagonal move hits):
  the larger move component is taken as the normal. The 50 % tangential loss is applied once per tick (first
  contact), so sub-step count does not change it. Scraping a wall at an angle tick after tick still costs 50 %
  per tick: walls are expensive, as intended.
- **Bounce:** normal component reversed × 0.5 (restitution), tangential 50 % as in block (the plan names the mode
  but gives no numbers).
- **Finish:** checked after every sub-step; the entry fraction is exact along the sub-step segment (it crosses at
  most one x and one y cell line; the cell between the two crossings is checked too). Finish time =
  ticks before + (sub-step + fraction) / sub-steps. The car stops where it entered.
- **Sensors:** distances from the car centre; Finish counts as road, outside the map as wall; 1 = nothing within
  range; a ray that starts on grass gives 0 for "non-road". Ray order left → right, each [non-road, wall].
  Rays are evenly spaced from −spread/2 to +spread/2; at 360° the turn is divided by the count (no duplicate
  ray). Body: forward speed / max speed, lateral speed / max speed, yaw rate / max turn rate, on-grass 0/1.
  Memory: last applied throttle and steer, ticks / time-limit ticks, distance driven / start distance (not
  clamped; about 0…1.5). Hint: central difference of the bilinear field ±0.5 cell, angle via FastMath.Atan2,
  divided by π; 0 when flat or not finite.
- **Fitness:** bestDist/bestTick follow every improvement; the stall reference moves only on > 0.25 cell. The
  stall clock starts at max(last stall-reference tick, grace = 90 ticks), so a car that never moves stops at
  tick 270. A crashed car keeps its progress score (the plan gives no death penalty). A finished car's
  bestDist is set to 0.
- **Time limit:** explicit setting 1…3600 s, or 0 = auto = ceil(reference time × 2.5 × 60) ticks; if the
  reference driver fails, auto uses the editor estimate path / (0.6 × max speed). Stall time limits 0.5…60 s
  (the plan gives none).
- **SimConfig hash** covers the resolved time limit in ticks (not the auto flag), every physics/sensor/fitness
  constant (also the fixed ones and the car size), walls mode, stall time, brain shape, track hash, mode id and
  SimVersion, each value preceded by its name. Population size is not in it: it changes no trajectory or
  score (§6.4 still makes it a "new segment" setting; that is M3's segment logic).
- **Core API:** `IAgentMode` = `Reset/Sense/Advance(range)`, `Step(range, ticks, IAgentPolicy)`, `Tick`, `Status`,
  `IsDone`, `Score`, `WriteSnapshot(range, AgentSnapshot)`, plus flat `Inputs`/`Outputs` arrays. `Step` runs
  agent-major (each agent K ticks) since agents never interact. `AgentSnapshot` is one buffer; prev/curr
  double-buffering belongs to the scheduler. Mode settings reach `SimConfig` through `IModeSettings`, so
  Core does not depend on Racing. `BrainShape` (inputs/hidden1/hidden2/outputs, hidden 4…32) is added now
  because the config hash needs it.
- **Reference driver:** each tick walks the steepest descent (value + step cost, same diagonal rule) of its own
  steering field up to 96 cells ahead *(Stage E: the racing graph with cells whose centre is < 1.5 from a wall
  costing up to 5×, and aim points moved to the best lattice point of cells too narrow at their centre)*; pure
  pursuit to the first point ≥ 3 + 0.3 × speed cells away (max 9), or the nearest earlier one reachable in a
  straight line ≥ 0.9 from blocking cells (Stage E); speed cap =
  min over bends of √(v_corner² + 2 × 0.6 × brake × distance), v_corner = 0.7 × max turn rate × arc / bend
  angle over ±4 path points (min 3 cells/s); slow to 3 cells/s when facing more than 90° off the path. Un-jam
  (Stage E): forward speed < 0.5 cells/s for 20 ticks while asking for throttle → reverse with mirrored steering
  for 30 ticks. It runs in a one-car RacingMode (same finish/crash/stall rules), capped at 600 s. Deterministic.
- **Recording:** "NGRC", format 1, SimVersion, count, then raw deflate of zigzag varint deltas of x, y (1/256
  cell) and heading (units, wrapped to int16). Hairpins (34 s) encodes well under the 20 KB budget.


**Stage C decisions:**

- **Genome layout:** layer after layer; per neuron its incoming weights in input order, then its bias. A sum starts
  at the bias and adds weight × input in input order; tanh via FastMath. Default 22-12-2 = 302 weights. Hidden
  activations live in per-agent scratch (thread-safe, no allocation).
- **Gaussian:** Box–Muller from two 32-bit draws: u = (top 24 bits + 1) / 2^24 in (0, 1], radius √(−2 ln u) in
  double with our own ln (exponent split + atanh series, ~1e-16), angle = 16 bits as FastMath angle units
  (`CosUnits`). IEEE sqrt is correctly rounded, so deterministic. |N| ≤ 5.8.
- **Generation 0:** every weight N(0, 0.5) in index order, then +0.3 *added* to the throttle output's bias (so that
  bias is N(0.3, 0.5)); genome i uses stream `Derive(Derive(seed, 0), i)`. Fresh random genomes use the same rule.
- **"Uniform crossover 50 %"** is read as: 50 % of offspring are a uniform crossover of two tournament winners (each
  weight from either parent with 50 %, one 32-bit draw per 32 weights); the other 50 % copy one winner. Then every
  offspring is mutated. (`CrossoverRate` setting, default 0.5.)
- **Children order:** elites (best first) at 0…E−1, offspring, then the fresh random genomes at the end. Counts:
  fraction × N rounded half up, at least 1 when the fraction is > 0 (so the best always survives a tiny population).
  200 cars → 10 elites, 186 offspring, 4 random. Child c draws, in order: tournament A (k draws), crossover coin,
  [tournament B, crossover bits], mutation (per weight: 1 draw, +2 when mutated).
- **Ranking:** score descending, NaN last, ties by lower index (a strict total order, so the sort result is unique).
  Tournament = k uniform draws with replacement, lowest rank wins.
- **Seeds:** generation g uses `SeedHash.Derive(branchSeed, g)` (g = 0 initialises, otherwise it breeds g from
  g − 1); child c uses `Derive(generationSeed, c)`. A GenerationRecord stores the seed and the effective params that
  *made* its population; its stats are from evaluating that population.
- **Evolution setting limits** (plan gives none): elitism 0…50 %, tournament 1…10, crossover 0…1, mutation chance
  0…1, σ 0.01…2, random 0…50 %, stagnation window 5…200. Population size is not a live evolution setting (it is a
  "new segment" setting, §6.4); a runner keeps its size.
- **Stagnation:** compares the segment's best-ever progress (cells) / best-ever finish time (ticks) now with
  `window` generations ago (needs window + 1 observations). A first finish inside the window counts as progress.
  A trigger starts 5 boosted breedings and restarts the window (no immediate retrigger). Turning the boost off
  cancels a running boost. Boost = σ × 2 and chance × 2 (capped at 1) for offspring only (elites are copies and
  random genomes are not mutated, so this is "non-elite offspring only"). The tracker is not saved: it is
  rebuilt by replaying the stored generation stats (they hold best progress and best time).
- **IAgentMode** gained `ProgressDistance` (racing: start distance − best distance, cells), `FinishTicks` and
  `Record(agent, recording)`, so stats, stagnation and recordings stay mode-generic.
- **Best-car recording:** after a generation, only the best agent is re-simulated (agents never interact, so this
  repeats its run exactly) and recorded: the start pose plus one sample per tick. Its score is compared bit for bit
  with the original as a built-in determinism check (throws if they differ). *(Stage E:)* it runs (with the
  population copy) on a worker while the calling thread breeds; ~1–2 ms per generation.
- **Scheduler:** the calling (scheduler) thread is worker 0 and W − 1 background threads are the rest, so W
  threads simulate in total. A phase is ≤ 60 ticks; before each phase the alive agents are re-split into chunks
  of 4 *alive* agents (Stage E; was 32 — see Deviations; a chunk spans from its first to its last alive agent),
  taken via an atomic counter. Only as many helpers as there are chunks beyond the first are woken. The chunk
  counter is padded to its own cache lines. A worker exception is rethrown on the caller after the phase. `Run`
  returns the ticks the furthest agent really ran (Stage E), so `ElapsedTicks` and the snapshot tick end exactly
  at the last car's stop, never past the time limit.
- **Snapshot:** `SnapshotBuffer` holds prev/curr; publishing swaps them and writes curr under a short lock; the
  renderer copies both into its own buffers under the same lock (`CopyTo`), so it never sees a torn snapshot and
  nothing allocates. Published at generation start and at the end of every `RunTicks` call.
- **Session header** (§5.1 fields plus what resume needs): `formatVersion, simVersion, currentBranch, evolution,
  segments[{id, branch, firstGeneration, configHash, simVersion, mode, settings, brain, timeLimitTicks, trackHash,
  track}], branches[{name, parent, forkGeneration, seed (hex text)}], population{branch, generation, segment,
  params, brain, weights (base64 LE float32)}`. Settings are camelCase JSON. On load with the current SimVersion,
  each segment's config hash is recomputed and must match; the track copy must match its hash. With another
  SimVersion the header still loads and `Compatibility` disables re-simulation, fork and resume with a message.
- **History chunks:** "NGHC" binary (layout in `HistoryChunk`). Chunk k holds generations 25k…25k+24 (a forked
  branch starts mid-chunk). Records that do not fill a chunk yet are saved as a shorter file with the same `from`;
  see Deviations. Every file goes temp → move; header.json uses `File.Replace`. Branch names are 1–64 of
  letters, digits, '-', '_' (they are folder names).
- **Zero-allocation tests** now measure a few windows and pass when one allocates nothing (`Allocations` helper):
  with the bigger, parallel test suite a tiered-JIT promotion sometimes landed inside the single measured window
  (seen once each in a new Mlp test and in Stage B's `StepDoesNotAllocate`, which now uses the helper too). A real
  per-tick allocation still fails every window.

**Stage D decisions:**

- **learn-suite:** 3 fixed seeds (1, 2, 3) per track, every one must pass; a budget of B means a finish in
  generation 0 … B − 1. Without the road check a case stops at its first finish.
- **Road check** (grass_shortcut): runs the whole budget, then the last generation's best car must have finished
  and have its centre on a Grass cell in < 5 % of its ticks (`LearnTest.MaxGrassShare`; recording samples after
  each tick, the start pose not counted).
- **bench** times the full training loop (what generations/s depends on) from seed 1 after 3 warm-up
  generations. *(Stage E:)* each run lasts whole generations for ≥ 10 s; the §9 headline is 200 cars on
  7 threads on every bundled track, 5 runs each, median/min and the worst track; then the 1/3/7-thread scaling
  on s_curve; rays/sensors/brain are timed alone over the poses of the 1-thread run's best car.

### Deviations

- None so far. Note for Stage B: the bundled tracks are hand-made and some are shorter than the 20–40 s target
  for *generated* tracks (sprint ≈ 220 road cells); the physics tuning target applies to generated tracks (§3.3).
- Stage B: none. All §3.3 start values and limits are used as written; everything the plan left open is listed
  under "Stage B decisions" above.
- Stage C:
  - **Partial history chunks.** §5.1 says chunks of 25 are append-only and never rewritten. Complete chunks are
    written once and never touched. But an autosave or exit before a chunk is full would otherwise lose up to 24
    generations, so the unfinished tail is written as a shorter chunk (`gen-25-29.bin`); a later save writes the
    longer file (`gen-25-36.bin`, …, finally `gen-25-49.bin`) and only then deletes the shorter one. On load the
    longest file per start wins, so a crash at any point leaves a readable history.
  - **Sync every 60 ticks.** §2.1 says workers sync only to publish a snapshot and at generation end, and also that
    slices are rebalanced from the alive agents every 60 ticks. Rebalancing needs all workers stopped, so a
    headless generation has a short sync point every 60 ticks (one second of game time) too.
  - **Thread count.** §2.1 says "a scheduler thread plus W workers"; here the scheduler thread is one of the W, so
    W = 7 means 7 busy threads, not 8. This keeps the intent of "logical threads − 1" (one thread left for the
    UI/OS).
- Stage D: none.
- Stage E:
  - **Chunks of 4, not 32, and no 64-byte padding of slice data** (§2.1 says "dynamic chunks of 32 agents" and
    "pad slice data to 64 bytes"). With 32, 200 cars made 7 chunks and the tail of a generation (≤ 32 cars
    alive) ran on one thread; chunk 4 balances the load (worst bundled track 2.2–2.6 M → 4.1–4.6 M agent-ticks/s
    at 7 threads). Agents stay in shared struct-of-arrays, so neighbouring chunks share cache lines at their
    edges; measured by the reviewer (192 cars alive, 6 threads, 60 ticks): chunk 32 2.10 ms, 16 2.18 ms, 4
    2.09 ms, 1 3.61 ms — false sharing only shows at chunk 1, so the padding is not needed. Results are
    bit-identical for any chunk size.
  - **Passable = best point in the cell, not the cell centre** (§3.2: "a cell is passable if its clearance ≥
    the car half-width"). Read literally at cell centres it called 2-cell corridors impassable although the car
    drives them (tracks reported unfinishable, invisible shortcuts). Closest to the intent "the car centre can
    be there": the best of the cell's 9 half-cell lattice points.
  - **Reference driver steers on its own wall-penalised copy of the field** (§3.2: "steers down the
    distance-field gradient"). Same graph, passable cells and surface costs; cells near walls cost more, so its
    path keeps off walls the car cannot quite reach. Progress and fitness still use the plain field.
  - **7 bundled tracks, not 5** (M1 row): labyrinth and obstacle_field added so every §9 learn-suite row has a
    track now, hand-made until the generator (M5).
  - **grass_shortcut has a wide grass apron**, beyond the 1–3-cell verges of §7 (a generator rule); the apron is
    what makes it a grass-cutting test. `BundledTracksTests` exempts it from the verge-width check.

## M2 — Viewing

### Stage A — the core view (2026-10-09)

Done: main menu (Watch training · Drive (disabled until Stage B) · Quit), track picker (bundled tracks with a map
preview), training view: map texture, all cars in one MultiMesh, camera (wheel zoom, drag / WASD pan, follow best),
speed controller with interpolation, pause / one tick, HUD. Generations run back to back. New in Sim (all with tests):
`Core/SpeedController` (+ `SpeedMeter`), `Core/SnapshotInterpolation`, `Racing/TrainingHost` (the scheduler thread);
SimBench `view`. 334 tests green (Debug and Release). Every pinned hash is unchanged; SimVersion stays 2 (no sim math
changed).

- **Threads:** `TrainingHost` runs the `GenerationRunner` on its own "Sim scheduler" thread, which is worker 0 of the
  `AgentScheduler` (W threads in total, as in M1). Visual default W = physical cores − 1, min 1 (`View/CpuInfo`: Windows
  `GetLogicalProcessorInformationEx`, Linux `/sys/.../topology`); the laptop gets 3, this box 11.
- **Per frame:** the render thread calls `host.Frame(delta)` (adds `delta × 60 × speed` to ticksDue, wakes the sim
  thread) → copies the snapshot pair → interpolates → fills one pre-allocated `float[]` (16 floats per instance:
  Transform2D, colour, custom) → one `RenderingServer.MultimeshSetBuffer` + `MultimeshSetVisibleInstances`. The sim
  thread runs the due whole ticks within the 14 ms budget, sizing its `RunTicks` pieces from a running estimate of
  the cost per *agent*-tick × alive cars (a per-tick estimate overshot badly at each generation start, when 300 cars
  are alive again after a cheap tail). Measured allocation of the render path: **0 B per frame** (F3 debug rows).
- **Bundled tracks in the export:** the app reads `res://tracks/*.track`. `app/Nitrogenesis.csproj` copies the
  repository's `tracks/` into `app/tracks/` (gitignored) on every build, `build/export-windows.sh` copies them before
  the import, and `export_presets.cfg` has `include_filter="tracks/*.track"`. Checked: the Windows zip run under Wine
  with `Nitrogenesis.exe --headless -- --check-tracks` lists all 7 tracks with their pinned hashes.
- **Fixed: the exported app ran an unoptimized simulation (since M0).** Godot builds the referenced Sim project with
  `Configuration=ExportRelease`, and the .NET SDK only optimizes `Release`, so the shipped `Nitrogenesis.Sim.dll` had
  the JIT optimizer disabled (every method compiled with MinOpts): 5 generations of 300 cars took 4.5 s in the app vs
  1.0 s in SimBench. `src/Sim/Sim.csproj` now optimizes every configuration except Debug; the exported dll reports
  `IsJITOptimizerDisabled = False` and the app matches SimBench (MAX: ~75× → ~400×). (Runs from the editor / `--path app`
  still use the Debug Sim and are ~4.5× slower; measure exported builds.)
- **Dev flags** (debug builds only, after `--`; since Stage C the measuring ones work in release builds too): `--track NAME`, `--picker`, `--speed X|max`, `--threads N`, `--pop N`,
  `--seed S`, `--zoom Z`, `--timings`, `--stats` (prints timings every second), `--screenshot PATH --after S`,
  `--quit-after S`. `--check-tracks` works in every build (the export check above).

**Timings** (this Linux box: 12 cores, shared, load average 5–9 during the runs; s_curve, seed 1):

| Run | Speed | Result |
|---|---|---|
| App, Linux debug export, xvfb + llvmpipe (software GL), 1280×720, 300 cars, 3 threads | 1× | 60 FPS, sim 0.16–0.29 ms/frame, render path 0.05–0.07 ms |
| same | 10× | 60 FPS, actual 10×, sim 0.3–1.0 ms/frame |
| same | **100×** | **60 FPS, actual 100×** (91× in the second with the screenshot), sim 5–6 ms/frame, render path 0.07–0.10 ms, 0 B |
| same | MAX | 58–59 FPS, actual 330–450×, sim 14.8 ms/frame (the budget) |
| same, 200 cars, 11 / 3 / 1 threads | 1× | sim 0.6–0.7 / 0.3 / 0.3 ms/frame (waking 10 helpers costs ~0.3 ms) |
| same, 300 cars, 11 threads | 100× | 60 FPS, actual 99–100×, sim 2.6–3.9 ms/frame |
| SimBench `view` (no renderer, 60 Hz loop), 300 cars, 3 threads, after 30 generations at MAX | 100× | actual 100× on s_curve, hairpins, labyrinth; sim per frame median 2.5–2.7 ms, p95 12–14 ms, max 16–27 ms |
| same | MAX | s_curve 374×, hairpins 417×, labyrinth 600× |

The §9 visual target (300 cars at 100× with 3 sim threads, FPS steady at the 60 cap) holds here, also with a software
renderer that takes CPU from the sim; the laptop (Iris Xe) is the real check. 0.01× is smooth: the interpolated
position of the followed car moves by the same amount every frame across tick boundaries (checked with a per-frame
position print at 0.25× and 0.01×; `SpeedControllerTests.SlowSpeedIsSmooth`). Screenshots: `out/shots/` (menu, picker,
1×, 10×, 100×, MAX).

**Stage A decisions:**

- **Synchronising alpha with the snapshot at 1× and slower.** The sim thread runs asynchronously, so a frame could draw
  the old pair with the new alpha (a visible hitch every tick). At 1× and slower the sim runs one tick per
  `RunTicks` (prev/curr always one tick apart) and the render thread waits for that frame's tick, at most 4 ms
  (`TrainingHost.WaitForFrame`; the work is 0.1–0.7 ms). Above 1× nothing waits and only curr is drawn. Then alpha =
  frac(ticksDue) exactly as §4 says.
- **Backlog cap:** ticksDue holds at most 0.25 s of game time at the target speed (min 2 ticks), so a long frame or a sim
  that cannot keep up never makes it race ahead afterwards; the shortfall shows as the actual speed. The actual speed
  is measured over 0.5 s windows; the HUD shows "100× (actual 64×)" (orange) when it is below 90 % of the target, and
  "MAX (actual 400×)" at MAX. Leaving MAX drops its endless request. No game time piles up while the track is
  prepared (the reference driver runs first, on the sim thread; the HUD says "Preparing the track…").
- **Best car / leader:** the car with the highest live score among those still on the track (running or finished);
  it gets the gold outline (a gold silhouette instance ~0.35 cell bigger, drawn just under it; the leader is drawn
  last, on top) and the camera follows it. **Hidden cars:** crashed, stalled and timed-out cars are not drawn
  (visible instance count); finished cars stay parked in the finish.
- **Camera:** 100 % = 6 px per cell, zoom 25 %…800 % around the cursor; it moves rigidly with the followed car (keeps
  up at 100×), glides when the leader changes, snaps back to the start on a new generation. Dragging (any mouse
  button) or WASD/arrows switch to free; F follows the best again. Keys: − / + (also = and keypad), Space, `.`, F,
  F3 (timings), Esc (menu).
- **Total training time** = real time spent training (not paused), shown h:mm:ss. **Best time** = fastest finish of
  this training (one segment until M3), including live finishes of the running generation.
- **Car colours:** random HSV (S 0.6–0.9, V 0.8–1) per car index, fixed for the view. The car is an 18 × 11 px sprite
  (≈ 6 px per cell, like the map) on a hitbox-sized quad; body pixels are tinted by a small canvas shader.
- **Map colours:** road dark grey, grass green, wall near-black brown, danger orange-red (checkered in two shades),
  finish black/white checkered per cell; a faint fixed per-cell brightness jitter on road/grass/wall.
- **No history yet:** the view's runner does not keep population snapshots (no history store until M3), so no 241 KB
  copy per generation.
- **MultiMesh custom bounds:** the buffer is pushed straight to the RenderingServer, so Godot kept the bounds of the
  first (empty) buffer and culled every car; the MultiMesh gets a custom AABB covering the map.
- **HUD text** is refreshed 10 times per second (string formatting allocates a little; it is outside the render path).

### Stage B — interaction and the player car (2026-10-09)

Done: click a car → the camera follows it, it gets a light-blue outline, its sensor rays and its route this
generation are drawn; camera modes best / selected / free (HUD row "Camera"); **Drive** in the menu → track picker →
drive one car yourself (keyboard or gamepad → the same `CarPhysics`): 3-2-1 countdown, live timer, finish time,
best time, progress, R = restart, Esc / Menu button = back to the menu; the player's car is white with an orange
outline and draws its route. Esc or the new "Menu (Esc)" button leaves the training view; the simulation threads are
joined when the view is freed (debug log: "simulation stopped in 0.9–10 ms"). F3 now shows the main thread's bytes
per frame and the garbage collections. New in Sim (all with tests): `Racing/RouteTrails` (every car's route,
written by `RacingMode` when switched on), `Sensors.CastRays` / `RayAngle` / `RayCount` / `Range`,
`Racing/PlayerControls` (0.1 s key ramp, gamepad dead zones), `Racing/PlayerRun` (one car: countdown, timer, finish,
crash, restart, route), `TrainingHost.Watch` / `CopyTrail`, `ReferenceDriver.CreatePilot` (public, for the debug
autopilot and a test). 348 tests green (Debug and Release); every pinned hash unchanged (a new test runs the pinned trajectory with
trails on); SimVersion stays 2.

- **Rays** are cast on the render thread with the training's own `Sensors` (immutable, allocation-free) at the car's
  drawn pose: `Sensors.CastRays` is the code `Sensors.Write` uses, and a test checks its values × 1/range equal the
  brain's ray inputs bit for bit. (The inputs array itself is written by the workers mid-step, so the render thread
  must not read it.) Drawing: a thin white line to the first wall/danger (or the range), a yellow tick where the ray
  first leaves the road, a red tick at the hit.
- **Route trail:** `RacingMode` records every car's centre every `Stride` ticks into `RouteTrails` (stride = the
  smallest power of two that keeps a generation within 2048 samples per car: 1 up to a 34 s limit, 2 up to 68 s,
  … 128 at the 3600 s maximum; 300 cars with a 60 s limit ≈ 4.3 MB). So a car clicked late still shows its whole
  route since the start, also at 100×. Only the view's host switches it on; headless training keeps none (SimBench
  `bench` and `view` unchanged within noise). The scheduler thread copies the watched car's route after each frame
  (workers idle); a new watched car is copied at the next frame even while paused.
- **Selection rules:** a click is a left press + release that moves < 5 px (more is a drag, which pans and switches
  to free). It picks the nearest drawn car within its half-length + 0.5 cell (at least 12 px when zoomed far out);
  a click on empty road changes nothing. A selected car belongs to its generation (car i is another genome in the
  next one): when the generation ends, the selection is dropped and the camera goes back to the best car. If the
  selected car crashes or stalls it disappears like the others, the camera stays where it stopped and its route
  stays drawn (HUD: "car 17 (stalled)"). F = follow the best car again (drops the selection). In free mode a
  selected car keeps its rays and route. Following best shows the leader's rays and route.
- **Outlines:** best car gold (as in Stage A); selected car light blue, one size wider, so a selected best car shows
  both; player's car orange (the accent).
- **Player controls** (`PlayerControls`): keys set a target of −1/0/+1 per axis and the output moves towards it at
  1/0.1 s (6 ticks from 0 to full, 12 ticks from full left to full right). Gamepad (device 0): left stick steers
  (dead zone 0.2), right trigger throttle and left trigger brake/reverse (dead zone 0.05, throttle = RT − LT);
  outside its dead zone an analog axis is used directly and wins over the keys. WASD are read by physical
  position (AZERTY users get ZQSD), arrows by key. Gamepad Y or Start = restart, Back = menu (also in training).
- **Player run** (`PlayerRun`): same physics and finish rule as a training car (finish time at sub-tick precision),
  checked by a test: driven by the reference driver's control law it finishes in exactly the reference time
  (sprint, s_curve; in the app the debug autopilot finishes s_curve in 16.59 s = the reference). No time limit and
  no stall stop for the player. Countdown 3 s; inputs held during the countdown are already ramped at "GO". The
  route keeps a point per tick, halving (every second point, stride ×2) whenever 4096 points are used. Physics:
  default racing settings (a ghost's segment physics comes with M6). Camera zoom 200 %, rigidly on the car; the
  wheel zooms. Frame loop: one device read per frame, the 1× `SpeedController` gives the due ticks, the car is drawn
  between the last two ticks (same interpolation as training).
- **Allocation check (F3):** `View/FrameAllocMeter` takes `GC.GetAllocatedBytesForCurrentThread` from one frame's end
  to the next, so it covers the whole main-thread frame (the view, `_Draw` callbacks, input handlers, other nodes),
  not only the render path; the row shows the largest frame of the last 0.1 s, green at 0. Taken out on purpose: the
  HUD text refresh (10/s in training, ~1.1 KB each, shown on its own row) and the debug command line's own work.
  The GC row counts collections from the first generation on (the set-up allocates). The trail is one
  `DrawPolyline` per frame from pre-allocated power-of-two arrays (tail padded with the last point), so `_Draw`
  allocates nothing.

**Checks** (this Linux box, shared, load average 5–9; xvfb + llvmpipe software GL, 1280×720):

| Run | Result |
|---|---|
| Linux debug export, s_curve, 300 cars, 3 threads, 1×, following a selected car (route + rays) | 58–60 FPS, sim 0.23–0.43 ms/frame, render path 0.07–0.09 ms, **0 B every frame**, 0 collections |
| same, 100×, following the best car (route + rays) | 58–60 FPS in the quieter run (35–58 in a second one with the box busier), actual 100×, sim 4.5–7 ms/frame, render path 0.07–0.10 ms, **0 B every frame**, 0 collections |
| `--path app` (Debug Sim), s_curve 200 cars 1×, with and without a selected car, 12 s each, twice | same FPS either way (57–60 after the first second): the route and rays cost nothing visible |
| Drive, s_curve, debug autopilot | 60 FPS, finish 16.59 s (the reference time), 0 B per frame apart from HUD text and screenshots |
| Drive, sprint, keys injected through Godot's input (`--press`) | throttle ramps to 1, steer passes 0.33 on the way, S brakes and reverses (−3.4 cells/s), R restarts (attempt 2, 1 s countdown), arrows drive |
| Menu → Drive → picker → Start → Esc → menu, by injected keys | works; Esc in training: menu, simulation joined in 0.9 ms |
| SimBench `bench` s_curve 3 × 3 s, before / after | 1 thread 1.24 / 1.19 M, 300 cars 7 threads 4.24 / 4.44 M agent-ticks/s (noise) |
| SimBench `view` 100×, 300 cars, 3 threads, before / after (trails on) | actual 100× both, sim median 2.0–2.5 / 1.8–2.3 ms per frame |
| Windows export (`build/export-windows.sh`) under Wine | tests green; `--headless -- --check-tracks` lists the 7 tracks with their pinned hashes; `--headless --quit-after 120` runs and exits without errors |

Screenshots: `out/shots-m2b/` (follow + rays at 1× and 100×, drive countdown / driving / finish / keys, picker,
menu after Esc).

**New debug flags** (debug builds, after `--`): `--drive NAME` (drive at once), `--autopilot` (the reference
driver's control law drives the player car), `--select N` (follow car N), `--click-car N@T` (a real left click on car
N at T s), `--press KEY@A-B,…` (hold keys through Godot's input), `--screenshot PATH@T,PATH@T…` (several shots;
`--screenshot PATH --after S` still works). `--stats` prints allocations per second and, in drive, phase/time/input.

**Also fixed:** `TrainingHostTests.TenXRunsTenTicksPerFrame` failed once in a full test run on the busy box (51 of 60
ticks: a frame's work overran the 14 ms budget, which correctly leaves the rest due). The test now lets frames
without new game time run the rest and checks that exactly 60 ticks were due.

### Stage C — review fixes (2026-10-09)

A review of Stage A + B found ten problems; all are fixed. New / changed tests: `SpeedControllerTests`
(pause drops the backlog, alpha never goes back before the ticks are consumed, the meter reads on-time slow speeds
as on time, a new target starts a fresh window), `TrainingHostTests` (pause at a falling-behind 100× runs no
backlog, then Step runs exactly one tick; leader hysteresis), `SnapshotInterpolationTests` (a pair 19 ticks apart
is drawn at curr). 364 tests green (Debug and Release). Every pinned hash unchanged; SimVersion stays 2 (no sim math changed).

- **Pause stopped nothing the sim already owed (high).** At 100× falling behind, up to 1500 owed ticks kept running
  after Space, and `.` then ran the whole rest. `SpeedController.Paused` now drops the whole ticks still owed and
  keeps only the render fraction; a tick already running when Space is pressed keeps the fraction too
  (`Consume` of more than was due). Step = exactly one tick.
- **Outlines were not solid (medium).** In a Godot 4 canvas `fragment()`, COLOR already carries the texel, so the gold
  / light-blue silhouettes drew the sprite's black rim and tyres and the roof tint was applied twice. The shader now
  takes the instance colour in `vertex()` (a varying): the best car has a solid gold outline, the selected car a
  solid light-blue one, the roof is the intended 0.78 shade (`out/shots-m2c/z8-best.png`, `z8-sel.png`).
- **The laptop check was impossible with the release build (medium).** Release builds now keep the measuring flags
  (`--track`, `--drive`, `--picker`, `--pop`, `--speed`, `--threads`, `--seed`, `--zoom`, `--timings`, `--stats`,
  `--select`, `--quit-after`); only the input-injecting and file-writing ones stay debug-only (`--click-car`, `--press`,
  `--autopilot`, `--screenshot`). The zip now also has `SimBench/SimBench.exe` (self-contained single file, ~37 MB,
  with the bundled tracks in `SimBench/tracks/`), so the M1 bench targets can be re-measured on the laptop. It prints
  UTF-8 (× and — in a Windows console).
- **Backward steps at 1× and slower when the 4 ms wait timed out (medium).** Alpha is now `ticksDue` clamped to 0…1:
  while a whole tick is still owed the pair is stale and curr is drawn (alpha 1), instead of wrapping to the small
  fraction and drawing the car almost a tick back. Per-frame trace at 0.25× (s_curve, 300 cars): largest step of the
  followed car 0.075 cells vs a median 0.050 (no backward or double steps).
- **Pause / Step / speed slider buttons (medium, PLAN §6.2).** The training HUD has Pause/Resume, Step (enabled while
  paused) and a 13-step speed slider (0.01× … MAX) with its label. They take no keyboard focus (Space and `.` still
  reach the view) and the slider ignores the wheel (which zooms). A status line ("Running" / "Paused") is always
  shown, so pausing does not move the buttons under the cursor. Gamepad in training: LB / RB speed, A pause, X step,
  Back menu (second hint line). Checked with injected clicks: Pause → paused, Step → one tick, slider → 100×.
- **Pairs many ticks apart were interpolated (low).** `SnapshotInterpolation` lerps only when curr is exactly one tick
  after prev; otherwise curr is drawn.
- **False "can't keep up" at 0.05× (low).** A `SpeedMeter` window now lasts at least 0.5 s and at least 20 ticks at the
  target speed (0.05×: 6.7 s, 0.01×: 33 s), and a new target speed starts a fresh window (no mixed 1×/100× reading
  after a change). A test drives the meter at a perfect 60 FPS for 120 s at 0.01× … 100×: never below 90 %.
- **Start marker (low).** The map texture paints the start as an arrow in the accent colour (about 4 × 5 cells, cells
  at least half inside it), pointing along the start heading: in the picker preview, training and drive.
- **Follow-best lagged at 100× (low).** Two changes: the leader changes only when another car leads by more than
  1 % of the track (`TrainingHost.LeaderMargin`, or the leader leaves the track), and when the followed car changes
  the camera already moves with the new car while it glides over. Hairpins, 300 cars, 3 threads, 100×, 13 s
  (`--path app`, Debug Sim): leader switches 120 → 48, frames with the leader more than 20 cells from the screen
  centre 7.8 % → 2.0 % (the rest are a crashed leader handing over to a car far behind).
- **Stale tracks shipped (low).** `export-windows.sh` recreates `app/tracks/` from scratch; the csproj target deletes
  `app/tracks/*.track` files that are no longer in `tracks/` before copying (checked with a dummy file).

**Checks (Stage C)** (this Linux box, shared):

| Run | Result |
|---|---|
| `build/export-windows.sh` | green (tests 364/364), zip 103 MB with `SimBench/` |
| Windows release exe under Wine, `--headless -- --check-tracks` | the 7 tracks with their pinned hashes |
| same, `--headless -- --track s_curve --pop 300 --speed 100 --threads 3 --stats --quit-after 20` (release keeps the measuring flags) | 60 FPS, actual 100×, sim 3.3–6.6 ms/frame, 0 B per frame, 0 collections, generation 45 after 19 s |
| `SimBench\SimBench.exe view --seconds 5` from the zip, under Wine | actual 100×, sim median 2.0 ms, p95 14 ms, 0 late frames |
| `--path app`, hairpins, 300 cars, injected clicks on Pause, Step, slider | paused, one tick, 100× (`out/shots-m2c/buttons.png`) |
| `--path app`, s_curve, 0.25×, per-frame position of the followed car | steps 0.050 median, 0.075 max: no backward or double steps |

Screenshots: `out/shots-m2c/` (1× with HUD controls, paused, after clicking Pause / Step / slider, 100×, 800 % best and
selected outlines, picker with the start arrow, drive).

### Deviations

- Stage A:
  - **Render thread waits up to 4 ms at 1× and slower** (§4: "It never blocks the UI"). See the decision above; it is
    bounded, only happens when a tick is due at those speeds, and makes slow motion smooth instead of hitching.
- Stage B:
  - **R restarts with a 1-second countdown** (§6.5: "instant-restart key (R)"). R puts the car back at once, from any
    state, without a menu; a short "1" before "GO" lets the timer start fairly instead of on the key press. The first
    start counts down from 3.
  - **The zero-allocation check leaves out the HUD text** (§9: "zero-allocation check in a debug overlay"). The HUD
    strings are rebuilt 10 times a second in training (every frame in drive, for the running timer); C# strings
    cannot be refilled in place. That is ~1.1 KB × 10/s, measured and shown on its own row; everything else on the
    main thread is 0 B per frame. Input events also allocate (Godot creates a managed object for each event it hands
    to a C# input callback, and a handler's first run sets up runtime data), so frames with mouse movement, clicks
    or key presses show some bytes (a first click: ~15 KB); watching without touching anything shows 0.
- Stage C:
  - **The gold outline is on the leading car with a 1 % hysteresis** (§6.2: "the best car has a gold outline"). A car
    within 1 % of the track of the best score keeps the gold outline (and the camera) until another car leads by
    more, or it leaves the track; otherwise the outline and the camera flicked between level cars many times a
    second at 100×. Scores, selection and the "Best now" row are unchanged (exact).

**What to try (M2):** unzip, run Nitrogenesis.exe.

1. **Watch training** → pick a track → Start. Cars train generation after generation.
2. − / + or the slider change speed (HUD: target and actual), Space or Pause pauses, `.` or Step runs one tick while
   paused, wheel zooms, drag or WASD/arrows pan (camera "free"), F follows the best car again. A gamepad: LB / RB
   speed, A pause, X step. The orange arrow on the road is the start.
3. **Click a car**: the camera follows it (light-blue outline, HUD "Camera: car N"), its 7 rays show what it senses
   (yellow tick = road ends, red tick = wall), and a line in its colour shows its route since the generation
   started. When it crashes the line stays; at the next generation the camera returns to the best car. Following
   the best car also shows its rays and route.
4. **F3**: the "Alloc" row should read **0 B / frame** in green at 1× and at 100× while you do not touch the mouse;
   "GC" should stay at 0 for minutes. Sim/render timings are there too.
5. **The M2 acceptance on the laptop:** in a terminal in the unzipped folder run
   `Nitrogenesis.exe -- --track s_curve --pop 300 --speed 100 --threads 3 --timings` (the debug rows are on; F3
   toggles them). After a few generations: FPS ≈ 60 (cap 60) and the speed line white "100×" (orange "100× (actual
   64×)" = it cannot keep up). Also try `--speed 0.01`: the cars creep smoothly.
   **The M1 bench on the laptop:** `SimBench\SimBench.exe bench --seconds 5 --repeats 3` (target ≥ 2.5 M agent-ticks/s
   with 7 threads; a few minutes) and `SimBench\SimBench.exe view` (the training view's simulation at 100×, 300 cars, 3 threads, no
   rendering). Send me the last lines.
6. Esc (or "Menu (Esc)" top right) → back to the menu; start another training: no slowdown from the old one.
7. **Drive** → pick a track → 3-2-1-GO. W/S or ↑/↓ throttle and brake/reverse, A/D or ←/→ steer; a gamepad works
   too (left stick, right trigger = gas, left trigger = brake, Y = restart, Back = menu). The timer runs live; at the
   finish the time shows big, with your best below it. Touch the orange danger cells to see "CRASHED". R restarts
   (1 s countdown), the wheel zooms, Esc goes back to the menu.

## M3 — Training control

### Stage A — Sim-side data layer (2026-10-09)

Done (all in `src/Sim`, with tests): `History/` — `HistoryEntry` (a stored generation: the record with its recording kept
encoded), `HistoryStore` (memory-capped, thinning), `Session` (segments, branches, active branch, current population,
evolution settings, lineage, save planning), `Rebuilder` (any generation from the nearest earlier population),
`SessionSaver` (background save thread), `TrainToCondition`, `ForkKind`; `Io/` — `SessionFolder` rewritten (save plans,
backup header, damaged-file tolerance, crash-injection hook, lock file), `SessionHeader` (M3 fields), `HistoryChunk`
format 2 (ticks + CRC-32), `SessionStore` (list / create / rename / delete / latest), `AppPaths`, `Crc32`, `SavePlan`;
`Racing/TrainingHost` reworked on top of a session; `RacingTraining` from a stored config / `ResumeFrom(session)`;
`GenerationRunner` (`EndGeneration(breedWith)`, `SetThreads`, `RecordBestCar`, `PopulationParams`, record `Ticks` and
encoded recording); `Breeder.FromBest`. SimBench `kill-test` (+ its child `save-stress`) and `headless`.
429 tests green (Debug and Release; 364 before). Every pinned hash unchanged; SimVersion stays 2 (no sim math
changed). The app is unchanged apart from compiling against the new host (no M3 UI yet).

- **History in memory:** each generation keeps seed, effective params, stats, ticks run, best genome, the best car's
  recording (encoded: 2–5 KB instead of 10 bytes per tick in memory) and the full-population snapshot until thinned.
  Measured encoded recordings: sprint 1.9 KB, hairpins 3.1–3.7 KB, labyrinth 2.8–4.7 KB, obstacle_field 1.4–3.3 KB
  (0.2–0.5 ms to encode, done on the worker that records the best car while the main thread breeds).
- **Thinning** (history memory limit, default 1 GB, `TrainingHostOptions.HistoryMemoryLimit` until M7): level 1 keeps
  the snapshots of generations divisible by 5 and drops the others oldest first, only as many as needed; when only
  multiples of 5 are left, level 2 keeps every 10th, then 20th, 40th… Best genome, recording and stats are never
  dropped. In steady state recent generations keep every 5th snapshot and old ones get sparser. Per-level "clean below"
  marks keep a thinning pass at one step per generation (2000 thinnings over 20 000 snapshots: 78 ms in all).
- **Rebuild** (`Rebuilder`): walks back the lineage to the nearest snapshot, the current population or a branch's
  first population (generation 0 from the seed; best-only fork from the parent's best genome; a full fork continues in
  the parent), then re-runs each generation under its own segment's config for exactly the ticks it ran and breeds
  with the stored seed and params. Every re-run generation is compared with its record (stats, best genome, ticks,
  bit for bit); a difference throws instead of returning another population. Tests: equal to the original snapshot
  bit for bit after thinning, across a segment change, through generations ended early, through full and best-only
  forks and a fork of a fork of a fork. **M3 acceptance "fork from a thinned generation works"**:
  `RebuildTests.FullForkFromAThinnedGenerationStartsFromItsExactPopulation` (and through the host:
  `TrainingHostSessionTests.ForkFromAThinnedGenerationAndSwitchBackThroughTheHost`).
- **Saving** (§5.1): every file goes temp → flush to disk → move; header.json is the commit point, written last with
  `File.Replace` keeping the previous one as `header.json.bak`; it lists each branch's history end, and generations at
  or after it are ignored on load. Chunk window w (generations 25w…25w+24) is written as one file per consecutive run
  of its records; a save writes only windows with new generations or thinned snapshots, and after writing deletes the
  window's files it supersedes. Autosave every 2 min (`AutosaveInterval`) and on exit; the training thread only copies
  (header + current population + references to the immutable entries), the saver thread writes.
- **Crash safety, simulated** (`SessionCrashTests`): a save with new chunks in several windows, thinned windows
  rewritten over saved ones, superseded partial chunks deleted, a branch switch, a new segment and a new fork is cut at
  each of its 26 steps (temp file created, half written, fully written, moved, deleted, header): every time the
  session loads as exactly the previous save (before the header move) or the new one (after), without warnings, every
  snapshot still held equals its original, and it resumes, trains and saves again. Also: damaged header → previous
  save (and the damaged one never becomes the backup); a header that contradicts itself → previous save; a damaged
  chunk → a reported gap, the rest loads, training goes on, the damaged file is kept; leftovers of a fork whose header
  never committed are cleared on its first save; a second window cannot open a locked session.
- **Crash safety, real kill** (`SimBench kill-test`, **M3 acceptance "kill the app mid-autosave → it resumes"**): a
  child process trains sprint headless (40 cars, 2 threads) with autosave interval 0, a 3 MB history limit (constant
  thinning and chunk rewrites) and forks at fixed points; the parent kills it at random moments, loads the session
  (must have no warnings, no gaps, and rebuild a random thinned generation), and the next child resumes it. At the
  end one child runs to the target and must equal an uninterrupted run in the parent.

  | Run | Kills | Inside a save | Failed loads | Final run identical |
  |---|---|---|---|---|
  | Linux, `kill-test --rounds 30 --gens 400` | 26 (the child finished) | 15 (half-written chunk, half-written header, after a chunk move, between deletes) | 0 | yes (generation 400, 10 branches) |
  | Windows runtime (self-contained SimBench.exe) under Wine 10.0, `DOTNET_EnableAVX=0`, `--rounds 20 --gens 250` | 20 | 8 | 0 | yes (generation 250, 7 branches) |

- **Wine finding (not an M3 bug):** under Wine 10.0 the runner's determinism self-check ("Re-simulating agent N gave
  score …: the simulation is not deterministic") fails within 5–100 generations whenever garbage collections are
  frequent: with saving on, and equally with a thread that only allocates garbage and no saving at all (child switches
  `NG_STRESS_GARBAGE`, `NG_STRESS_NOSAVE`, `NG_STRESS_NOFORK`). With the JIT's AVX off (`DOTNET_EnableAVX=0`) it never
  happens (6 runs of 300 generations), and on Linux the same stress passes with identical results. So Wine does not
  keep the AVX register state when the .NET GC suspends a thread mid-computation; real Windows saves it. M1/M2's Wine
  checks passed because they made few collections. Consequence for M8: the Wine self-test must run with
  `DOTNET_EnableAVX=0`; the laptop (real Windows) re-check is `SimBench\SimBench.exe kill-test`.
- **Speed** (Linux box, Release, s_curve, 200 cars, 7 threads, 150 generations, box load ~16):
  `SimBench train` (runner only) 24.1 gens/s; `SimBench headless` (through the host, with history) 20.9–22.6 gens/s;
  the same saving continuously (autosave interval 0, the worst case) 19.4–20.0 gens/s.
- **App check** (Windows release build under Wine, `--headless -- --track s_curve --pop 300 --speed 100 --threads 3
  --stats --quit-after 40`, AVX on and off): 60 FPS, actual 100×, sim 4–6 ms/frame (M2: 3.3–6.6), render path
  0.05–0.09 ms, **0 B per frame**, 86 generations; GC count 2 in 40 s (M2: 0): the history now keeps one ~360 KB
  population copy per generation (background GC, no FPS dip seen).
- `build/export-windows.sh` green (429 tests), zip 103 MB (it overwrote the local `out/Nitrogenesis-0.2.0-win64.zip`,
  VERSION is still 0.2.0).

**Stage A decisions:**

- **Sessions location:** `AppPaths.DataRoot` = `Environment.SpecialFolder.ApplicationData` + `Nitrogenesis`, i.e.
  `%APPDATA%\Nitrogenesis\` on Windows (PLAN §5.1), not Godot's `user://` (that would be
  `%APPDATA%\Godot\app_userdata\Nitrogenesis`). Sessions in `sessions\`, tracks in `tracks\`, settings in `settings.json`.
  The environment variable `NITROGENESIS_DATA` overrides the root (tests, measuring runs).
- **Session name = folder name**, made safe (letters of any language, digits, spaces and `-_()[],.+'&!#`; others
  become `_`; no trailing dots/spaces; reserved device names get a `_` prefix; ≤ 60 characters) and unique ("name (2)").
  Rename = folder move (a case-only rename goes through a temporary name). Default name "<track> yyyy-MM-dd HH.mm".
  **Continue** = the openable session with the newest `savedAtUtc`; damaged ones are listed after the others.
- **Header** stays format 1; M3 adds optional fields (`savedAtUtc`, `trainingSeconds`, `totalTicks`, per branch
  `historyEnd`, `kind` root/full/best, `forkParams`, per segment `populationSize`, population `weightsCrc32`).
  **History chunks** are format 2: + the ticks the generation ran, + a CRC-32 trailer; format 1 still loads (ticks
  unknown = run to the end).
- **A new session is saved before its first generation runs** (so it is in the Sessions list at once); the exit save
  waits for the saver. **Lock:** the host holds `session.lock` open with no sharing while the session is trained.
- **Read-only sessions** (other SimVersion): load with their recordings and stats; training, forks, rebuilds and
  saving are refused with the §5.1 message; the host does not accept them.
- **Segments:** each branch has a segment at its first generation; `TrainingHost.ChangeSettings` applies at the next
  generation boundary and starts a segment only when the SimConfig hash changes (a change of the brain's inputs —
  rays, memory inputs, hint — is refused: "new training only", §6.4). Best time, stagnation and the graph baseline run
  over the contiguous generations of the lineage with the same config hash (`Session.SegmentRunStart`), so changing a
  setting and back starts a third run, and a fork continues its parent's run. The stagnation tracker is rebuilt by
  replaying that run's stats (as in M1); a resumed session continues bit-identically (tested with boosts).
- **Population size stays fixed per session in M3**; segments carry it (`populationSize`) for M7's "trim worst / fill
  with mutants".
- **Fork from generation X** (an evaluated generation): the new branch starts at X with that full population and its
  own seed (derived from the root seed and the branch count) and evaluates X again (same result as the parent's X),
  then breeds X+1 with its own seed and the current evolution settings. **Fork from best only:** child 0 is the best
  genome unchanged, the others are copies mutated with the current mutation chance and σ (no boost), each from its own
  stream under the branch's generation-X seed. Branch names fork-1, fork-2… (the branch count at creation).
- **Switching branch** continues the branch from its last stored generation: that generation is evaluated again (it
  must match its record bit for bit and is not stored twice), then the next is bred with the current settings.
  Only the active branch's next population is stored in the header (PLAN §5.1 "the current population").
- **Train to…:** the running generation continues without visuals; the condition is checked after each generation
  that ends during the run ("to generation N" = N is the next to run; "until a car finishes" / "best time < X s" = the
  generation that just ended). Afterwards the view continues at its chosen speed without owed game time.
  `TrainingStats` has `Headless`, `TrainTo`, `GenerationsPerSecond` (≥ 1 s windows), `TrainToRuns`.
- **Step mode** pauses right after a generation ends with the next one already set up at the start (its summary in
  `LastGeneration`/`LastStats`); "Next" unpauses. **Next generation** ends the running one now (also while paused);
  its record keeps the ticks it ran so a rebuild repeats exactly that.
- **Requests from the UI** (fork, switch, train-to, next generation, evolution settings) are queued and run on the
  scheduler thread between simulation steps. One refused before it changed anything leaves `Notice`; a failure after
  it changed the session stops the training with `Error` (the exit save still writes the consistent session).
- **Population copies are not pooled** (M1 noted it for M3): a save in progress may still hold a thinned snapshot, so
  recycling its array could put another generation's genomes into a save. Measured cost: 2 GCs in 40 s at 100×, the
  render path stays at 0 B per frame.
- **Thinning "oldest"** = the lower generation number across all branches; ties go to the older branch.
- **Stagnation replay on resume** uses the current evolution settings for the whole segment run (the stored params do
  not hold the stagnation window or the boost switch). Exact unless those two were changed during the run; then only
  the timing of the next boost can differ from an uninterrupted run.

**Stage A review (2026-10-10, after the cut-off).** The cut-off run had finished the stage; the review kept all of it
and fixed five gaps (each with a test that fails without the fix; 432 tests green):
- A snapshot thinned while a save was still writing its window stayed on disk (the window was not marked for
  rewriting, because its generations were not "saved" yet): `Session` now also remembers the ends of the last planned
  save. Harmless for loading, but disk use grew past the memory limit.
- After loading the backup header (header.json damaged), only the same `SessionFolder` object knew to delete the
  damaged header before the next save; another object (e.g. one made again from `SessionStore.Folder`) would have
  moved the damaged header over the good backup. The save plan now carries it (`SavePlan.ReplacesDamagedHeader`).
- A fork refused or cancelled (generation not evaluated, rebuild cancelled) had already applied the evolution settings
  passed with it, to the session only (not the runner). They now apply only once the fork is made.
- Rename and delete of a session that a training host has open are refused (`SessionLockedException`): on Windows a
  recursive delete would have removed part of the folder before failing at the lock file, and on Linux a rename would
  have let the host write a fresh, partial folder under the old name.
- The saver thread reports any exception as a failed save (it caught only I/O-type ones; anything else would have
  ended the app). A resumed host also thins at once when its history limit is lower than the loaded history.

Re-checked after the fixes: `SimBench kill-test --rounds 15 --gens 250` (Linux, Release): 15 kills, 6 inside a save,
0 failed loads, final run identical (generation 250, 7 branches). `build/export-windows.sh` green (432 tests). The
Windows build under Wine (`--headless -- --track s_curve --pop 300 --speed 100 --threads 3 --stats --quit-after 20`):
60 FPS, actual 100×, sim 5.2–6.6 ms/frame, **0 B per frame**, generation 45 after 19 s.

**For Stage B (app):** set the process priority to Below normal while `TrainingStats.Headless` (PLAN §4) and back
after; open sessions through `SessionStore` (`Folder`, `Session.Load`, then `TrainingHost(session, options with
Folder)`); the history list, graph and branch list read the session under `Session.SyncRoot` (`LineageEntries`,
`SegmentRunStart`, `Segments`, `Branches`), never the current population's weights.

### Stage B — the app (2026-10-10)

- **01 Menu and Sessions:** the menu, Sessions list, Modal and the open/continue flow already existed and worked; the only change is Rename. It now checks the name first and says why: empty, characters not allowed (the old code silently turned them into "_"), or "a session named X already exists" (the old code silently made "X (2)"). A letter-case-only change of the own name is allowed. A session held by another Nitrogenesis window refuses Rename, Delete and Open with "open in another Nitrogenesis window" and stays untouched. Inside one window an open session cannot be reached from the Sessions list (leaving the training view saves and releases the lock first).
- 02 (graph): GraphView series now drawn with antialiased polylines (the aliased ones showed tick marks and a broken look at width 1.2); ScoreGraph got tests/Sim.Tests/ScoreGraphTests.cs (11 tests).
- **03 Train without visuals (progress screen):**
  - `ProgressScreen` (generation N / target bar, generations/s, time, best time, live graph, Cancel/Esc, footer with thread
    count and priority) and `TrainingView.StartTrainTo/LeaveProgress` already worked; checked: train-to 30 stops with the
    view at generation 30; `trainto-finish` stops after the first generation with a finisher (hairpins: gen 1, graph shows
    best 0.98 -> 2.6); `trainto-time:12.8` on sprint (seed 1) stopped at generation 12; `cancel` returns to the view and
    the generation goes on; closing/quitting during train-to saves the session (`--continue` showed all 25 generations).
  - `CpuPriority` is Windows only (`Process.PriorityClass`). Linux is left alone on purpose: setpriority can lower but a
    normal user cannot raise it back. It now logs every change with `GD.Print` ("process priority: Normal -> BelowNormal",
    "restored to Normal"), remembers the original class only after the change worked, and keeps it when a restore fails so
    the next `Restore()` retries. Every exit from the progress state calls `Restore()`: finish and cancel (host stops, the
    view sees `Headless` false), error (`Error` set), and app exit/window close (`_ExitTree` -> `LeaveProgress`).
  - Not measurable here: the Windows priority itself (no Windows); gens/s on this box is 1.5-7 on sprint with 200 cars
    because the machine was at load ~20 on 12 cores.
- **04 Replay views:** `ReplayView` (Watch best = `RecordingPlayback`, Watch whole generation = `GenerationReplay`) was
  already complete and correct; only tests were added (`RecordingPlaybackTests`, `GenerationReplayTests`: playback
  reproduces every sample and stops at the last, restart/pause/step, MAX not offered; a re-simulated generation
  matches its stored stats, also after thinning and after Restart; SimVersion mismatch refuses, recordings still play).
  Watch best offers 0.01x-100x (no MAX); whole generation has the full training steps.
  Verified: best of generation 12 finishes at 12.78 s (= history row), whole generation 12 ends "same result as
  recorded", fingerprint unchanged after Back (also with training paused), keep-training goes on headless.
- **05 Training controls:** Mode Auto/Step, StepSummary + Next, Next generation, Train to… dialog, Replay best, stagnation banner and badge, mutation slider with the pending note, the "saved" indicator and the small/large graph already existed and worked. Checked with scripts (sprint, 100 cars, seed 1): Step mode over 4 generations advanced the number by exactly 1 per Next and the summary showed that generation's stats; Next generation at tick ~770 of 1916 stored generation 0 with 972 ticks (not 1916); the slider set to ×2 during generation 1 showed "applies from generation 2" until it began, and the stored record of generation 2 has σ 0.40 / chance 0.20; Train to… dialog (all three conditions are in the dialog; "until a car finishes" started and returned); Replay best opened generation N-1 and Back returned; the boost banner and BOOST ACTIVE badge appeared after generation 59; a save made to fail (folder read-only) shows "save failed · training goes on" in red plus a 12 s banner with the reason (training continued; after the folder was writable again the next save cleared it and showed "saved HH:mm"); the small graph click opens the large one and Esc, G or a click closes it (real mouse events pushed through the viewport). 0 B/frame: `--stats` at 1× and 100× idle, in Step mode with the summary shown, with the large graph opened/closed and after a slider change. Gap-fills: the save failure also raises a banner (before only the corner text and a tooltip), the summary row "Bred with" is now "Next bred with" (it shows how the waiting generation was bred), the boost banner text is shorter (it overlapped the HUD panel). New debug `--do` verbs in TrainingView.DebugDo: `until-waiting`, `until-gen:N`, `until-tick:N` (the next step waits), `state` (prints stats and the last stored generations with ticks and σ), `click-graph[:big]`, `click-button:LABEL` (a real mouse click at a visible button), `key:NAME`; `mutation:F` now moves the slider as a user would.

- **06 Training panel:** the panel (History / Branches / Segments tabs), fork dialog, branch switch with rebuild progress and Cancel, and the read-only HistoryScreen already existed; checked with scripts (sprint, seed 1, 60 cars): train 30, fork at 10 (full), fork at 20 (best only) from the fork, switch main / fork-1 / fork-2 back and forth: each branch resumes at its last stored generation (re-run, not stored twice), generation numbers and branch ranges right, fingerprint identical after quit -> `--continue`. Fork from a thinned generation (`--history-mb 1`, 150-300 cars): the dialog says "N generations to re-run", the rebuild shows progress, Cancel leaves the old branch active with an identical fingerprint, the finished fork continues. History list with 420 generations: virtual rows, wheel and scrollbar, jumps to the oldest/newest; `--stats` with the panel open idle: 0 B/frame (the refresh returns early when the session version did not change). Read-only session (header simVersion 1): message shown once, Watch best works, Whole gen./Fork/Switch disabled.
  - **Sim bug found and fixed:** a fork that was made active and left again before it finished a generation could not be switched back to ("Generation N of 'fork-1' has not been evaluated"): `Rebuilder.MakePlan` now accepts a branch's first generation without a record (its first population follows from the fork). Test: `AForkLeftBeforeItsFirstGenerationEndedCanBeSwitchedBackTo` (full and best-only).
  - After a fork or switch the HUD's last-generation summary and boost marker belonged to the other branch (`TrainingHost` now resets `LastGeneration`/`BoostStartedAt`). Host refusals with a notice: fork or switch while training without visuals runs, switch to the active branch (tests added). The view also refuses with a banner (`WhyNotNow`): error, rebuild running, train-to/background training, not ready.
  - Panel: 580 px wide, row buttons "Watch best / Whole gen. / Fork…", only the shown tab is rebuilt on refresh, fork entries in the Segments tab are a one-line note ("fork-1 starts at generation 10: same settings") instead of a fake segment, separators between real segments, panel sits under the rebuild box and the enlarged graph. The rebuild text for "Best car only" no longer claims a rebuild.
  - HistoryScreen: the older-physics message is shown once (not also inside the panel).
  - Debug verbs: `until-*` waits are now one-shot (a fork moves the generation number back and the script stalled), `state` prints the branches, `later:S:VERB:ARG` (runs a verb after a delay while a rebuild or train-to blocks the script), `cancel-after:S`, `fork-raw`, `switch-raw`, `panel-scroll:N`, `panel-wheel:N` (real wheel events), `panel-first`, `rebuild-cancel`.
- **07 Autosave and resume:**
  - Already there and working: the autosave on the saver thread (`TrainingHost.Autosave`, 2 min, "saved HH:mm"), the exit save in `Dispose`, Esc/Menu/quit through `Main.AfterSaving`, the failed-save banner (05). Checked and kept.
  - **Mode and speed are now saved** (they were not stored anywhere: Continue always came back in Auto at the `--speed` default). `SessionHeader` got optional `stepMode` (bool) and `speed` (number, or the text "max" for MAX); `Session.StepMode/Speed` (null = a file without them, then the options count) with `SetViewState` (a change counts as an unsaved change); the host copies them in before every save. Format stays 1, old headers load. `--speed` on the command line still wins over the saved speed. Paused is not saved. (Resuming in Step mode now waits for Next: Stage C, R3/R3b.)
  - **Exit save failing:** `TrainingHost.Dispose` is split into `Stop()` (stop, final save, result in `ExitSaveError`), `RetryExitSave()` and `Dispose()`. `Main` stops the view first (`TrainingView.StopAndSave`), and if the save failed it keeps the stopped view in memory and shows "Could not save the session" with the reason and "Leave without saving" / "Try again" (Esc asks again). So nothing is lost by accident and a retry works after the problem is fixed. Applies to Menu, Sessions, Open, new training and quit/window close alike.
  - **Window close / signals:** `Main._Notification` close request: the saving layer is now added deferred (adding a child inside the notification failed: "Parent node is busy setting up children"). SIGTERM, SIGHUP and SIGINT are handled like the window's X (first one saves and quits, a second one ends the app at once). A SIGKILL, a power loss or a dead X server cannot be handled: the last autosave counts (at most 2 min lost).
  - Debug verbs: `close-request` (Main: `Root.PropagateNotification(NotificationWMCloseRequest)`, the real path), `exit save: …` log line in `StopAndSave`; while a view is being left only `wait`, `shot`, `modal-ok`, `modal-close` run from the script.

### Stage C — review fixes (2026-10-11)

A two-lens review of Stage A + B (data safety; app behaviour and threading vs PLAN §8 M3 and 0 B/frame), every finding
checked by running code. Two medium data findings (R1, R2) and five low ones were fixed; each fix has a test or a
scripted check. SimVersion 2 and every pinned hash unchanged. 486 tests green (Debug and Release).


- **R1 Opening race (data loss):** Main loads a session (seconds) and only then creates the `TrainingHost`, which took the folder lock. If another window saved the session in between, the stale session was trained and saved over it; if it was deleted, the lock recreated the folder and a partial session (header + new chunks) was written. Fix: for a loaded session the constructor uses `SessionFolder.AcquireLockForLoaded`: no folder means refuse at once; after taking the lock the header.json fingerprint (`SessionFolder.HeaderStamp`, taken by `Session.Load` before reading, `Session.LoadedHeaderStamp`; also right when the backup header was used, which a savedAt compare is not) must still be the loaded one. Otherwise the lock is released, a folder the lock recreated is removed again (nothing left behind) and `SessionChangedException` (an `IOException`: "The session changed on disk while it was opening ... Open it again.") is thrown. Tests: `OpeningAStaleSessionAfterAnotherWindowSavedIsRefusedAndKeepsTheOtherWindowsWork`, `OpeningASessionDeletedMeanwhileIsRefusedAndLeavesNothingBehind`, `ADeletedFolderThatIsRecreatedByTheLockLeavesNothingBehind`, `OpeningASessionLoadedFromTheBackupHeaderStillWorksAndSpotsAnotherSave`.
- **R2 No autosave while idle (data loss window):** the scheduler thread slept until a frame or command came, so a paused training, Step mode waiting for Next or a replay never autosaved. Fix: the wait has a 1 s timeout; on a timeout with nothing to do the loop only runs `Autosave(force: false)` and sleeps again (no frame/served accounting touched). Tests: `AutosaveStillRunsWhileTheTrainingIsPaused`, `AutosaveStillRunsWhileStepModeWaitsForNext` (autosave interval 1 s, no Frame calls, the saved header reaches the latest generation).
- **R3 Continue in Step mode ran at once:** a resumed session restored Step mode but its first generation started running. Fix: resuming with Step mode on starts paused in the WaitingForNext state; Next/Resume starts it. Test: `ResumingInStepModeWaitsForNextInsteadOfRunning`.
- R3b: a resumed Step-mode session now fills the summary (`LastGeneration`/`LastStats`) from the stored generation before the current one; at a branch's first generation it starts paused but not waiting.
- R4: closing a replay that had "keep training while watching" on, in Step mode, unpauses so the half-run generation finishes and waits at its summary.
- R5: in the Sessions list, muted (read-only/damaged) rows turn white while selected so they stay readable on the orange bar.
- R6: the Segments tab note for a fork-only segment now starts with "segment N:" (the number the HUD shows).
- The save-failure banner (and every banner) is a fixed 520 px wide box with word wrap: a long error with its path
  covered the HUD panel and the Menu / Training panel buttons (`out/shots-m3-final/failed-save-banner-wrapped.png`).
- **Checked and found sound:** kill mid-autosave (SimBench kill-test, 12 random `kill -9` of the app at 100× with
  continuous saving), quit/window close/SIGTERM during train-to, replay, Step mode and a fork rebuild (every ended
  generation saved, Continue identical), a failed save (message, training goes on, retry at exit), rename/delete of an
  open session (refused; in one window it cannot be reached), fork/switch during training (queued on the scheduler;
  refused during a train-to or rebuild), SimVersion mismatch (read-only, never saved), thinning (fork from thinned
  generations rebuilds bit-exactly), the lock order (the host's lock is taken before `Session.SyncRoot`, never the
  other way round).
- **Left as they are (low):** the replay view's 10/s text refresh is not excluded from its own allocation row (~4 KB in
  those frames; the training view is 0 B/frame); with continuous saving (`--autosave-seconds 0`) 1–2 frames per run
  show ~7 KB on the main thread right after a garbage collection (not seen with the default 2-min autosave);
  `TrainingHostTests.TenXRunsTenTicksPerFrame` (M2) can fail when the box is heavily loaded (passes on rerun); the
  mutation and speed sliders snap to their steps (×1.41, 25×), by design.
  Also from the final check: generations that end while "keep training while watching" runs are not stopped by Step
  mode (by design; after Back the running generation finishes and waits), so the few that end in the moment between
  Back and the cancel reaching the scheduler are skipped too; a time-limit change made while Step mode waits applies
  after the waiting generation, and is lost if the app is quit before that generation ends (pending changes are not
  saved).

### Deviations

- Stage A:
  - **Generation 0's snapshot is never thinned** (PLAN §4: "the oldest full-population snapshots are thinned"). It is a
    multiple of every spacing, so the levels never drop it; it is one snapshot, and generation 0 rebuilds from the seed
    anyway.
  - **History chunks may be several files per window** (PLAN §5.1: chunks of 25 records, append-only). M1's partial
    chunks are generalised: a window is one file per consecutive run of its records; only after a damaged file is a
    window split, so the rest of the history stays readable. Complete, unthinned windows are still written once.
- Stage B:
  - 07: `SessionHeader`/`Session`/`TrainingHost` (Sim) changed for the saved mode and speed and the `Stop`/`RetryExitSave` split (PLAN §5.1 lists neither); tests added (`ModeAndSpeedRoundTripAndAreOptional`, `AnInvalidModeOrSpeedIsRefused`, `ModeAndSpeedComeBackWhereTheUserLeftThem`, `ASessionWithoutModeAndSpeedUsesTheOptions`, `AFailedExitSaveIsReportedAndCanBeRetried`).

**What to try (M3):** unzip, run Nitrogenesis.exe (a normal build; saved trainings live in %APPDATA%\Nitrogenesis\sessions).

1. **New training** → pick a track (try Sprint) → Start. The HUD (top left) now has Pause, Step, Mode: Auto, the speed
   slider, **Next gen**, **Train to...**, **Replay best** and a **Mutation** slider; the corner says "saved HH:mm" (the
   session saves itself every 2 minutes and when you leave).
2. **Mode: Step** (or key M): the training stops at the end of every generation and shows a box with that generation's
   result (finished, best time, best and average score, "Next bred with"). **Next (Enter)** plays one more generation;
   **Back to Auto** returns to normal. Try it at 10x or more so a generation takes seconds.
3. **Next gen** (key N): ends the running generation right now (cars that are still driving just stop) and breeds the
   next one. The graph shows a dip for that generation.
4. **Mutation slider**: drag it to x2 while a generation runs; the HUD says it applies from the next generation, and
   the next generation is bred with the stronger mutation. x1 is normal.
5. **Train to...** (key T): three conditions in one box: *To generation N*, *Until a car finishes*, *Until the best time
   is below S*. Start: the map is not drawn, all threads work, you see generation, generations/s, time, best time and
   a live graph. **Cancel (Esc)** returns to the training at the generation reached. Closing the app meanwhile saves.
6. **Graph**: click the small graph (bottom right) or press G for the big one (axes, dashed orange line = a new segment
   starts). Esc, G or a click closes it.
7. **Training panel** (button top right, or H): tab **History** (one row per generation: best score, time, how many
   finished; **Watch best**, **Whole gen.**, **Fork...**), tab **Branches**, tab **Segments**.
8. **Watch best**: plays the stored recording of that generation's best car (0.01x to 100x, Pause, Step, Restart).
   **Whole gen.**: simulates that whole generation again with all its cars (the result should match the history row).
   Tick **Keep training while watching** to let the training go on in the background; **Back (Esc)** returns and the
   training view shows the newest generation.
9. **Fork...** on a row: a box offers **Whole population** (continue with all cars of that generation) or **Best car
   only** (a new population of mutated copies of its best car). A fork from an old generation may first show
   "Rebuilding generation N..." with a progress bar and Cancel (that is normal: old generations are re-run from a
   snapshot). The fork becomes the active branch; the old branch stays.
10. **Branches** tab: every branch with its origin ("from main at generation 10 - whole population"); **Switch** makes
    another branch active (each branch continues where you left it).
11. **Segments** tab: type a new **generation time limit** (0 = auto) and press "Apply at the next generation...". A
    confirmation box explains that this starts a new segment: the best time, the stagnation check and the graph restart
    there (the graph shows a dashed orange line); the population carries on.
12. **Esc** (or Menu) saves and returns to the menu. **Continue** (menu) opens the last session exactly where you left
    it (same generation, branch, mode, speed, mutation); **Sessions** lists all of them (Open, **Rename...**,
    **Delete...** with a confirmation). A session made with an older physics version opens read-only (graph, history,
    Watch best only).
13. **Close the window (X) in the middle of training** and press Continue again: nothing is lost (the X saves
    first; after a crash or power loss at most the last 2 minutes are). If saving ever fails (folder read-only, disk full) you see "save failed - training goes on"; on
    leaving a box offers **Try again** or **Leave without saving**.
14. **F3**: the Alloc row should still read **0 B / frame** at 1x and at 100x, also with the panel and graph open.
15. **Kill test on the laptop:** in a terminal in the unzipped folder run `SimBench\SimBench.exe kill-test --rounds 15
    --gens 250` (kills a training in the middle of saving 15 times; takes a few minutes). It must end with "0 failed
    loads" and "identical to the uninterrupted run: yes". Send me the last lines. (`SimBench\SimBench.exe learn-suite`
    must also end with "learn-suite passed".)

## M4 — Track editor

### Decisions
- Ticket 01 (`src/Sim/Map/EditDocument.cs`, `DirtyRect.cs`): brush of size n at cell (x,y) covers lo..lo+n-1 with lo = x-(n-1)/2, so odd sizes are centred and even sizes extend one cell towards +x/+y. Round brush = cells whose centre is within n/2-0.1 of the brush centre (1 = dot, 2 = 2x2, 3 = plus, 15 = disc); BrushSize is clamped to 1-15.
- Undo cap `EditDocument.MaxUndoSteps` = 200 (oldest dropped). A step stores only changed cell indices + old bytes + the one new type (about 5 bytes per changed cell; 200 whole-L fills would be about 80 MB, unrealistic).
- New map start = centre cell centre (w/2+0.5, h/2+0.5), angle 0. `SetStart` clamps the cell into the grid, snaps the angle to the nearest 15 degrees and normalises to [0,360) (so 352 -> 345, 353 -> 0; ties round up). No convention existed in TrackFile, which stores the angle as given.
- Every call returns `EditResult(DirtyRect Rect, bool StartChanged)` and also raises `Changed`; stroke moves report only the cells newly changed by that move. `Revision` bumps on every real change (including each stroke move, undo, redo).
- Undo/redo while a stroke is active ends the stroke first (Undo then undoes that stroke). Starting a new stroke or any other tool ends an open one. A stroke keeps type/size/shape from BeginStroke.
- `IsDirty`/`MarkSaved` follow the undo position (undo back to the saved state = clean); if the saved step falls off the cap it can never be clean again. Name/Meta edits are not tracked.
- Fill is iterative (explicit stack, cells repainted when pushed), 4-connected, no-op for same type or outside grid. `Snapshot()` returns a Track with a cloned grid and deep-copied meta.
- Ticket 02 (`app/Editor/`: `EditorView`, `EditorCanvas`, `EditorToolbar`, `PaintSwatch`, `DrawLayer`): the canvas is a Control holding a Node2D "world" (scale = px per cell, position = pan) with layers map sprite, grid, `MapLayer` (hook for ticket 03), preview/start overlay. Painting only repaints the dirty rectangle: `MapTexture.CreateImage` + `MapTexture.Repaint(image, grid, rect)` (BlitRect) and one `ImageTexture.Update` per frame (changes are accumulated in `_Process`); `MapTexture.Create` output is unchanged. Hooks on `EditorView`: `Document`, `DocumentReplaced` (raised by `ReplaceDocument`), `MapLayer`, `StatusBarSlot` (a 30 px PanelContainer with an empty placeholder label).
- Ticket 02: the editor draws the start as an overlay (the M2 arrow as a polygon, accent colour) with a rotate handle 7 cells ahead while the Start tool is active. Placing or rotating keeps a pending start in the canvas and calls `SetStart` once on mouse release, so it is one undo step. Line and rectangle call the document once on release (clamped to the map); brush and eraser use Begin/Move/EndStroke (ended on release, focus loss and Esc); fill acts on press. The eraser is the Wall paint type while the Eraser tool is active; picking a paint type while Eraser or Start is active switches to Brush.
- Ticket 02: view: wheel zoom around the cursor (x1.15 per notch, from half the fit zoom to 64 px/cell), middle/right drag or Space+left drag pans (map stays at least 60 px on screen), grid (G) shows from 8 px per cell. Shortcuts: B L R F E S, 1-5, [ ], G, Ctrl+Z, Ctrl+Y / Ctrl+Shift+Z, Esc (cancels a drag first, otherwise back to the menu with no unsaved-changes check until ticket 05). Toolbar buttons take no focus so keys keep reaching the view.
- Ticket 02 debug: `--editor [S|M|L|TRACKNAME]` (new map, or an editable copy of a bundled track). `--do` verbs (EditorView.DebugDo): `tool:`, `paint:`, `size:`, `shape:`, `stroke:x,y/x,y/...`, `line:x0,y0,x1,y1`, `rect:`, `fill:x,y`, `start:x,y,angle`, `undo`, `redo`, `grid:on|off`, `zoom:PX_PER_CELL`, `center:x,y`, `fit`, `hover:x,y`, `drag-preview:tool,x0,y0,x1,y1`, `newmap:S|M|L`, `state` (size, revision, undo/redo, dirty, start, cell CRC), `mouse:down|move|up|wheelup|wheeldown,x,y[,left|right|middle]` (real injected mouse events at the centre of cell x,y; the start handle is 7 cells ahead of the start), `bench[:CELLS_PER_FRAME]` (a serpentine stroke made of injected mouse moves, prints idle and painting frame times and texture-update times; the script waits for it, use `--quit-after`). Screenshots in `out/shots-m4/`.
- Ticket 02: checks on this box (load average about 36, xvfb + llvmpipe, 1280x720): L map, brush 7, 408 mouse moves of 16 cells: idle frame 60 ms vs 55 ms while painting (max 148), texture update avg 7 ms (3.6 ms on a quieter run), so painting adds nothing visible over the software renderer's own cost. xvfb was not installed any more and was added with apt.
- Ticket 03 (`src/Sim/Map/EditorAnalysis.cs`, `EditorAnalysisRunner.cs`; app `app/Editor/EditorChecks.cs`, `EditorStatusBar.cs`, `AnalysisOverlay.cs`): `EditorAnalysis.Run(track, settings, ct)` is a pure function that reuses Clearance, DistanceField, Solvability and the GrassShortcut result type (two Dijkstra runs: with grass and road only). Result = state (the Solvability cases), unreachable mask (passable cells that cannot reach the finish; filled only when the finish is cut off), too-narrow mask (non-blocking cells not passable for half-width 0.9), road-behind-start flag, grass-shortcut flag + route, estimate and a message list (Ok / Warning / Error). Estimate = start distance in the with-grass field / (`EstimateSpeedFactor` 0.6 x MaxSpeed); `VeryShortSeconds` = 20 gives a warning.
- Ticket 03: start problems: outside the map, in a wall / too narrow, car outline touching wall or danger (the Solvability cases), plus "one car length of road behind the start" = the centre line sampled every 0.5 cells from the start back 3.0 cells (`CarSize.Length`), each sample must be a Road or Finish cell the car fits on (grass, danger, wall or outside fail). The road check is skipped when the start already has one of the first three errors. The grass-shortcut route is the with-grass field followed downhill (lowest of 8 neighbours) from the start; it is only built when the shortcut warning applies.
- Ticket 03: `EditorAnalysisRunner`: one background thread, `Request(snapshot, revision)` debounced 150 ms (`DebounceMilliseconds`) after the last call; a newer request replaces a waiting one, cancels a running one and clears an untaken result, so only the newest request ever delivers. `TryTakeResult` (UI, once per frame) / `WaitForResult` (tests); the result carries its `Revision` and the UI shows it only if it equals `document.Revision` (a new runner is made when the document is replaced, since revisions restart at 0). The lock guards only the hand-over, never the analysis.
- Ticket 03: the editor asks for a new analysis from `Document.Changed` itself (not in the next frame), so the debounce starts at the edit; a snapshot is one 36 KB array copy. While a check is pending the status bar and overlays show the previous result dimmed with "checking...". Overlay = one RGBA texture (1 px per cell, nearest): red tint = cut off, orange diagonal hatch = too narrow, plus a cyan polyline for the grass route. Log line per applied result: `editor-check: ... rev N applied X ms after the edit (result ready after Y ms, frame wait Z ms)`.
- Ticket 03 timing (box load average 12-22, 12 cores, xvfb + llvmpipe frames of 20-60 ms): Run takes 4-6 ms (Release, bundled maps), 10 ms on a wide-open M map with a big grass block, 13-22 ms in a Debug Sim build (first call about 40 ms of JIT). Acceptance (wall a line across the road, "Not finishable" on screen): 169-188 ms in 7 runs at load about 12; 181-232 ms at load about 21 (150 ms debounce + 5-25 ms compute/wake-up + 0-45 ms wait for the next frame, which is the software renderer's frame time). On an idle box with a GPU expect about 160-170 ms. The timing tests (`EditorTimingTests`, run alone) assert the best of 15 runs (Run < 50 ms, 150 ms in a Debug build; request to result <= 200 ms) and a loose median bound, because a loaded box makes the median noisy.
- Ticket 04 (Test / Drive; `EditorView`, `TestRouteLayer`, `EditorToolbar` "Try it" box, `EditorChecks.SetReferenceTime` -> `EditorStatusBar.SetReferenceTime`): Test (button or T) runs the analysis and then `ReferenceDriver.Run` in `Task.Run` on a snapshot, tagged with the document revision; any edit (`Changed`) or `ReplaceDocument` drops the result, the route and the badge at once, and a run that finishes later is ignored. Not finishable per the live analysis is not driven. Badge: "Finished in 27.3 s" (route + status bar "27.3 s (Test)" instead of the live "≈ 31 s"), "Finishable, but very tight" (route + red mark where the driver crashed/stalled/timed out) or "Not finishable".
- Ticket 04: real tight case found by a probe (not faked): a two-lane serpentine on an M map, lanes 4 cells wide, 1 cell wall between them, joined by a 4-wide connector: `paint:road;rect:20,20,236,23;rect:20,25,236,28;rect:233,24,236,25;paint:finish;rect:21,25,23,28;start:23,22,0`. The field says finishable (about 39 s) but the reference driver stalls at the hairpin after 15.6 s. Other widths 3-8 and gaps 1-4 finished; width 2 is not finishable.
- Ticket 04: Drive (button or D) hands `Document.Snapshot()` to `DriveView` (`BackToEditor = true`: the button says "Editor (Esc)", hints and the finish text say editor). Main parks the editor (`RemoveChild`, no free) in `_parkedEditor`, shows the drive view, and on its Back (deferred, so not inside the key event) frees the drive view, restores the clear colour and re-adds the same editor. `_Ready` runs once but `_EnterTree`/`_ExitTree` every time, so hooks that were in `_ExitTree` only are now redone in `_EnterTree` (EditorView, EditorCanvas); `EditorChecks` disposes its runner in `_ExitTree` (no analysis thread while driving or after the editor is freed) and makes a new runner plus a fresh analysis in `_EnterTree`. `Main.Switch` frees a parked editor too (Menu while driving). Checked with /proc thread names: 1 EditorAnalysis thread in the editor, 0 while driving, 1 after Esc, 0 after the menu.
- Ticket 04 debug: `--do` verbs `test` (later `shot` steps wait for the result), `drive`, and in the drive view `back` or `key:Escape` (new general `key:NAME` verb, a real key press through Godot's input); `--autopilot` also works for a drive opened from the editor. `state` now ends with `test=none|running|Finished|Tight|NotFinishable`. Shots `out/shots-m4/test-*.png`, `drive-*.png`.
- Ticket 05 (`src/Sim/Io/TrackLibrary.cs`, `app/Editor/EditorFileDialogs.cs`, toolbar File group, `TrackPicker`): user tracks live in `AppPaths.Tracks` as one `<name>.track` per name; names are compared ignoring case (Windows), same rules as session names (not empty, allowed characters, max 60, no trailing dot/space, no device names) with a sentence per failure. Save is atomic: temp file `<file>.saving` in the same folder, `Flush(true)`, then `File.Replace` (or `Move` for a new file); on any failure the temp is deleted and the old file is untouched (`TrackFile.Save` is unchanged). `List()` shows a damaged file with its problem instead of failing; 25 tests in `TrackLibraryTests` (611 total).
- Ticket 05 UI: File group (New / Open / Save / Save as, Ctrl+N/O/S/Ctrl+Shift+S) and the track name with a bullet when unsaved replace the "TRACK EDITOR" heading; Undo/Redo/Grid share one row so the toolbar still fits 720 px. New = size box (S 192x108, M 256x144, L 384x216). Open = one list: user tracks (damaged ones disabled with the reason), then bundled (opens a copy named "<name> copy" with no file, so Save asks for a name). Save on a document with a file writes there, otherwise Save as; the name box validates, a clash asks "A track named X already exists. Overwrite?" (Cancel returns to the name box); a failed save shows the reason, the document stays dirty. "Save changes to X?" (Save / Don't save / Cancel) guards New, Open, Back/Esc and the window close.
- Ticket 05 quit: `Main.RequestQuit(confirm)` asks only for the window X and OS signals; while the editor is parked for Drive it is brought back first (deferred, the close notification blocks AddChild) and asks there. `quit`, `--quit-after` and shot-quit never ask. The training close/save flow is unchanged.
- Ticket 05 pickers: bundled tracks first, then a "YOUR TRACKS" heading and the user tracks ("· your track", damaged ones greyed "· damaged"). A session already stores its own track copy (`SegmentEntry.Track`), so Continue works after the user file is deleted (checked). `--track/--drive/new:NAME` and `--editor NAME` also find user tracks (bundled first). A user track may share a name with a bundled one; both are listed.
- Ticket 05 debug: editor `--do` verbs `new:S|M|L`, `open:USERTRACK`, `open-bundled:NAME`, `save`, `save-as:NAME` (same functions as the dialogs, including the questions), `new-dialog`, `open-dialog`, `save-as-dialog[:TEXT]`; Main `modal-button:LABEL` (presses a button of the newest box); picker `pick:NAME` and `start`, `--picker drive`. `state` shows `name=` and `file=`. Shots `out/shots-m4/files-*.png`.

### Review (07) and fixes (08) (2026-10-11)

Manager review of tickets 01-05 against PLAN §6.3/§8 M4 (tools and undo, analysis correctness and threading, file
safety, unsaved-changes flows, picker/session integration, training view 0 B/frame) plus the tester's findings (06).
Checked and found sound: one stroke = one undo step, undo/redo restore exact bytes (24-step round trip by hash), redo
cleared by a new edit; the analysis runner only ever delivers the newest request and the UI shows a result only when
its revision equals the document's (a new runner per document); Test results are tagged with the revision and dropped
after any edit; the analysis thread stops while driving and when the editor is freed; atomic track save (temp + flush +
File.Replace/Move, temp removed on failure, old file intact); sessions keep their own track copy (Continue works after
the user file is deleted); training view still 0 B/frame at 1x and 100x. Fixed:

- **F1 (medium) Unfinishable user tracks could be trained/driven** (no finish, start in wall: 60 cars in a wall with a
  3600 s limit). The pickers now check the selected track (`Solvability.Check`, cached), show "Not finishable: <reason>
  - fix it in the Track editor" in red and disable Start (also Enter, double-click, the `start` verb).
- F2 (low) "Too little road behind the start" was a red error next to a green "Finishable"; it is now an orange
  warning (errors = not finishable). F3 (low) a start on Danger said "in a wall": now "on a wall or danger cell, or in a
  spot too narrow". F4 (low) a new map (no finish, start in wall) said "Start needs 3 cells of free road behind it"; it
  now names the start-in-wall problem (tests `LessThanOneCarLengthOfRoadBehindTheStartIsAWarning`,
  `ANewMapNamesTheStartInWallInsteadOfTheRoadBehindIt`, `AStartOnDangerSaysDanger`).
- F5 (low) damaged track files showed raw JSON parser text; now "damaged: not a readable track file" (details in the
  tooltip and the log). F6 (low) the Start tool drew a meaningless one-cell hover box: removed. F7 (low) the window X
  was ignored while the editor had unsaved changes and another box was open: the box is closed and "Save changes?" is
  asked (`Modal.CloseAll`, `Modal.UnsavedQuestionOpen`). F8 (low) Save as with a name differing only in letter case
  from an existing file left the document showing the typed case: the document now takes the saved file's name.
- F9 (debug) `key:` accepts modifiers: `key:ctrl+z`, `key:ctrl+shift+z`, `key:ctrl+s` (ctrl, shift, alt).
- Screenshots: `out/shots-m4-final/` (fixes and the final check), `out/shots-m4-verify/` (ticket 06), `out/shots-m4/`
  (tickets 02-05).
- **Final check (ticket 06 re-run after the fixes):** 613 tests green (Debug and Release); learn-suite passed (72.6 s);
  acceptance (s_curve, wall across the road by line/rect/brush, 12 runs, load 6.7-8.4, xvfb + llvmpipe): "Not
  finishable" on screen 167-186 ms after the edit (median ~178), 12/12 within 0.2 s; training view 0 B/frame at 1x and
  100x; `build/export-windows.sh` exported the exe but stopped at rcedit because Wine is no longer installed on this
  box (icon/version, SimBench publish and the zip not made; no Windows/Wine run); Linux headless `--check-tracks` and a
  scripted `--editor` run were clean. Builds must run through the physical path (`~/projects/coding_projects/...`):
  through the new `~/coding_projects` symlink the build fails with CS0006.
- **Left as they are (low):** while Eraser or Start is active the swatch row still highlights the last paint type; a
  damaged file named "Damaged" reads "Damaged - damaged: ..."; names longer than 60 characters are cut by the text box
  instead of rejected; Godot rewrote `app/project.godot` (header comment, `run/max_fps` moved; same settings).

### Deviations

- "At least one car length of free road behind the start" (PLAN §3.1, "required") is an orange warning in the editor,
  not an error: the track can still be finished and saved; the generator (M5) can treat it as a hard rule.
- The picker refuses tracks that are not finishable (not in the plan; a gap filled after the review).
- Painting rewrites only the dirty rectangle's pixels on the CPU, but the texture is uploaded whole once per frame
  while painting (Godot 4's `ImageTexture.Update` has no sub-rectangle upload; 3.6-7 ms on llvmpipe for an L map).
- The live-check debounce starts at every edit (each stroke move), so a check runs 150 ms after the stroke stops, not
  after the mouse is released.
- Acceptance timing was measured under xvfb with a software renderer (frames 20-60 ms); see "What to try" for the
  owner's check on real hardware.

### What to try (M4)

**What to try (M4):** unzip, run Nitrogenesis.exe (saved tracks live in %APPDATA%\Nitrogenesis\tracks, one `<name>.track` file each).

1. **Open the editor:** main menu → **Track editor**. You get an empty map (all wall, a start flag in the middle). To
   change a bundled track, press **Open** and pick one under BUNDLED: it opens as an editable copy ("<name> copy"),
   the original is never touched. **New** asks for a size: S 192x108, M 256x144, L 384x216.
2. **Tools** (left panel or keys): **Brush** (B), **Line** (L), **Rectangle** (R), **Fill** (F), **Eraser** (E, paints wall),
   **Start flag** (S). Pick what to paint with the swatches or keys 1-5: **Road, Grass, Wall, Danger, Finish**. The
   brush has a size slider (keys [ and ] change it, 1-15) and a Round / Square shape; a white outline shows what a
   click will change. Line and rectangle show a preview while you drag and paint when you let go. Fill repaints a
   whole connected area in one click.
3. **Start flag:** click a cell to put the start there; drag from it to turn the car (steps of 15 degrees); you can
   also grab the round handle in front of the arrow. Place the start on road with at least one car length of road
   behind it.
4. **Undo / redo:** Ctrl+Z, Ctrl+Y (or Ctrl+Shift+Z), or the buttons. Up to 200 steps; a new edit after undo drops the
   redo steps. A whole brush stroke is one step.
5. **View:** mouse wheel zooms around the pointer; pan with the middle or right mouse button, or Space + left drag.
   **Grid** (G) shows from 8 pixels per cell. Esc cancels a drag first; otherwise it leaves the editor.
6. **Status bar** (bottom): it re-checks the track about 0.2 s after every edit. Green "Finishable" with a time
   estimate (about); red "Not finishable" with the reason (no Finish zone; the finish cannot be reached, shown as red
   area on the map; the start is on a wall/danger or too narrow; the car at the start touches a wall or danger); orange
   warnings: "Start needs 3 cells of free road behind it", "Very short track: under 20 s" and "Cars will cut across the
   grass here" (a cyan line shows that route). Orange hatching on the map = spots too narrow for the car.
   Try it: build a road, put a Finish on it, then wall the road off with a line and watch "Not finishable" appear.
7. **Test** (button or T): a reference driver drives your track. The map shows its route and a box says
   "Finished in 27.3 s", "Finishable, but very tight" (a red mark shows where it got stuck) or "Not finishable". Any
   edit removes the result.
8. **Drive** (button or D): drive your track yourself (W/S, A/D or arrows); **Esc** brings you back to the editor with
   everything as you left it, undo still works.
9. **Save:** Ctrl+S saves (asks for a name the first time), Ctrl+Shift+S = **Save as**. Names: letters, digits, spaces and
   - _ ( ) [ ] , . + ' & ! # only, up to 60 characters, no trailing dot or space; the box says what is wrong. A name
   that already exists asks "Overwrite?". A dot after the track name in the top left means unsaved changes. **Open**
   (Ctrl+O) lists YOUR TRACKS first, then the bundled ones; a damaged file is listed greyed with the problem.
   **New**, **Open**, **Back** and closing the window ask "Save changes?" (Save / Don't save / Cancel) when
   something is unsaved.
10. **Use your track:** **New training** and **Drive** now list YOUR TRACKS after the bundled ones (a track that cannot
    be finished is shown with the reason in red and cannot be started). Train a few
    generations on it; **Continue** keeps working even if you later delete the track file (the training keeps its own
    copy).
11. **Please send back:** anything that felt clumsy (which tool, what you expected), any message that was unclear,
    a track that Test says is fine but cars cannot learn (or the other way round), the delay between an edit and the
    status bar update (it should feel instant), and a saved .track file if something would not open.

## M5 — Track generator (2026-10-13)

Done: `src/Sim/Gen/` (`GeneratorParams.cs`: `TrackTemplate`, `GeneratorParams`, `Templates`; `TrackGenerator.cs`: `Generate`,
`Validate`, `GenerateValid`, `GenCheck`, `GenResult`), SimBench `gen` and `gen-check`, `make-tracks` also writes the 6 fixed-seed
generated tracks (`tracks/gen_*.track`, bundled as "<Template> (generated)"), learn-suite cases for them, and the editor's
**Generate** / **Similar** buttons (`app/Editor/EditorGenerator.cs`, toolbar). 697 tests green (Debug and Release; 84 new:
`TrackGeneratorTests` and the 6 generated tracks in `BundledTracksTests`). SimVersion stays 2; no physics or default changed.

### How it works
- One attempt (`Generate`, pure function of template + parameters + seed, xoshiro128** from the seed): a full-wall map; a route on
  a coarse grid; Chaikin corner cutting (3 passes, ends kept); verge (Grass over Wall) then road with the round brush
  (`TrackBuilder.Polyline`); the template's features; the finish; the start. Only + − × ÷ and √ on doubles, so the cells are
  the same on every platform. A test regenerates each committed `gen_*.track` from its meta and compares the cells.
- **Coarse cell** ≥ (2 × (road radius + verge) + 2) × 4/3 (Labyrinth, not smoothed: × 1): smoothing pulls a corner up to 1/8
  of a cell per axis, so even two pulled-in corners of corridors that are not joined keep ≥ 2 cells of wall between their
  verges. Grass never links two parts of the route, so the grass-shortcut check never failed (0 of ~2,100 checked tracks).
  Minimum cell: Sprint 28, Labyrinth 16, the others 14 (review fix: on smaller cells the smoothed corners are too tight for
  the time estimate; Twisty with road 4 / verge 1, an 11-cell grid, was valid on the first try for 1 seed in 10, now 13–18 of 20).
  The grid is centred on the map; centres sit on cell centres (x.5).
- **Route length** = the middle of the target time × a per-template route-cells-per-second constant
  (`TrackGenerator.ExpectedSpeed`, 17–19, calibrated with gen-check). The walk counts a coarse step as one cell size and a turn
  as 0.19 of one less (a smoothed 90° turn is shorter). The reference times then sit at 24–33 s, about ±3 s round 30 s.
- **Walk** (all but Labyrinth): a self-avoiding walk on the coarse grid from a random border cell heading inwards, with
  backtracking out of dead ends (100,000 steps max, else "did not fit"); straight on first with the template's straightness
  (Sprint and Obstacle field 0.85, Twisty 0.15, Wrong turns 0.5), else the two turns first in random order.
- **Start**: on the first leg facing along it (0/90/180/270°), with 1.5 car lengths of road behind its centre (the road's
  round end; the start moves forward when the road is narrower than 11 cells). **Finish**: a straight line across the last leg
  (always along a grid axis) 3 cells before the route's end; the rest of the road up to its round end is Finish.
- **Validation** (`Validate`): `EditorAnalysis.Run` (finishable, one car length of road behind the start, grass shortcut — the
  editor's own checks), then `ReferenceDriver.Run`: it must finish within MinSeconds…MaxSeconds (default 20–40 s, parameters).
  **Retry** (`GenerateValid`): seeds s, s + 1, …, at most 50 seeds; the result's meta holds the seed that passed. On failure:
  "No valid track in 50 tries (seeds 1–50): <problem> N×, …" plus a hint for the most common problem.
- Meta: `{template: "WrongTurns", params: {size: "M", roadWidth: 8, verge: 2, branches: 3, minSeconds: 20, maxSeconds: 40},
  seed: 17}` (only the parameters the template uses). The file format did not need to change (M1 already had
  `meta:{template?, params?, seed?}` with a round-trip test); reading back ignores unknown keys and wrong types and clamps.

| Template | Defaults (road width / verge / extra) | What it builds |
|---|---|---|
| Sprint | 12 / 2 | walk on 28-cell cells, straightness 0.85: long straights, wide smoothed bends |
| Twisty | 8 / 2 | walk on 19-cell cells, straightness 0.15: a turn at most steps (waves, hairpins) |
| Labyrinth | 7 / 2 / dead-end length 2 | recursive-backtracker maze over the whole 16-cell grid; the route is the pair of cells about target/cell steps apart whose path passes the most side passages; every cell ≤ 2 steps off the route is kept (dead ends, also forked ones), none at the start or finish; straight corridors with round joins |
| Wrong turns | 8 / 2 / 3 branches | walk, straightness 0.5; N dead ends of 2–3 cells from different route cells (not the first or last two), each step to the free neighbour nearest the finish (the first must get closer) |
| Obstacle field | 20 / 2 / 14 obstacles | walk, straightness 0.85; obstacles spaced evenly over the stretches that run straight for (2 × radius + 12) cells before and 12 after: 60 % wall bars (5 thick, 1.5 cells of danger lining) from alternate edges leaving a 4-cell lane, 40 % danger islands (radius 3) with a lane on both sides |
| Mixed | 8 / 2 | walk whose straightness changes per piece of 3–6 steps: straight (0.85), twisty (0.15), chicane (small danger bars from alternate edges every 14 cells), wrong turn (1 branch to the finish), dead ends (2 short random branches) |

### Acceptance

**gen-check** (`dotnet run --project tools/SimBench -c Release -- gen-check`: seeds 1–50 per template, defaults, M map):

| Template | Valid on the first try | Reference time min / median / max | ms per seed (generate + validate) |
|---|---|---|---|
| Sprint | 50/50 (100 %) | 28.4 / 29.9 / 31.5 s | 32 |
| Twisty | 50/50 (100 %) | 28.1 / 29.4 / 30.5 s | 23 |
| Labyrinth | 50/50 (100 %) | 27.5 / 29.9 / 32.3 s | 27 |
| Wrong turns | 50/50 (100 %) | 28.7 / 30.0 / 31.1 s | 25 |
| Obstacle field | 50/50 (100 %) | 24.3 / 28.3 / 31.7 s | 25 |
| Mixed | 50/50 (100 %) | 28.7 / 29.9 / 30.9 s | 23 |

More seeds (`--seeds 200 --from 1000`): 100 % for every template except Obstacle field 97 % (6 × the reference driver crashed into
an obstacle; the next seed was valid each time). L map (`--size L`, 50 seeds): 100 / 100 / 100 / 100 / 90 / 98 %. S map
(`--size S`): Labyrinth 100 %, Twisty 98 %, Mixed 96 %, Wrong turns 92 %; **Sprint and Obstacle field 0 %**: with their wide
default roads a 30 s route does not fit on 192×108 ("the route does not fit: try a bigger map, a narrower road, a thinner
verge or shorter times").

**learn-suite** (`... -- learn-suite`, default settings, seeds 1/2/3): all 39 cases pass and all 12 controls fail, 77 s. The
fixed-seed generated tracks (defaults, seed 1, each valid on the first try):

| Track | Reference time | Budget | First finish (seeds 1 / 2 / 3) | Random search (control) |
|---|---|---|---|---|
| gen_sprint | 30.7 s | 30 | 1 / 0 / 0 | — |
| gen_twisty | 29.8 s | 100 | 5 / 1 / 2 | — |
| gen_wrong_turns | 29.7 s | 150 | 1 / 5 / 0 | — |
| gen_labyrinth | 31.1 s | 200 | 28 / 23 / 47 | no finish in 200 (×3) ✓ |
| gen_obstacle_field | 26.2 s | 100 | 13 / 4 / 10 | not a control: finishes in 2 of 3 seeds (gen 42, 79) |
| gen_mixed | 30.1 s | 150 | 20 / 7 / 11 | — |

The hand-made rows are unchanged (labyrinth 151 / 38 / 42, obstacle field 33 / 21 / 26, …). PLAN §3.3's physics values are
now final: generated M tracks take 24–33 s with them, so nothing was retuned and no template needs the direction hint.

### Decisions
- **Parameters** (all whole numbers, `GeneratorParams.For(template)` lists them for the UI): map size S/M/L, road width 4–30,
  verge 1–3, shortest/longest time 5–300 / 10–600 s (the longest stays ≥ 5 s above the shortest), plus Wrong turns: branches
  0–8; Labyrinth: dead-end length 1–6 maze cells; Obstacle field: obstacles 1–30 (as many as fit). The §7 "target (default
  20–40 s, setting)" is these two time parameters, so a template can aim at other times.
- "Retry with the next seed, up to 50 times" = 50 seeds in total (seed … seed + 49).
- "Generate similar" = the template and parameters from the open track's meta, a new random seed (1 … 2³¹ − 1). It also works
  after the track was edited (the meta stays) and on a copy of a bundled generated track.
- The generator treats "one car length of road behind the start" as a hard rule (the editor only warns, M4).
- Mixed has no row in the §9 table: its learn-suite budget is 150 (that of its hardest pieces, wrong turns).
- Labyrinth: §7's recursive backtracker is kept, but its mazes have long corridors with few forks, so a route from a fixed start
  had only 2–3 dead ends; picking the route among all cell pairs for the most side passages gives the "many dead ends" of §7.
- UI (`EditorGenerator`): **Generate** asks "Save changes?" first (like New/Open), then a box: template buttons (tooltip =
  description), map size, the template's parameters, seed + **New seed**. It starts with the open track's template and parameters
  when it was generated, else the last choice. Generating runs on a worker behind "Generating track" (Cancel/Esc stops between
  seeds; it takes 0.1–0.4 s, longer only when seeds fail); the track opens as a new document without a file, named
  "<Template> <seed>" (Save asks for a name), and **Test** runs at once, showing the reference time and route. **Similar** is
  greyed out unless the open track has a template in its meta. A failure shows the summary and the hint.
- Debug `--do` verbs: `generate:TEMPLATE[,SEED[,S|M|L]]` (defaults; later steps wait), `generate-dialog`, `similar`; `state`
  ends with `meta=Template/seed`. Screenshots: `/tmp` only (not kept).

### Deviations
- **Sprint is not straight across the map**: one straight crossing of an M map is ~14 s, below the 20 s minimum, so the sprint
  snakes (long straights joined by a few wide bends).
- **Twisty's turns are mostly taken at speed**: smoothed corners on 19-cell cells have a radius of ~6.7 cells and the car turns
  at 5.2 cells at full speed, so "many turns" shows in the shape and the steering work more than in braking. Tighter corners
  would need a narrower road than the "medium width" of §7.
- **The generated obstacle field is easier than the hand-made one**: obstacles only on straights (so the reference driver gets
  a run-up; without it 18 % of tracks failed) and islands with a lane on both sides. Evolution needs 4–13 generations, random
  search sometimes finishes it, so it has no random-search control (the hand-made obstacle_field keeps that role).
- **S map**: Sprint and Obstacle field never fit there with their default road widths (see above); the box says why.
- No Windows build was made for M5 (not asked for in this run).

### What to try (M5)
1. **Editor → Generate** (left panel, under Save): pick a template (hover a button for what it makes), keep the defaults and press
   **Generate**. The track opens at once with the reference driver's time on top ("Finished in 29.8 s") and its route drawn.
2. **Similar**: press it a few times: the same kind of track, a new layout each time (a new seed). It is greyed out on a map you
   painted yourself.
3. **Parameters**: open Generate again (it starts with this track's template and settings): try a wider or narrower road, verge
   1 or 3, Wrong turns 6, Labyrinth dead-end length 4, Obstacle field 25 obstacles, or a time range like 40–60 s on an L map.
4. **Seed**: note a track's seed (its name, e.g. "Twisty 1840275531"), press **Similar** for another one, then open Generate (it
   keeps the template and settings) and type that seed back in: the same track again. The seed alone is not enough: the template
   and every setting must match too.
5. **A track that cannot be made**: Sprint on the S map: after a moment a box says no valid track was found in 50 tries and why.
6. **Edit and use it**: paint on a generated track, Test it, Save it, then train on it from New training (it is under YOUR TRACKS).
7. **New bundled tracks**: New training and Drive list six "(generated)" tracks (one per template); try training on
   "Labyrinth (generated)" (first finish around generation 25–50).
8. **Please send back**: a template whose tracks look wrong or boring, a generated track the cars cannot learn, and any seed
   where Generate failed although the parameters looked reasonable (with the seed and parameters).
