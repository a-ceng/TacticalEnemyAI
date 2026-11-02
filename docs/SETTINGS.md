# Settings

Every setting, as shown in [Mod Settings](https://www.nexusmods.com/gunmancontractsstandalone/mods/28) (phone tile in VR,
Ctrl+M in flat) and as written in `UserData/MelonPreferences.cfg`. Changes apply at once unless the description says
otherwise. Every setting has a default button in Mod Settings, and "Reset section" resets a whole page.

Chances are 0-1 (0.3 = 30 %), times are in seconds, distances in metres.

## Main page: Tactical Enemy AI

Config section `[TacticalEnemyAI]`.

| In Mod Settings | Config key | Default | What it does |
|---|---|---|---|
| Mod enabled | `Enabled` | true | Master switch. Turned off mid-fight, the raised enemy limit and cover reach stay until the next map. |
| Blind fire | `SuppressFire` | true | Enemies that can't see you keep firing bursts at your position from cover. |
| Panic fire | `PanicFire` | true | An enemy you hit (and don't kill) may flinch and spray wildly at your area. |
| Panic chance | `PanicChance` | 0.8 | Chance that a hit makes an enemy panic-fire. |
| Back off chance | `BackOffChance` | 0.3 | Chance that an enemy you get close to backs off while shooting instead of standing its ground. 0 = never. |
| Wait before advancing: min (s) | `HoldMin` | 3 | After losing sight of you, an enemy holds and shoots at least this long before it may move up. |
| Wait before advancing: max (s) | `HoldMax` | 7 | ...and at most this long. Lower = more aggressive. |
| Enemies advancing at once | `MaxPushersMax` | 3 | Most enemies allowed to move up on you at the same time. Each time an advance ends the limit is re-rolled between this and "Advance: min movers". Higher = more aggressive. |
| How close they come (m) | `ContactDistance` | 15 | Enemies that can't see you walk toward you until this close (on your floor), then fight from cover. |
| Enemies awake at once | `MaxActive` | 6 | The game keeps at most this many enemies awake and pauses the rest (game: 3). Only raises: raising applies at once, lowering on the next map. 0 = game value. Higher costs CPU. |
| Takedown: send waiting enemies | `TakedownWake` | true | Once the fight starts, waiting enemies are sent at you so you don't have to search the map for them. |
| Takedown: enemies on you | `MinAttackers` | 4 | Keep at least this many enemies attacking you. 0 = only when the fight goes quiet. |

## Advanced page: Tactical Enemy AI (advanced)

Config section `[TacticalEnemyAI_Advanced]`.

### Contact and distance

| In Mod Settings | Config key | Default | What it does |
|---|---|---|---|
| Contact: time (s) | `ContactTime` | 8 | An enemy that saw you within this many seconds is "in contact" and fights from cover. Without contact it comes to find you. |
| Engage distance (m) | `EngageDistance` | 18 | While an enemy sees you it stops advancing at this distance (game: 10). Only raises. 0 = game value. |
| Far distance (m) | `FarDistance` | 30 | Beyond this distance from you, enemies may run when approaching or advancing, and their advances are longer. |

### Advancing

| In Mod Settings | Config key | Default | What it does |
|---|---|---|---|
| Advance: min movers | `MaxPushersMin` | 2 | Fewest enemies allowed to advance at the same time (the main page sets the most). |
| Advance: cover step (m) | `MaxCoverBound` | 12 | An advance to closer cover gains at most this many metres on you. |
| Advance: walk step (m) | `PushStep` | 6 | Metres an enemy walks per advance when no closer cover is available. |
| Advance: far walk step (m) | `FarPushStep` | 20 | Metres per advance beyond Far distance. |
| Impatient: enemies left | `FewEnemies` | 2 | With this many enemies or fewer left alive, they stop being patient: shorter holds, and all may advance. |
| Impatient: time factor | `AggressiveFactor` | 0.4 | Hold and in-cover times are multiplied by this while enemies are impatient (few left, or nobody has seen you for Quiet time). |
| Quiet time (s) | `QuietTime` | 12 | Seconds without any enemy seeing you before enemies get impatient and waiting takedown enemies are woken. |

### Cover and peeking

| In Mod Settings | Config key | Default | What it does |
|---|---|---|---|
| Cover: max from you (m) | `CoverMaxToYou` | 30 | Cover spots may be up to this far from you (game: 10). Only raises. Applies to enemies that start fighting after the change. |
| Cover: search radius (m) | `CoverSearchRadius` | 20 | Enemies look for cover up to this far from themselves (game: 10-15). Only raises. Applies to enemies that start fighting after the change. |
| Cover: min from you (m) | `MinCoverDistance` | 5 | Cover spots closer to you than this are never used (the game's own limit is 5 m). Enemies this close may also search for cover (game: 7; that part applies to enemies that start fighting after the change). |
| Cover: hide min (s) | `CoverHideMin` | 2 | Shortest time an enemy stays down in cover before peeking out. |
| Cover: hide max (s) | `CoverHideMax` | 5 | Longest time it stays down. |
| Peek: min (s) | `PeekMin` | 3 | Shortest time an enemy stays out of cover shooting before ducking back. |
| Peek: max (s) | `PeekMax` | 6 | Longest time it stays out. |
| Peek: step out (m) | `PeekStepMax` | 3.5 | How far an enemy steps out from its cover or corner to get a view of you. |
| Cover: switch chance | `CoverSwitchChance` | 0.35 | Chance that after peeking it moves to a different cover spot instead of the same one. |

### Blind fire

| In Mod Settings | Config key | Default | What it does |
|---|---|---|---|
| Blind fire: burst min (s) | `BurstMin` | 2.5 | Shortest blind-fire burst. |
| Blind fire: burst max (s) | `BurstMax` | 5 | Longest blind-fire burst. |
| Blind fire: pause min (s) | `PauseMin` | 0.5 | Shortest pause between bursts. |
| Blind fire: pause max (s) | `PauseMax` | 1.5 | Longest pause between bursts. |
| Blind fire: scatter (m) | `SuppressScatter` | 1 | Blind fire aims at a random point up to this many metres around you. |
| Blind fire: clear line (m) | `ClearLine` | 5 | Blind fire only along a firing line free of walls for this many metres, so an enemy doesn't shoot into its own cover. |
| Blind fire: wall near you (m) | `WallNearYou` | 12 | Blind fire only when the shots reach you or hit a wall (not a floor or ceiling) within this many metres of you. 0 = any wall. |

### Panic fire

| In Mod Settings | Config key | Default | What it does |
|---|---|---|---|
| Panic: flinch (s) | `PanicStun` | 0.2 | A panicking enemy's hit stun is cut to this (game: 1.8). Enemies that don't panic keep the game's stun. |
| Panic: leg hits | `LegHitStun` | Auto | Leg hits on a panicking enemy. Auto: keep the game's knee-down stun only when Knee Shot Stun is installed. Keep: always keep it. Cut: short flinch like other hits. |
| Panic: min (s) | `PanicMin` | 3 | Shortest panic fire. |
| Panic: max (s) | `PanicMax` | 6 | Longest panic fire. |
| Panic: fire delay factor | `PanicFireRate` | 0.5 | Time between trigger pulls is multiplied by this while panicking (0.5 = twice as fast). |
| Panic: spread factor | `PanicSpread` | 6 | Panic fire spread multiplier (1 = normal accuracy). |
| Panic: scatter (m) | `PanicScatter` | 6 | Blind panic fire aims at a random point up to this many metres around you (and up to 3 m high). |

### Close range

| In Mod Settings | Config key | Default | What it does |
|---|---|---|---|
| Close range (m) | `CloseDistance` | 10 | When you come this close to an enemy that sees you, it decides once whether to stand its ground or back off. |
| Back off: min (m) | `BackOffMin` | 6 | Shortest back-off move. |
| Back off: max (m) | `BackOffMax` | 10 | Longest back-off move. |

### Takedown and awake enemies

| In Mod Settings | Config key | Default | What it does |
|---|---|---|---|
| Keep fighters awake | `KeepActive` | true | Enemies attacking you are never paused by the game's active-enemy limit, its camping freeze or nav blocks. |
| Takedown: wake interval (s) | `WakeInterval` | 3 | Seconds between enemies sent at you. |

### Debug

| In Mod Settings | Config key | Default | What it does |
|---|---|---|---|
| Debug overlay | `Overlay` | false | Flat screen (in VR only on the mirror window): every enemy's state above its head and a box around it, seen through walls. The overlay key toggles it too. |
| Debug overlay key | `OverlayKey` | F8 | Key that toggles the overlay (Input System key name, e.g. F8, F9, Backquote). Change it in the cfg file. |
| Debug overlay size | `OverlaySize` | 0.5 | Size of the overlay's text, boxes and lines, relative to 1080p (scales with your screen height). Smallest 0.5; raise it for recording videos. |
| Debug log | `DebugLog` | false | Writes cover, peeks, advances, panic and wake-ups to `UserData/TacticalEnemyAI/session-<time>.log`, for bug reports. Needs a game restart. |
