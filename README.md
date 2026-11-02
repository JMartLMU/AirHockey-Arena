# AirHockey Arena

**Jacob Martinez**

CMSI-5998 Final Project · Export-Type Project

AirHockey Arena is a Unity air hockey project that combines physics-based play, procedurally generated obstacles, and opponents trained with Unity ML-Agents. Players control a mallet, defend their goal, and adapt their shots as boost pads, slow zones, bumpers, and walls change the table between rallies.

The project began with a roguelike concept. The implemented experience focuses on repeatable air hockey rallies, configurable arena generation, and machine learning experiments. The repository includes a playable scene, a training scene, recorded demonstrations, exported models, training logs, and automated generation tests.

## Getting started

The project records **Unity 2023.2.1f1** in `ProjectSettings/ProjectVersion.txt`. Its package files specify **ML-Agents 3.0.0**, **Sentis 2.1.0**, and **Unity Test Framework 1.4.3**. Python is only needed to train new models.

1. Clone or download this repository.
2. In Unity Hub, add **`src/Airhockey Arena`** as the project folder and open it with Unity 2023.2.1f1.
3. Allow Unity to import the assets and resolve the packages.
4. Open `Assets/Scenes/AirHockeyArena.unity`.
5. In the arena, check that `HandleBlue` uses **Heuristic Only** and `HandleRed` uses **Inference Only** in their **Behavior Parameters** components. These are the saved prefab settings; the red mallet references `Assets/MLAgents/Results/RandomSelfPlay01/Mallet.onnx`.
6. For human play, turn off **End Agent Episodes On Goal** on the arena's `AirHockeyEnvController`. Enable **Enable Keyboard Reset** to use `R`.
7. Press **Play** and click the Game view to give it keyboard focus.

| Control | Action |
| --- | --- |
| `W` / `S` | Move toward / away from the opponent's goal. |
| `A` / `D` | Move left / right from the controlled team's perspective. |
| `R` | Reset the rally when keyboard reset is enabled. |

Movement is relative to the mallet's team, so the directions reverse for red. Both agents share the same keyboard mapping when set to Heuristic Only. For an AI-versus-AI demonstration, assign a model to each mallet and set both to Inference Only.

The build scene list is empty in the repository. To create a standalone build, open **File > Build Settings**, add `AirHockeyArena.unity` to **Scenes In Build**, select a target platform, and build.

## Gameplay and physics

The arena contains a table, rail colliders, two goal triggers, a puck, and two mallets. The puck moves on the table's XZ plane without gravity or drag. Mallet collisions apply a strike impulse and transfer part of the mallet's velocity to the puck. Rails provide rebounds, while speed limits help keep the simulation controlled.

Each mallet stays on its own side of the center line. The environment controller resets the puck and mallets after a goal or a rally timeout, with randomized starting positions. It attempts to find obstacle-clear mallet spawns within the blue and red spawn fields, with a fallback if no suitable point is found.

The scene's `GameSettings` object exposes movement, strike strength, table bounds, observation scaling, and reward settings. Its saved speed limits are **16 units/second for mallets** and **35 units/second for the puck**. The arena's default rally limit is **25,000 physics steps**.

### Obstacles

| Object | Implemented behavior |
| --- | --- |
| **Boost Pad** | Multiplies the puck's planar velocity when it enters the trigger. The saved prefab uses a **1.5×** multiplier. |
| **Slow Zone** | Uses the same script as the boost pad with a **0.4×** velocity multiplier. |
| **Pop Bumper** | A solid obstacle that applies an outward impulse on contact with the puck. |
| **Generated Wall** | A solid rectangular barrier whose generated position and rotation change available routes and rebounds. |

Boost pads and slow zones are traversable triggers. Bumpers and generated walls count as blocking obstacles during generation. Obstacle scripts have their own speed caps, and the environment also enforces the scene's puck speed limit.

## Procedural arena generation

[WorldGen.cs](<src/Airhockey Arena/Assets/Scripts/WorldGen.cs>) divides a `GenerationField` into a grid and uses seeded, multi-octave Perlin noise to create layouts. Separate noise maps determine occupancy, obstacle type, and rotation. Seeded positional jitter varies placement within each cell.

The initial layout mirrors obstacle types, positions, and rotations across the table's center line. Generation then performs path and spacing checks:

1. Reserve cells using the configured goal-clear rows, puck-clear radius, and optional reserved regions.
2. Find low-cost grid routes from the center to both end rows. Blocking obstacles carry a higher path cost and are removed along the selected routes.
3. Instantiate the obstacles and remove blocking pairs that leave less than the configured mallet clearance. Clearance removal also removes the corresponding mirrored obstacle.

