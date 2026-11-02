# How the enemy AI works

Notes from building this mod, game 0.3.1.1. They add to the enemy section of
[GAME_KNOWLEDGE.md](https://github.com/EvheniiS/GunmanContracts-Mods/blob/main/GAME_KNOWLEDGE.md) (senses, the position
leak, spawners, harmless shots), so those topics aren't repeated here. Everything is in `ANBBasicNPC` unless it says
otherwise. "Checked" means read in the disassembly; "seen" means observed in game logs.

## One frame

`UpdateExec` runs for every spawned enemy, visible or not:

1. `checkVisibility`, then `checkVisibilityRelatedActions` (hiding out-of-view enemies).
2. `checkAbilities` recomputes `canReact` and `canMove`. `canMove` false disables the NavMeshAgent. Hit stun, cover,
   `StayPutTime > 0` and custom animations all set it. Being in cover also sets `canReact` false. (checked)
3. Every `ActionPulseRate + 1` frames (6): `AttackLogic` = `AL_targetCheck`, camping check, `AL_tacticsCheck`,
   `AL_chasingCheck`, then fire if `attackReady`. `Navigation` runs on the same pulse. (checked)
4. `Locomotion` every frame: walk or run, stop, speed.

## Why they rush

Out of sight, an attacker runs to `lastKnownPosition` (your live position, see the leak) with
`agentStopDistanceNormal` 2 m. It only walks when it sees you, is in firing range or is within `attackWalkDistance`
(6 m). In sight it stops at `agentStopDistanceAttack` (10 m for guns). So the charge is the unseen chase, not the
fight. (checked, seen)

## Shooting and blind fire

- In sight and within `FiringDistance` (30 m): `attackReady`, stand still and shoot. Out of range but in sight it does
  neither, it just stands there. (checked, seen)
- `shootingIntervalTime` is the burst length, not a pause. `weaponFireRate` is the `WaitForSeconds` between trigger
  pulls. (checked)
- Blind fire is rolled once, on the pulse sight is lost, and only if the wall that blocked the view ray is at least
  10 m away: `blindfireTime = Random(blindfireMin, blindfireMax + 1)`, 0-4 s by default. While it runs the enemy
  stands and fires at `lastKnownPosition`. Cover, regaining sight or any state change clears it. (checked)
- `viewBlockMask` includes floors and ceilings. A ray from the floor below hits the ceiling right under you, so any
  "is there a wall near the target" test needs to look at the hit normal. (seen)

## Cover

- `LookForCover` takes the free `ANBCoverspot` nearest to the enemy that is within `coverLookRadius` of it, within
  `coverMaxDistanceToTarget` (10 m) of you, hidden from you by a blocker within 2 m of the spot's `hideCheckOrigin`
  (`CoverHidden`), and reachable without a path corner within 5 m of you (`pathSecure`, whose only caller is
  `LookForCover`). (checked)
- No search at all while you are closer than `noCoverOnTargetDistance` (7 m). (checked)
- In sight, the cover roll is `Random(0, 11) < coverChance` (7). A roll that finds nothing skips that pulse's
  "stand and shoot", so the enemy walks at you instead. That is a big part of the close-range rushing. (checked)
- In cover the enemy is blind and silent: no sight test, no shooting, agent off, for `coverTime` (3-13 s). Hits kick it
  out. `StartCover(spot)` is never called by the game but works; `ExitCover` throws if `currentCoverSpot` is null.
  (checked, seen)
- Since the ceiling counts as a blocker, a spot on the floor below you passes `CoverHidden`. (seen)
- Registered spots: Warehouse 168 (273 in the scene, the rest destroy themselves in `Awake`), The Range 36, Restaurant
  10, Outpost 0. (seen)

## Paused and frozen enemies

- `ANBEncounterSystem` keeps at most `DntMaxActive` enemies awake (takedown: `TDM_DntMaxActive`, 3). Above that it
  pauses the furthest ones first, attacking enemies included. A paused enemy wakes when a slot frees and you are within
  its `maxAutoWakeDistance`, or when you see it. So an enemy you wake far away gets paused again on the next check.
  Raising `DntMaxActive` fixes it. (checked, seen)
- Camping: stay within 2 m for 5 s in a fight and every attacker that neither sees you nor is seen gets `StayPutTime`.
  A running attacker can also freeze up to two enemies near it (`StayPut`, 50-120 s). (checked)
- Restarting a takedown reuses the scene, there's no scene load. Woken enemies often start with a stale
  `lastKnownPosition` far from you. (seen)

## Hits

- `GetHit` sets `overallHitTime` to `stunAtHitTime` (1.8 s) or `stunAtHitTimeLegs` (3 s); `checkAbilities` counts it
  down and clears `isGettingHit`. No reacting, moving or firing meanwhile. Writing `overallHitTime` shortens the stun.
  (checked)
- A hit on an enemy that didn't see you calls `StartAttack` first. Hits end cover and no blind fire follows. Shotguns
  call `TakeDamage` once per pellet. (checked)
- In play most enemies hit once die within 1-3 s, before a 1.8 s stun is over. (seen)

## Flanking

`stopFlanking` lowers the maximum number of flankers instead of the current count, so the limit doesn't hold.
(checked)
