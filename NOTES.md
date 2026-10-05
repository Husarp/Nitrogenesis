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