The saved arena prefab uses a **4 × 8 grid**, an **empty threshold of 0.4**, and **2.4 units of blocking clearance**. These are Inspector settings; the generator's script defaults and the test fixture use different values. The empty threshold controls noise-based occupancy, rather than guaranteeing a fixed percentage of empty cells.

The arena controller provides three useful configurations:

| Setting | Result |
| --- | --- |
| Disable `enableObstacleGeneration` | Play or train on a plain table. |
| `Manual` generation mode | Keep the generated layout across rally resets. Use a fixed seed to reproduce it. |
| `RandomSafeSeedEveryReset` generation mode | Generate a new layout and apply the path and clearance checks before each rally. This is the saved arena prefab's mode. |

`preferredObstacleType` and `preferredObstacleChance` bias occupied cells toward a particular obstacle for specialized training or evaluation. Generation runs independently of the agents; their actions only control mallet movement.

## ML-Agents opponents

[AirHockeyAgent.cs](<src/Airhockey Arena/Assets/Scripts/AirHockeyAgent.cs>) implements the `Mallet` behavior. Blue uses team ID `0`, and red uses team ID `1`.

### Actions and observations

The action space has **two discrete branches**, each with three choices:

| Branch | Choices |
| --- | --- |
| Forward movement | Stay, move forward, move backward. |
| Lateral movement | Stay, move right, move left. |

Both branches can act together for diagonal movement. The resulting direction is normalized before applying movement, and the mallet prefab requests a decision every five physics steps.

Agents receive **12 vector observations**: the two planar components of their position relative to the table center, their velocity, the puck's relative position and velocity, and the opponent's relative position and velocity. These values are scaled and expressed from the agent's team perspective.

The mallet prefab also configures a forward ray-perception sensor for goals, rails, agents, the puck, and all four obstacle types. It uses five rays per direction plus a center ray, a 20-unit ray length, and three observation stacks. A `ReverseRays` child is present with its sensor component disabled in the saved prefab. The source uses ML-Agents ray perception and direct puck observations; the proposal's custom wall-reflecting shot-path sensor was not implemented.

### Rewards and rally resets

The reward system encourages scoring and useful puck contact:

- **Scoring:** the scoring team receives `1 - elapsedSteps / MaxEnvironmentSteps`, clamped between `0` and `1`. With the step limit disabled, a goal awards `1`.
- **Conceding:** the other team receives `-1`.
- **Puck contact:** a base reward of `0.02` plus a launch-speed bonus of up to `0.08`, using the saved settings.
- **Repeated contact:** the first five hits per agent in a rally use multipliers of `1`, `0.6`, `0.35`, `0.2`, and `0.1`; later hits receive no contact reward.
- **Timeout:** active agents receive the configured penalty, saved as `-0.05`, when training episode handling is enabled.

With `endAgentEpisodesOnGoal` enabled, goals end both team episodes and timeouts interrupt them. Rally resets still occur when this option is disabled for human play or demonstration recording. Goals, puck hits, hit rewards, hit speeds, and training timeouts are recorded through the ML-Agents statistics recorder.

## Training and saved models

`Assets/Scenes/TRAININGAirHockeyArena.unity` contains **16 arena instances** for collecting experience. The repository includes PPO configurations for demonstration-assisted learning, self-play, randomized layouts, and obstacle specialization.

| Configuration | Purpose | Configured step budget | Saved run(s) |
| --- | --- | --- | --- |
| `BehavioralCloning.yaml` | PPO with behavioral cloning from `Demos/Mallet_0.demo`; cloning runs for the first 50,000 steps. | 500,000 | `MalletBC02` |
| `SelfPlay.yaml` | Competitive self-play. | 3,500,000 | `SelfPlay02` |
| `RandomSelfPlay.yaml` | Self-play configuration used for randomized arena training. | 1,000,000 | `RandomSelfPlay01` |
| `SpecialtySP.yaml` | Self-play configuration used for obstacle-focused training. | 500,000 per run | `BoostPref`, `SlowPref`, `BumperPref`, `WallPref` |

All four configurations use PPO with two hidden layers of 256 units. The self-play configurations keep a pool of past opponents and alternate teams. Arena generation mode and obstacle preferences are set in Unity; the YAML files configure the trainer.

Saved run metadata records `RandomSelfPlay01` initializing from `SelfPlay02`, and each specialty run initializing from `RandomSelfPlay01`. Each run directory contains an exported `Mallet.onnx`, checkpoints, configuration, and logs. Earlier experiments are retained under `Results/Old Results`. Models can be selected manually in a mallet's Behavior Parameters; obstacle preferences do not automatically select an opponent model.

### Running training

The saved training logs record **Python 3.10.12**, **`mlagents` and `mlagents-envs` 1.1.0**, **PyTorch 2.2.2+cu121**, and **NumPy 1.23.5**. These describe the recorded training environment; the repository does not include a Python environment lockfile.

Before starting a trainer, open the training scene and configure both mallets in every arena to use **Default** behavior, the shared behavior name **Mallet**, and distinct team IDs `0` and `1`. The checked-in arenas inherit the human-versus-AI prefab settings, so this change is required for self-play. Enable **End Agent Episodes On Goal** and **Spawn Red Mallet**, and leave demonstration recording off. Set the desired obstacle mode on the arenas.

With `mlagents-learn` available in your Python environment, run the following from the repository root to start a new randomized-layout training run from the included self-play checkpoint:

```powershell
Set-Location "src/Airhockey Arena/Assets/MLAgents"
mlagents-learn Configs/RandomSelfPlay.yaml --run-id MyRandomSelfPlay --initialize-from SelfPlay02 --results-dir Results
```

Set the arenas to `RandomSafeSeedEveryReset`, then press Play in Unity when the trainer waits for an environment connection. Use a new run ID for each experiment. To train a specialty, use `Configs/SpecialtySP.yaml`, initialize from `RandomSelfPlay01`, and set the desired obstacle preference in Unity.

For behavioral cloning, first replace the old absolute `demo_path` in `Configs/BehavioralCloning.yaml` with the following path, relative to the working directory above:

```yaml
demo_path: Demos/Mallet_0.demo
```

To record a new demonstration, use the single-arena scene, set the controlled mallet to Heuristic Only, enable its Demonstration Recorder, and turn off End Agent Episodes On Goal. The recorder is configured to write to `Assets/MLAgents/Demos`. Disabling Spawn Red Mallet supports blue-only demonstrations.

## Automated play-mode tests

[SafetyTests.cs](<src/Airhockey Arena/Assets/Tests/PlayModeTests/SafetyTests.cs>) implements the two generation checks proposed for the project:

| Test | What it checks |
| --- | --- |
| `GoalPathwayTest` | Searches a sampled grid for routes from the center to both goal-side edges and checks that each route segment is clear of blocking geometry. |
| `MalletFreedom` | Uses flood fill to require access to at least **80% of sampled open space** in each half and across the combined play area. |

The tests create a separate arena fixture with simple obstacle geometry, an **8 × 12 generation grid**, and a **28 × 40 sampling grid**. Each test checks three generated seeds. They assess layout connectivity and clearance; they do not measure learned-agent performance or simulate a complete scoring rally.

Run them from **Window > General > Test Runner > PlayMode**. To reproduce a seed set, launch Unity from an environment with `AIR_HOCKEY_SAFETY_TEST_SEED` set to an integer. The tests log the base seed and the generated world seeds.

## Source layout

All paths below are relative to `src/Airhockey Arena/Assets`.

| Path | Responsibility |
| --- | --- |
| `Scripts/AirHockeyEnvController.cs` | Arena setup, goals, resets, team bounds, spawn selection, and obstacle regeneration. |
| `Scripts/AirHockeyAgent.cs` | Agent observations, movement, keyboard control, puck strikes, and contact rewards. |
| `Scripts/AirHockeySettings.cs` | Shared physics, reward, reset, and observation settings. |
| `Scripts/PuckGoalController.cs` | Detects goal triggers and reports the scoring team. |
| `Scripts/WorldGen.cs` | Seeded layout generation, obstacle preferences, path carving, and clearance checks. |
| `Scripts/Obstacles/` | Boost/slow pad and bumper behavior. |
| `Scenes/` | Single-arena play and 16-arena training scenes. |
| `Prefabs/` | Arena, mallet, and obstacle prefabs. |
| `MLAgents/Configs/`, `MLAgents/Demos/`, `MLAgents/Results/` | Trainer settings, recorded play, and training artifacts. |
| `Tests/PlayModeTests/` | Procedural-generation safety tests. |
| `Imports/AirHockey/` | Imported table models, textures, materials, and related assets. |

## Project scope and documents

The implemented project covers the physical arena, all four planned obstacles, seeded procedural generation, ML-Agents training and inference, and both generation tests. A roguelike campaign, upgrades, automatic difficulty progression, and an on-screen match scoreboard remain outside the implemented scope. Goal events currently drive rewards, statistics, and rally resets.

- [Original project proposal](<doc/Final Project Proposal.pdf>)
- [Final project presentation](<doc/AirHockey Arena Presentation.pdf>)
