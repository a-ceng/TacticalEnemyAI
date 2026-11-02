using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2Cpp;
using Il2CppHurricaneVR.Framework.Weapons.Guns;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.AI;

[assembly: MelonInfo(typeof(TacticalEnemyAI.TacticalEnemyAIMod), "Tactical Enemy AI", "1.0.0", "ACeng")]
[assembly: MelonGame("ANB_Seth", "GunmanContracts")]

namespace TacticalEnemyAI
{
    // Enemies fight from cover instead of rushing you: they take cover when they lose sight of you, step out to a spot
    // with a view of you and shoot, keep up blind fire on your position, switch cover now and then, and advance cover
    // to cover one or two at a time. Hit by you, they usually panic-fire. How the game's AI works:
    // notes/game-ai-mechanics.md.
    //
    // What the game does (0.3.1.1, checked in disassembly):
    // - In sight and in range an enemy stands and shoots. Out of sight it runs to your live position.
    // - Blind fire (shoot at lastKnownPosition without sight) happens once, when sight is lost, for 0-4 s, and only
    //   when the wall that blocked its view is at least 10 m away (so it doesn't shoot into its own cover).
    // - LookForCover only accepts spots within 10 m of you, and an enemy in cover is blind and never shoots.
    // - Extra enemies are paused when more than a few are active (dynamic NPC toggle); campers freeze unseen attackers.
    //
    // What this mod does, per enemy attacking you with a gun:
    // - Contact: it saw you within ContactTime s, or is within ContactDistance. Without contact it doesn't use cover at
    //   all: it walks (runs beyond FarDistance) toward you until it sees you or gets within ContactDistance. Everything
    //   below is how it fights while in contact.
    // - Cover rules: spots up to CoverMaxToYou from you, never closer than MinCoverDistance, and a cover taken while
    //   holding stays within 3 m of the enemy's own distance to you, or up to 6 m further when it is within 12 m
    //   (pathSecure filter; the game itself never uses a spot or path within 5 m of you). A failed cover roll in sight no
    //   longer makes it walk at you.
    // - Peek cycle: in cover CoverHideMin..Max s, then out to a peek spot within PeekStepMax from which it sees you (or
    //   whose shots hit a wall within WallNearYou of you), shoots for PeekMin..Max s, then back into the same spot or
    //   (CoverSwitchChance) another one.
    // - Lost sight: tries cover at once and every few seconds; meanwhile holds and fires bursts at you, but only when
    //   the shots reach you or a wall within WallNearYou of you; otherwise it steps out to a peek spot first.
    // - Advance: after HoldMin..Max s without sight (or while it sees you out of firing range), if fewer than 2..3
    //   enemies are moving, it bounds: to a cover spot closer to you (at most MaxCoverBound closer), else walks PushStep
    //   (FarPushStep beyond FarDistance). The game's flanking counts as a bound. With FewEnemies or fewer left, or after
    //   QuietTime without anyone seeing you, holds shrink (AggressiveFactor) and everyone may advance.
    // - In sight it stops advancing at EngageDistance. You within CloseDistance: stands or (BackOffChance) backs off.
    // - Panic fire: hit by you (PanicChance), the hit stun is cut to PanicStun (leg hits: LegHitStun) and it sprays at your area for
    //   PanicMin..Max s, faster (PanicFireRate) and wilder (PanicSpread, PanicScatter, high shots), seen or not.
    // - The game's active-enemy limit is raised to MaxActive. Enemies it controls are never paused or frozen by the
    //   game. In takedown, once the fight is on, waiting enemies are woken until MinAttackers are attacking you, and
    //   also when it goes quiet.
    public class TacticalEnemyAIMod : MelonMod
    {
        internal static MelonLogger.Instance Log;
        internal static MelonPreferences_Entry<bool> Enabled, DebugLog, SuppressFire, PanicFire, KeepActive, TakedownWake, Overlay;
        internal static MelonPreferences_Entry<string> OverlayKey, LegHitStun;
        internal static MelonPreferences_Entry<float> OverlaySize, EngageDistance, HoldMin, HoldMax, PushStep, FarDistance, FarPushStep,
            CoverMaxToYou, CoverSearchRadius, MinCoverDistance, MaxCoverBound, CoverHideMin, CoverHideMax, PeekMin, PeekMax,
            PeekStepMax, ClearLine, CoverSwitchChance, BurstMin, BurstMax, PauseMin, PauseMax, SuppressScatter, PanicMin,
            PanicMax, PanicSpread, PanicScatter, PanicChance, PanicStun, WallNearYou, CloseDistance, BackOffChance,
            BackOffMin, BackOffMax, QuietTime, WakeInterval, AggressiveFactor, ContactTime, ContactDistance, PanicFireRate;
        internal static MelonPreferences_Entry<int> MaxPushersMin, MaxPushersMax, FewEnemies, MinAttackers, MaxActive;

        static StreamWriter File;

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;
            // Two pages in Mod Settings (phone tile / Ctrl+M): the main one for everyone, the rest under "(advanced)".
            // Shown in this order, with these names. Identifiers are the cfg keys: never rename or move them between
            // categories (saved values would be lost).
            var c = MelonPreferences.CreateCategory("TacticalEnemyAI", "Tactical Enemy AI");
            var a = MelonPreferences.CreateCategory("TacticalEnemyAI_Advanced", "Tactical Enemy AI (advanced)");
            MelonPreferences_Entry<T> E<T>(MelonPreferences_Category cat, string id, T def, string name, string desc) => cat.CreateEntry(id, def, name, desc);

            // ---- main page
            Enabled = E(c, "Enabled", true, "Mod enabled", "Master switch. Turned off mid-fight, the raised enemy limit and cover reach stay until the next map.");
            SuppressFire = E(c, "SuppressFire", true, "Blind fire", "Enemies that can't see you keep firing bursts at your position from cover.");
            PanicFire = E(c, "PanicFire", true, "Panic fire", "An enemy you hit (and don't kill) may flinch and spray wildly at your area.");
            PanicChance = E(c, "PanicChance", 0.8f, "Panic chance", "Chance (0-1) that a hit makes an enemy panic-fire.");
            BackOffChance = E(c, "BackOffChance", 0.3f, "Back off chance", "Chance (0-1) that an enemy you get close to backs off while shooting, instead of standing its ground. 0 = never.");
            HoldMin = E(c, "HoldMin", 3f, "Wait before advancing: min (s)", "After losing sight of you, an enemy holds and shoots at least this long before it may move up...");
            HoldMax = E(c, "HoldMax", 7f, "Wait before advancing: max (s)", "...and at most this long. Lower = more aggressive.");
            MaxPushersMax = E(c, "MaxPushersMax", 3, "Enemies advancing at once", "Most enemies allowed to move up on you at the same time (re-rolled down to the advanced minimum). Higher = more aggressive.");
            ContactDistance = E(c, "ContactDistance", 15f, "How close they come (m)", "Enemies that can't see you walk toward you until this close (on your floor), then fight from cover.");
            MaxActive = E(c, "MaxActive", 6, "Enemies awake at once", "The game keeps at most this many enemies awake and pauses the rest (game: 3). Only raises: raising applies at once, lowering on the next map. 0 = game value. Higher costs CPU.");
            TakedownWake = E(c, "TakedownWake", true, "Takedown: send waiting enemies", "Once the fight starts, waiting enemies are sent at you so you don't have to search the map for them.");
            MinAttackers = E(c, "MinAttackers", 4, "Takedown: enemies on you", "Keep at least this many enemies attacking you. 0 = only when the fight goes quiet.");

            // ---- advanced page
            ContactTime = E(a, "ContactTime", 8f, "Contact: time (s)", "An enemy that saw you within this many seconds is in contact: it fights from cover. Without contact it comes to find you.");
            EngageDistance = E(a, "EngageDistance", 18f, "Engage distance (m)", "While an enemy sees you it stops advancing at this distance (game: 10). Only raises. 0 = game value.");
            FarDistance = E(a, "FarDistance", 30f, "Far distance (m)", "Beyond this distance from you enemies may run when approaching or advancing, and advances are longer.");

            MaxPushersMin = E(a, "MaxPushersMin", 2, "Advance: min movers", "Fewest enemies allowed to advance at the same time (the main page sets the most).");
            MaxCoverBound = E(a, "MaxCoverBound", 12f, "Advance: cover step (m)", "An advance to closer cover gains at most this many metres on you.");
            PushStep = E(a, "PushStep", 6f, "Advance: walk step (m)", "Metres an enemy walks per advance when no closer cover is available.");
            FarPushStep = E(a, "FarPushStep", 20f, "Advance: far walk step (m)", "Metres per advance beyond Far distance.");
            FewEnemies = E(a, "FewEnemies", 2, "Impatient: enemies left", "With this many enemies or fewer left alive, they stop being patient: shorter holds, all may advance.");
            AggressiveFactor = E(a, "AggressiveFactor", 0.4f, "Impatient: time factor", "Hold and in-cover times are multiplied by this when few enemies are left or nobody has seen you for Quiet time.");
            QuietTime = E(a, "QuietTime", 12f, "Quiet time (s)", "Seconds without any enemy seeing you before enemies get impatient and waiting takedown enemies are woken.");

            CoverMaxToYou = E(a, "CoverMaxToYou", 30f, "Cover: max from you (m)", "Cover spots may be up to this far from you (game: 10). Only raises. Applies to enemies that start fighting after the change.");
            CoverSearchRadius = E(a, "CoverSearchRadius", 20f, "Cover: search radius (m)", "Enemies look for cover spots up to this far from themselves (game: 10-15). Only raises. Applies to enemies that start fighting after the change.");
            MinCoverDistance = E(a, "MinCoverDistance", 5f, "Cover: min from you (m)", "Cover spots closer to you than this are never used (the game's own floor is 5 m). Enemies this close may also search for cover (game: 7; that part applies to enemies that start fighting after the change).");
            CoverHideMin = E(a, "CoverHideMin", 2f, "Cover: hide min (s)", "Seconds an enemy stays down in cover before peeking out...");
            CoverHideMax = E(a, "CoverHideMax", 5f, "Cover: hide max (s)", "...and at most.");
            PeekMin = E(a, "PeekMin", 3f, "Peek: min (s)", "Seconds an enemy stays out of cover shooting before ducking back...");
            PeekMax = E(a, "PeekMax", 6f, "Peek: max (s)", "...and at most.");
            PeekStepMax = E(a, "PeekStepMax", 3.5f, "Peek: step out (m)", "How far an enemy steps out from its cover or corner to get a view of you.");
            CoverSwitchChance = E(a, "CoverSwitchChance", 0.35f, "Cover: switch chance", "Chance (0-1) that after peeking it moves to a different cover spot instead of the same one.");

            BurstMin = E(a, "BurstMin", 2.5f, "Blind fire: burst min (s)", "Blind-fire burst length...");
            BurstMax = E(a, "BurstMax", 5f, "Blind fire: burst max (s)", "...and at most.");
            PauseMin = E(a, "PauseMin", 0.5f, "Blind fire: pause min (s)", "Pause between blind-fire bursts...");
            PauseMax = E(a, "PauseMax", 1.5f, "Blind fire: pause max (s)", "...and at most.");
            SuppressScatter = E(a, "SuppressScatter", 1f, "Blind fire: scatter (m)", "Blind fire aims at a random point up to this many metres around you.");
            ClearLine = E(a, "ClearLine", 5f, "Blind fire: clear line (m)", "Blind fire only along a firing line free of walls for this many metres (no shooting into its own cover).");
            WallNearYou = E(a, "WallNearYou", 12f, "Blind fire: wall near you (m)", "Blind fire only when the shots reach you or hit a wall (not a floor or ceiling) within this many metres of you. 0 = any wall.");

            PanicStun = E(a, "PanicStun", 0.2f, "Panic: flinch (s)", "A panicking enemy's hit stun is cut to this (game: 1.8). Enemies that don't panic keep the game's stun; for leg hits see the next setting.");
            LegHitStun = E(a, "LegHitStun", "Auto", "Panic: leg hits", "Leg hits on a panicking enemy: Auto (keep the game's knee-down stun only when Knee Shot Stun is installed), Keep (always keep it) or Cut (short flinch like other hits).");
            PanicMin = E(a, "PanicMin", 3f, "Panic: min (s)", "Panic fire length...");
            PanicMax = E(a, "PanicMax", 6f, "Panic: max (s)", "...and at most.");
            PanicFireRate = E(a, "PanicFireRate", 0.5f, "Panic: fire delay factor", "Time between trigger pulls is multiplied by this while panicking (0.5 = twice as fast).");
            PanicSpread = E(a, "PanicSpread", 6f, "Panic: spread factor", "Panic fire spread multiplier (1 = normal accuracy).");
            PanicScatter = E(a, "PanicScatter", 6f, "Panic: scatter (m)", "Blind panic fire aims at a random point up to this many metres around you (and up to 3 m high).");

            CloseDistance = E(a, "CloseDistance", 10f, "Close range (m)", "When you come this close to an enemy that sees you, it decides once whether to stand its ground or back off.");
            BackOffMin = E(a, "BackOffMin", 6f, "Back off: min (m)", "Shortest back-off move...");
            BackOffMax = E(a, "BackOffMax", 10f, "Back off: max (m)", "...and longest.");

            KeepActive = E(a, "KeepActive", true, "Keep fighters awake", "Enemies attacking you are never paused by the game's active-enemy limit, its camping freeze or nav blocks.");
            WakeInterval = E(a, "WakeInterval", 3f, "Takedown: wake interval (s)", "Seconds between enemies sent at you.");

            Overlay = E(a, "Overlay", false, "Debug overlay", "Flat screen (VR: mirror window only): every enemy's state above its head and a box around it, seen through walls. The overlay key toggles it too.");
            OverlayKey = E(a, "OverlayKey", "F8", "Debug overlay key", "Key that toggles the overlay (Input System key name, e.g. F8, F9, Backquote). Edit it in the cfg.");
            OverlaySize = E(a, "OverlaySize", 0.5f, "Debug overlay size", "Size of the overlay's text, boxes and lines, relative to 1080p (scales with your screen height). Smallest 0.5; raise it for recording videos.");
            DebugLog = E(a, "DebugLog", false, "Debug log", "Write cover, peeks, advances, panic and wake-ups to UserData\\TacticalEnemyAI\\session-<time>.log (for bug reports). Needs a game restart.");
            if (DebugLog.Value)
            {
                try
                {
                    string dir = Path.Combine(MelonEnvironment.UserDataDirectory, "TacticalEnemyAI");
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                    File = new StreamWriter(path) { AutoFlush = true };
                    Log.Msg($"loaded - logging to {path}");
                }
                catch (Exception ex) { Log.Warning($"no log file: {ex.Message}"); }
            }
            else Log.Msg("loaded");
        }

        public override void OnApplicationQuit() => File?.Dispose();

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            Brain.Reset();
            D($"---- scene '{sceneName}' ----");
        }

        public override void OnUpdate()
        {
            if (Enabled.Value) Brain.Tick();
            DebugOverlay.CheckKey();
        }

        public override void OnGUI() => DebugOverlay.Draw();

        internal static void D(string s)
        {
            if (File == null) return;
            try { File.WriteLine($"[t={Time.time,7:0.0}] {s}"); } catch { }
        }

        static readonly HashSet<string> warned = new();
        internal static void Warn(string where, Exception ex)
        {
            if (warned.Add(where + ex.GetType().Name)) Log.Warning($"{where}: {ex.Message}");
        }

        // Leg hits on a panicking enemy keep the game's knee-down stun (LegHitStun). Auto: only with Knee Shot Stun,
        // which re-applies that stun every frame and would fight the panic flinch.
        internal static bool KeepLegStun(ANBBasicNPC n)
        {
            if (!n.gettingHitLegs) return false;
            string m = LegHitStun.Value?.Trim() ?? "";
            if (m.Equals("Cut", StringComparison.OrdinalIgnoreCase)) return false;
            if (m.Equals("Keep", StringComparison.OrdinalIgnoreCase)) return true;
            return KneeShotStunInstalled();
        }

        static bool? kneeShotStun;
        static bool KneeShotStunInstalled()
        {
            if (kneeShotStun == null)
            {
                try { kneeShotStun = MelonBase.RegisteredMelons.Any(m => m?.Info?.Name == "Knee Shot Stun"); }
                catch { kneeShotStun = false; }
                if (kneeShotStun.Value) Log.Msg("Knee Shot Stun found: leg hits keep its knee-down stun");
            }
            return kneeShotStun.Value;
        }

        internal static float R(MelonPreferences_Entry<float> lo, MelonPreferences_Entry<float> hi) =>
            UnityEngine.Random.Range(lo.Value, Mathf.Max(lo.Value, hi.Value));
    }

    internal static class Brain
    {
        static void D(string s) => TacticalEnemyAIMod.D(s);

        internal enum Mode { None, Engaged, Covering, Peek, Hold, Bound, BackOff, Approach }
        internal enum BoundKind { Cover, Walk, Flank }
        internal enum Search { Hold, Advance }

        internal class Npc
        {
            public ANBBasicNPC N;
            public int Id;
            public bool Dead, Tuned;
            public Mode M;
            public float ModeT;
            public float HoldUntil;               // earliest time it may advance
            public float CoverPeekAt, PeekUntil;
            public BoundKind BK;
            public Vector3 BoundFrom;
            public float BoundStep;
            public Vector3 MovePos;
            public float MoveT;                   // last time it moved 0.5 m
            public bool HasMove;                  // walking to MoveTarget (peek spot or back-off point)
            public Vector3 MoveTarget;
            public float MoveStartT;
            public bool Blocked;                  // wanted to blind-fire but its own cover/corner is in the way
            public float NextPeekTry;
            public bool CloseRolled;
            public float NextCoverTry;
            public bool FireOn;
            public float FireToggleAt;
            public bool PanicArmed;
            public float PanicLen, PanicUntil, HitT;
            public float FireRateSaved;           // weaponFireRate before panic (0 = not changed)
            public float LastSeen = -100f;        // last time it saw you (contact)
            public float NextUnpause;
            public int LastHitFrame = -1;
            public ANBCoverspot LastSpot;
            public string Tag => $"E{Id}";
        }

        internal static readonly Dictionary<IntPtr, Npc> Npcs = new();
        static int nextId = 1;
        static float nextTick;
        internal static int maxPushers = -1;
        static float lastAnySight = -1f, nextWake;
        internal static bool aggressive, fight;  // fight: an enemy has attacked you this round

        // Set while the mod's own LookForCover call runs, so the pathSecure filter knows the rules for it.
        internal static Npc Searching;
        internal static Search SearchRule;
        internal static Vector3 ExcludeSpot;
        internal static bool HasExclude;

        internal static void Reset()
        {
            Npcs.Clear();
            nextId = 1;
            maxPushers = -1;
            lastAnySight = -1f;
            nextWake = 0f;
            aggressive = false;
            fight = false;
            Searching = null;
        }

        static bool Same(Component a, Component b) => a != null && b != null && a.Pointer == b.Pointer;
        static float Flat(Vector3 a, Vector3 b) { a.y = 0; b.y = 0; return Vector3.Distance(a, b); }
        static bool OnFloorOf(Vector3 a, Vector3 b) => Mathf.Abs(a.y - b.y) <= 1.5f;

        // The body moves with the NavMeshAgent's object; ANBBasicNPC.transform stays at the spawn point.
        internal static Vector3 Body(ANBBasicNPC n)
        {
            try { var t = n.agentTransform; if (t != null) return t.position; } catch { }
            return n.transform.position;
        }

        internal static Transform PlayerT()
        {
            try { return ANBStaticGameManager.ANBmain?.PlayerHitTarget; } catch { return null; }
        }

        internal static Npc Get(ANBBasicNPC n) => n != null && Npcs.TryGetValue(n.Pointer, out var s) ? s : null;

        static Npc Track(ANBBasicNPC n)
        {
            if (Npcs.TryGetValue(n.Pointer, out var s)) return s;
            s = new Npc { N = n, Id = nextId++ };
            Npcs[n.Pointer] = s;
            return s;
        }

        // Attacking you with a gun in hand (not disarmed, not running to pick one up).
        internal static bool Managed(ANBBasicNPC n) =>
            n.isEnemy && n.isAttacking && !n.isDead && !n.isFleeing && n.currentWeapon != "melee" && n.HVRgunBase != null
            && !n.isGoingForGun && !n.isPickingUpGun && Same(n.attackTarget, n.Playertarget);

        static void SetMode(Npc s, Mode m, string why)
        {
            if (s.M == m) return;
            if (s.M == Mode.Bound) RollPushers();
            if (m != Mode.Peek && m != Mode.BackOff) s.HasMove = false;
            s.M = m;
            s.ModeT = Time.time;
            if (why != null) D($"{s.Tag} {m}: {why}");
        }

        static float HoldTime() =>
            TacticalEnemyAIMod.R(TacticalEnemyAIMod.HoldMin, TacticalEnemyAIMod.HoldMax) * (aggressive ? TacticalEnemyAIMod.AggressiveFactor.Value : 1f);

        static int Movers()
        {
            int k = 0;
            foreach (var s in Npcs.Values) if (!s.Dead && s.M == Mode.Bound) k++;
            return k;
        }

        static bool SlotFree()
        {
            if (aggressive) return true;
            if (maxPushers < 0) RollPushers();
            return Movers() < maxPushers;
        }

        static void RollPushers()
        {
            int lo = Mathf.Max(0, TacticalEnemyAIMod.MaxPushersMin.Value);
            maxPushers = UnityEngine.Random.Range(lo, Mathf.Max(lo, TacticalEnemyAIMod.MaxPushersMax.Value) + 1);
        }

        // ------------------------------------------------------------------ per tick (10 Hz)

        internal static void Tick()
        {
            if (Time.time < nextTick) return;
            nextTick = Time.time + 0.1f;
            var player = PlayerT();
            if (player == null) return;
            Vector3 pp = player.position;
            float now = Time.time;
            int attackers = 0;
            try
            {
                var list = ANBStaticGameManager.ANBmain?.encounterSystem?.allEnemies;
                if (list == null) return;
                int alive = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var n = list[i];
                    if (n == null || !n.isEnemy || n.isDead) continue;
                    alive++;
                    if (Managed(n)) attackers++;
                }
                // A takedown restart reuses the scene: everyone dead (or not spawned yet) starts a new round.
                if (alive == 0 && (fight || lastAnySight >= 0f))
                {
                    fight = false;
                    lastAnySight = -1f;
                    D("round over");
                }
                if (attackers > 0 && !fight) { fight = true; D("fight is on"); }
                RaiseActiveLimit();
                bool quiet = lastAnySight >= 0f && now - lastAnySight > TacticalEnemyAIMod.QuietTime.Value;
                bool aggr = lastAnySight >= 0f && (alive <= TacticalEnemyAIMod.FewEnemies.Value || quiet);
                if (aggr != aggressive)
                {
                    aggressive = aggr;
                    D(aggr ? $"enemies get impatient ({alive} left{(quiet ? $", nobody has seen you for {now - lastAnySight:0} s" : "")})" : "enemies are patient again");
                }
                for (int i = 0; i < list.Count; i++)
                {
                    var n = list[i];
                    if (n == null || !n.isEnemy) continue;
                    var s = Track(n);
                    try { Update(s, n, pp, now); }
                    catch (Exception ex) { TacticalEnemyAIMod.Warn("update", ex); }
                }
            }
            catch (Exception ex) { TacticalEnemyAIMod.Warn("enemy list", ex); }
            try { Wake(pp, now, attackers); } catch (Exception ex) { TacticalEnemyAIMod.Warn("wake", ex); }
        }

        // The game keeps DntMaxActive enemies awake and pauses the furthest beyond that, which also re-paused every
        // far enemy the takedown wake-up sent at you. Raise it (only raises; takedown sets 3).
        static void RaiseActiveLimit()
        {
            int max = TacticalEnemyAIMod.MaxActive.Value;
            var es = ANBStaticGameManager.ANBmain?.encounterSystem;
            if (max <= 0 || es == null || !es.useDynamicNPCToggle || es.DntMaxActive >= max) return;
            D($"active-enemy limit {es.DntMaxActive:0} -> {max}");
            es.DntMaxActive = max;
        }

        static void Update(Npc s, ANBBasicNPC n, Vector3 pp, float now)
        {
            if (n.isDead)
            {
                if (!s.Dead) { s.Dead = true; SetMode(s, Mode.None, null); D($"{s.Tag} died"); }
                return;
            }
            if (s.Dead) { s.Dead = false; s.Tuned = false; RestoreFireRate(s, n); }    // back from the pool
            if (!n.gameObject.activeInHierarchy) return;
            if (!Managed(n)) { if (s.M != Mode.None) SetMode(s, Mode.None, "not attacking you"); RestoreFireRate(s, n); return; }

            if (!s.Tuned) Tune(s, n);
            Vector3 np = Body(n);
            if (Vector3.Distance(np, s.MovePos) > 0.5f) { s.MovePos = np; s.MoveT = now; }
            float dist = Flat(np, pp);
            bool seen = n.targetInSight;
            if (seen) { lastAnySight = now; s.LastSeen = now; }
            if (s.M == Mode.None)
            {
                if (!seen) s.LastSeen = -100f;
                SetMode(s, seen ? Mode.Engaged : Mode.Hold, "starts attacking");
            }
            // An enemy attacking you must not sit paused by the game's active-enemy limit.
            if (n.NPCPaused && now >= s.NextUnpause)
            {
                s.NextUnpause = now + 2f;
                try { ANBStaticGameManager.ANBmain.encounterSystem.toggleDynamicNPC(n, true); D($"{s.Tag} was paused by the game: woken"); }
                catch (Exception ex) { TacticalEnemyAIMod.Warn("unpause", ex); }
            }

            if (s.PanicArmed)
            {
                // Leg hits may keep the game's knee-down stun (LegHitStun).
                if (n.isGettingHit) { if (!TacticalEnemyAIMod.KeepLegStun(n)) n.overallHitTime = Mathf.Min(n.overallHitTime, Mathf.Max(0.05f, s.HitT + TacticalEnemyAIMod.PanicStun.Value - now)); }
                else
                {
                    s.PanicArmed = false;
                    s.PanicUntil = now + s.PanicLen;
                    s.HasMove = false;
                    if (s.FireRateSaved <= 0f && n.weaponFireRate > 0f)
                    {
                        s.FireRateSaved = n.weaponFireRate;
                        n.weaponFireRate *= Mathf.Clamp(TacticalEnemyAIMod.PanicFireRate.Value, 0.1f, 1f);
                    }
                    if (s.M == Mode.Bound || s.M == Mode.BackOff || s.M == Mode.Approach) { SetMode(s, seen ? Mode.Engaged : Mode.Hold, null); s.HoldUntil = now + HoldTime(); }
                    D($"{s.Tag} panic fire {s.PanicLen:0.0} s ({dist:0.0} m, {(seen ? "sees you" : "blind")})");
                }
            }
            if (now >= s.PanicUntil) RestoreFireRate(s, n);
            // Panicking: stands and sprays (AfterTargetCheck); no cover, peeks or advances until it's over.
            if (now < s.PanicUntil && !n.isCovering) return;

            // Distance only counts on your floor: under you it would hold and blind-fire into the ceiling.
            bool contact = now - s.LastSeen < TacticalEnemyAIMod.ContactTime.Value
                || (dist <= TacticalEnemyAIMod.ContactDistance.Value && OnFloorOf(np, pp - Vector3.up * 1.5f));

            // In cover: the game runs it (blind, silent). The mod times the peeks.
            if (n.isCovering)
            {
                if (!contact)
                {
                    LeaveCover(s, n, now, null);
                    StartApproach(s, n, pp, dist);
                    return;
                }
                if (s.M != Mode.Covering && !(s.M == Mode.Bound && s.BK == BoundKind.Cover)) SetMode(s, Mode.Covering, null);
                if (n.isInCover)
                {
                    if (s.M == Mode.Bound) { SetMode(s, Mode.Covering, "reached cover"); s.HoldUntil = now + HoldTime(); }
                    if (s.CoverPeekAt <= 0f)
                        s.CoverPeekAt = now + TacticalEnemyAIMod.R(TacticalEnemyAIMod.CoverHideMin, TacticalEnemyAIMod.CoverHideMax) * (aggressive ? TacticalEnemyAIMod.AggressiveFactor.Value : 1f);
                    s.LastSpot = n.currentCoverSpot;
                    if (now >= s.CoverPeekAt) Peek(s, n, np, pp, now, dist, fromCover: true);
                }
                else if (now - s.ModeT > 12f && now - s.MoveT > 3f) LeaveCover(s, n, now, "never reached its cover");
                return;
            }
            s.CoverPeekAt = 0f;
            if (s.M == Mode.Covering) SetMode(s, seen ? Mode.Engaged : Mode.Hold, "cover ended");
            if (s.M == Mode.Bound && s.BK == BoundKind.Cover) SetMode(s, Mode.Hold, "lost its cover on the way");

            if (s.HasMove) UpdateMove(s, n, np, now);
            if (s.M == Mode.BackOff && s.HasMove) return;
            if (s.M == Mode.BackOff) SetMode(s, seen ? Mode.Engaged : Mode.Hold, "after backing off");

            if (seen)
            {
                if (s.M == Mode.Hold || s.M == Mode.Approach || (s.M == Mode.Bound && n.targetInRange)) SetMode(s, Mode.Engaged, $"sees you ({dist:0.0} m)");
                CloseCheck(s, n, np, pp, dist, now);
                if (s.M == Mode.BackOff) return;
                if (s.M == Mode.Bound) { UpdateBound(s, n, np, now, dist); return; }
                // Sees you but too far to shoot (FiringDistance): don't peek and duck forever, close in.
                bool peekDone = s.M == Mode.Peek && !s.HasMove && now >= s.PeekUntil;
                if (!n.targetInRange && (s.M == Mode.Engaged || peekDone) && now >= s.HoldUntil && SlotFree())
                {
                    StartBound(s, n, np, pp, now, dist, "out of firing range");
                    return;
                }
                if (peekDone) ReCover(s, n, now, dist);
                else if (s.M == Mode.Engaged && now >= s.NextCoverTry)
                {
                    s.NextCoverTry = now + 6f;
                    TryCover(s, n, Search.Hold, null, "under fire");
                }
                return;
            }

            if (dist > TacticalEnemyAIMod.CloseDistance.Value + 4f) s.CloseRolled = false;
            if (s.M == Mode.Engaged)
            {
                SetMode(s, Mode.Hold, "lost sight of you");
                s.HoldUntil = now + HoldTime();
                s.NextCoverTry = now + 3f;
                if (TryCover(s, n, Search.Hold, null, "lost sight of you")) return;
            }

            // No contact: no cover games far away behind walls, come and find you.
            if (s.M == Mode.Approach)
            {
                if (contact)
                {
                    SetMode(s, Mode.Hold, $"close enough ({dist:0.0} m)");
                    s.HoldUntil = now + HoldTime();
                    s.NextCoverTry = now;
                }
                else
                {
                    n.lastKnownPosition = pp;
                    return;
                }
            }
            else if (!contact && !s.HasMove && (s.M == Mode.Hold || s.M == Mode.Peek || (s.M == Mode.Bound && s.BK == BoundKind.Walk)))
            {
                StartApproach(s, n, pp, dist);
                return;
            }

            switch (s.M)
            {
                case Mode.Peek:
                    if (s.HasMove) break;
                    if (s.Blocked && now >= s.NextPeekTry)
                    {
                        s.NextPeekTry = now + 2f;
                        if (!FindPeek(n, np, pp, out Vector3 p, out bool clear)) { s.PeekUntil = now; D($"{s.Tag} no view of you from here"); }
                        else if (Flat(p, np) > 0.5f) StartMove(s, p, now, clear ? "steps out further" : "steps out for a clearer shot");
                    }
                    if (now < s.PeekUntil) break;
                    if (now >= s.HoldUntil && SlotFree()) StartBound(s, n, np, pp, now, dist, null);
                    else ReCover(s, n, now, dist);
                    break;
                case Mode.Hold:
                    if (now >= s.HoldUntil && SlotFree()) { StartBound(s, n, np, pp, now, dist, null); break; }
                    if (now >= s.NextCoverTry)
                    {
                        s.NextCoverTry = now + 4f;
                        if (TryCover(s, n, Search.Hold, null, "holding")) break;
                    }
                    // Its corner blocks the shot: step out to where it can shoot (lean around the corner).
                    if (s.Blocked && now >= s.NextPeekTry)
                    {
                        s.NextPeekTry = now + 3f;
                        Peek(s, n, np, pp, now, dist, fromCover: false);
                    }
                    break;
                case Mode.Bound:
                    UpdateBound(s, n, np, now, dist);
                    break;
            }
        }

        // Walks (runs beyond FarDistance, see BeforeLocomotion) along the game's own attack path to your position.
        static void StartApproach(Npc s, ANBBasicNPC n, Vector3 pp, float dist)
        {
            s.HasMove = false;
            n.lastKnownPosition = pp;
            SetMode(s, Mode.Approach, $"no contact, comes to find you ({dist:0.0} m)");
        }

        static void RestoreFireRate(Npc s, ANBBasicNPC n)
        {
            if (s.FireRateSaved <= 0f) return;
            n.weaponFireRate = s.FireRateSaved;
            s.FireRateSaved = 0f;
        }

        // Per enemy, once: let it use cover further from you and look further for it.
        static void Tune(Npc s, ANBBasicNPC n)
        {
            s.Tuned = true;
            n.coverMaxDistanceToTarget = Mathf.Max(n.coverMaxDistanceToTarget, TacticalEnemyAIMod.CoverMaxToYou.Value);
            n.coverLookRadius = Mathf.Max(n.coverLookRadius, TacticalEnemyAIMod.CoverSearchRadius.Value);
            n.noCoverOnTargetDistance = Mathf.Min(n.noCoverOnTargetDistance, TacticalEnemyAIMod.MinCoverDistance.Value);
        }

        // ------------------------------------------------------------------ short moves (peek spot, back-off point)

        static void StartMove(Npc s, Vector3 target, float now, string why)
        {
            s.HasMove = true;
            s.MoveTarget = target;
            s.MoveStartT = s.MoveT = now;
            if (why != null) D($"{s.Tag} {why} ({Flat(Body(s.N), target):0.0} m)");
        }

        static void UpdateMove(Npc s, ANBBasicNPC n, Vector3 np, float now)
        {
            float limit = s.M == Mode.BackOff ? 8f : 4f;
            string end = Flat(np, s.MoveTarget) <= (s.M == Mode.BackOff ? 1.5f : 0.6f) ? "in position"
                       : now - s.MoveT >= 2f && now - s.MoveStartT >= 2f ? "stuck"
                       : now - s.MoveStartT > limit ? "timed out" : null;
            if (end == null) return;
            s.HasMove = false;
            if (s.M == Mode.Peek)
            {
                s.PeekUntil = Mathf.Max(s.PeekUntil, now + TacticalEnemyAIMod.R(TacticalEnemyAIMod.PeekMin, TacticalEnemyAIMod.PeekMax));
                s.FireOn = false; s.FireToggleAt = now;
                if (end != "in position") D($"{s.Tag} peek move {end}");
            }
            else D($"{s.Tag} back-off ends: {end}");
        }

        // A spot within PeekStepMax (sideways, or slightly toward you) from which it sees your head; failing that, one
        // whose line to you stays clear for ClearLine metres (blind fire then reaches your cover, not its own).
        static readonly float[,] PeekOffsets = { { 0f, 0f }, { 1.2f, 0f }, { 2.2f, 0f }, { 3.2f, 0f }, { 1.5f, 1f }, { 2.5f, 1f }, { 0.8f, 1.5f } };

        internal static bool FindPeek(ANBBasicNPC n, Vector3 from, Vector3 head, out Vector3 result, out bool clear)
        {
            result = from; clear = false;
            Vector3 dir = head - from; dir.y = 0;
            if (dir.sqrMagnitude < 0.01f) return false;
            dir.Normalize();
            Vector3 side = Vector3.Cross(Vector3.up, dir);
            float step = Mathf.Max(0.5f, TacticalEnemyAIMod.PeekStepMax.Value);
            int mask = n.viewBlockMask.value;
            int area = NavMesh.AllAreas;
            try { if (n.agent != null) area = n.agent.areaMask; } catch { }
            float bestFree = -1f;
            Vector3 best = from;
            float first = UnityEngine.Random.value < 0.5f ? 1f : -1f;
            for (int i = 0; i < PeekOffsets.GetLength(0); i++)
            {
                for (int k = 0; k < 2; k++)
                {
                    float lat = PeekOffsets[i, 0] * (k == 0 ? first : -first), fwd = PeekOffsets[i, 1];
                    if (i == 0 && k == 1) continue;
                    if (Mathf.Abs(lat) > step || fwd > step) continue;
                    Vector3 p = from;
                    if (i > 0)
                    {
                        if (!NavMesh.SamplePosition(from + side * lat + dir * fwd, out NavMeshHit hit, 0.8f, area)) continue;
                        p = hit.position;
                        if (!OnFloorOf(p, from)) continue;
                        if (!NavMesh.CalculatePath(from, p, area, path) || path.status != NavMeshPathStatus.PathComplete || PathLength(path) > step * 2f) continue;
                    }
                    Vector3 eye = p + Vector3.up * 1.5f;
                    Vector3 v = head - eye;
                    float len = v.magnitude;
                    if (!Physics.Raycast(eye, v / len, out RaycastHit rh, len - 0.4f, mask, QueryTriggerInteraction.Ignore))
                    {
                        result = p; clear = true;
                        return true;
                    }
                    // Blind: the wall it hits must be near you (your cover), the nearer the better.
                    float score = rh.distance >= TacticalEnemyAIMod.ClearLine.Value && WallNearYou(rh, head) ? 1000f - Vector3.Distance(rh.point, head) : -1f;
                    if (score > bestFree) { bestFree = score; best = p; }
                }
            }
            if (bestFree < 0f) return false;
            result = best;
            return true;
        }

        static float PathLength(NavMeshPath p)
        {
            var c = p.corners;
            float len = 0f;
            for (int i = 1; i < c.Length; i++) len += Vector3.Distance(c[i - 1], c[i]);
            return len;
        }

        // A wall (not a floor or ceiling: no shooting at you through floors) within WallNearYou of you.
        static bool WallNearYou(RaycastHit wall, Vector3 you)
        {
            if (Mathf.Abs(wall.normal.y) > 0.5f) return false;
            float r = TacticalEnemyAIMod.WallNearYou.Value;
            return r <= 0f || Vector3.Distance(wall.point, you) <= r;
        }

        // Its own firing line: the first wall must be at least ClearLine away from it and a wall within WallNearYou of you.
        static bool FireLineClear(ANBBasicNPC n, Vector3 aim)
        {
            Vector3 o;
            try { o = n.gunMuzzleFixed != null ? n.gunMuzzleFixed.position : Body(n) + Vector3.up * 1.4f; }
            catch { o = Body(n) + Vector3.up * 1.4f; }
            Vector3 v = aim - o;
            float len = v.magnitude;
            if (len < 1.5f) return true;
            if (!Physics.Raycast(o, v / len, out RaycastHit rh, len - 0.5f, n.viewBlockMask.value, QueryTriggerInteraction.Ignore)) return true;
            return rh.distance >= Mathf.Min(TacticalEnemyAIMod.ClearLine.Value, len - 1f) && WallNearYou(rh, aim);
        }

        // ------------------------------------------------------------------ cover

        // The game's own search (nearest free spot that hides it from you), under the mod's pathSecure rules.
        static bool TryCover(Npc s, ANBBasicNPC n, Search rule, ANBCoverspot exclude, string why)
        {
            try
            {
                var agent = n.agent;
                if (agent == null || !agent.enabled || n.isCovering || n.attackTarget == null || n.isGettingHit) return false;
                n.currentDistanceToTarget = n.currentDistanceToPlayer;    // AL_targetCheck only updates it in sight
                Searching = s;
                SearchRule = rule;
                HasExclude = exclude != null;
                if (HasExclude) ExcludeSpot = exclude.transform.position;
                n.LookForCover();
            }
            catch (Exception ex) { TacticalEnemyAIMod.Warn("LookForCover", ex); }
            finally { Searching = null; HasExclude = false; }
            if (!n.isCovering) return false;
            s.HasMove = false;
            var pt = PlayerT();
            float d = pt != null && n.currentCoverSpot != null ? Flat(n.currentCoverSpot.transform.position, pt.position) : -1f;
            if (rule == Search.Advance) { s.BK = BoundKind.Cover; SetMode(s, Mode.Bound, $"advances to cover {d:0.0} m from you ({why})"); }
            else SetMode(s, Mode.Covering, $"takes cover {d:0.0} m from you ({why})");
            return true;
        }

        // pathSecure postfix: one call per candidate spot that passed the game's own tests.
        internal static bool AllowSpot(ANBBasicNPC n, Vector3 you, Vector3 spot)
        {
            float dSpot = Flat(spot, you);
            if (dSpot < TacticalEnemyAIMod.MinCoverDistance.Value) return false;
            var pt = PlayerT();                                             // a spot under you "hides" behind the ceiling
            if (pt != null && !OnFloorOf(spot, pt.position - Vector3.up * 1.5f)) return false;
            var s = Searching != null && Searching.N.Pointer == n.Pointer ? Searching : null;
            if (s != null && HasExclude && Vector3.Distance(spot, ExcludeSpot) < 0.5f) return false;
            float dSelf = Flat(Body(n), you);
            if (s != null && SearchRule == Search.Advance)
                return dSpot <= dSelf - 3f && dSpot >= dSelf - TacticalEnemyAIMod.MaxCoverBound.Value;
            // Holding: about as far from you as it is now; up close it may step back to cover (no standing in the open).
            return dSpot >= dSelf - 3f && dSpot <= dSelf + (dSelf < 12f ? 6f : 3f);
        }

        // Out of cover (or out from behind its corner) to a spot with a view of you, then shoot there for a while.
        static void Peek(Npc s, ANBBasicNPC n, Vector3 np, Vector3 pp, float now, float dist, bool fromCover)
        {
            if (fromCover)
            {
                try { if (n.currentCoverSpot != null) n.ExitCover(); }
                catch (Exception ex) { TacticalEnemyAIMod.Warn("ExitCover", ex); }
            }
            s.CoverPeekAt = 0f;
            s.Blocked = false;
            s.NextPeekTry = now + 2f;
            s.FireOn = false; s.FireToggleAt = now;
            bool found = FindPeek(n, np, pp, out Vector3 p, out bool clear);
            if (!fromCover && !found) return;                              // nowhere better: keep holding
            SetMode(s, Mode.Peek, $"{(fromCover ? "peeks out" : "leans out")} ({dist:0.0} m{(found ? clear ? ", has a view of you" : ", blind but clear line" : ", no view")})");
            s.PeekUntil = now + TacticalEnemyAIMod.R(TacticalEnemyAIMod.PeekMin, TacticalEnemyAIMod.PeekMax);
            if (found && Flat(p, np) > 0.5f) StartMove(s, p, now, null);
        }

        static void LeaveCover(Npc s, ANBBasicNPC n, float now, string why)
        {
            try { if (n.currentCoverSpot != null) n.ExitCover(); }
            catch (Exception ex) { TacticalEnemyAIMod.Warn("ExitCover", ex); }
            SetMode(s, n.targetInSight ? Mode.Engaged : Mode.Hold, why);
        }

        // Back into cover after a peek: the same spot, or now and then a different one.
        static void ReCover(Npc s, ANBBasicNPC n, float now, float dist)
        {
            var old = s.LastSpot;
            s.LastSpot = null;
            bool sw = old == null || UnityEngine.Random.value < TacticalEnemyAIMod.CoverSwitchChance.Value;
            if (sw && TryCover(s, n, Search.Hold, old, old == null ? "after leaning out" : "switches cover")) return;
            try
            {
                var agent = n.agent;
                if (old != null && old.spotFree && agent != null && agent.enabled && !n.isCovering
                    && old.CoverHidden(n, n.attackTarget, false, old.hideCheckOrigin))
                {
                    n.StartCover(old);
                    if (n.isCovering) { SetMode(s, Mode.Covering, "ducks back"); return; }
                }
            }
            catch (Exception ex) { TacticalEnemyAIMod.Warn("StartCover", ex); }
            if (TryCover(s, n, Search.Hold, null, "its spot is no good")) return;
            SetMode(s, n.targetInSight ? Mode.Engaged : Mode.Hold, "no cover, holds here");
        }

        // ------------------------------------------------------------------ advancing

        static void StartBound(Npc s, ANBBasicNPC n, Vector3 np, Vector3 pp, float now, float dist, string why)
        {
            string w = $"{Movers() + 1}/{(aggressive ? "all" : maxPushers.ToString())} moving{(why != null ? ", " + why : "")}";
            if (TryCover(s, n, Search.Advance, s.LastSpot, w)) return;
            s.BK = BoundKind.Walk;
            s.BoundFrom = s.MovePos = np;
            s.MoveT = now;
            s.BoundStep = dist > TacticalEnemyAIMod.FarDistance.Value ? TacticalEnemyAIMod.FarPushStep.Value : TacticalEnemyAIMod.PushStep.Value;
            SetMode(s, Mode.Bound, $"walks {s.BoundStep:0} m toward you ({w}, you {dist:0.0} m)");
        }

        static void UpdateBound(Npc s, ANBBasicNPC n, Vector3 np, float now, float dist)
        {
            string end = null;
            if (s.BK == BoundKind.Flank)
            {
                if (!n.isFlanking) end = "flank done";
                else if (now - s.ModeT > 20f) { try { n.stopFlanking(); } catch (Exception ex) { TacticalEnemyAIMod.Warn("stopFlanking", ex); } end = "flank timed out"; }
            }
            else
            {
                end = Flat(np, s.BoundFrom) >= s.BoundStep ? "walked its step"
                    : now - s.MoveT >= 3f && now - s.ModeT >= 3f ? "stuck"
                    : now - s.ModeT > 20f ? "timed out" : null;
            }
            if (end == null) return;
            D($"{s.Tag} advance ends: {end} ({dist:0.0} m from you)");
            SetMode(s, n.targetInSight ? Mode.Engaged : Mode.Hold, "after advancing");
            s.HoldUntil = now + HoldTime();
            s.NextCoverTry = now;                                           // look for cover right away
        }

        // ------------------------------------------------------------------ close range

        static void CloseCheck(Npc s, ANBBasicNPC n, Vector3 np, Vector3 pp, float dist, float now)
        {
            float close = TacticalEnemyAIMod.CloseDistance.Value;
            if (dist > close + 4f) { s.CloseRolled = false; return; }
            if (dist > close || s.CloseRolled || !OnFloorOf(np, pp - Vector3.up * 1.5f) || s.M == Mode.BackOff) return;
            s.CloseRolled = true;
            if (UnityEngine.Random.value >= TacticalEnemyAIMod.BackOffChance.Value) { D($"{s.Tag} stands its ground ({dist:0.0} m)"); return; }
            if (!FindBackOff(n, np, pp, dist, out Vector3 p)) { D($"{s.Tag} wanted to back off, no room: stands ({dist:0.0} m)"); return; }
            try { if (n.isStrafing) n.stopStrafing(); } catch (Exception ex) { TacticalEnemyAIMod.Warn("stopStrafing", ex); }
            try { if (n.isFlanking) n.stopFlanking(); } catch (Exception ex) { TacticalEnemyAIMod.Warn("stopFlanking", ex); }
            SetMode(s, Mode.BackOff, $"backs off {Flat(np, p):0.0} m (you {dist:0.0} m -> {Flat(p, pp):0.0} m)");
            StartMove(s, p, now, null);
        }

        static readonly NavMeshPath path = new NavMeshPath();

        // A reachable point BackOffMin..BackOffMax away, further from you, whose path never comes much closer to you.
        static bool FindBackOff(ANBBasicNPC n, Vector3 np, Vector3 pp, float dist, out Vector3 result)
        {
            result = np;
            Vector3 away = np - pp; away.y = 0;
            if (away.sqrMagnitude < 0.01f) away = Vector3.forward;
            away.Normalize();
            float lo = TacticalEnemyAIMod.BackOffMin.Value, hi = Mathf.Max(lo, TacticalEnemyAIMod.BackOffMax.Value);
            int mask = NavMesh.AllAreas;
            try { if (n.agent != null) mask = n.agent.areaMask; } catch { }
            for (int i = 0; i < 14; i++)
            {
                float spread = i < 8 ? 60f : 100f;
                Vector3 dir = Quaternion.Euler(0, UnityEngine.Random.Range(-spread, spread), 0) * away;
                Vector3 cand = np + dir * UnityEngine.Random.Range(lo, hi);
                try
                {
                    if (!NavMesh.SamplePosition(cand, out NavMeshHit hit, 2f, mask)) continue;
                    Vector3 p = hit.position;
                    if (!OnFloorOf(p, np) || Flat(p, pp) < dist + 2f) continue;
                    if (!NavMesh.CalculatePath(np, p, mask, path) || path.status != NavMeshPathStatus.PathComplete) continue;
                    bool ok = true;
                    var corners = path.corners;
                    for (int k = 0; k < corners.Length && ok; k++) ok = Flat(corners[k], pp) >= dist * 0.75f;
                    if (!ok) continue;
                    result = p;
                    return true;
                }
                catch (Exception ex) { TacticalEnemyAIMod.Warn("back-off path", ex); return false; }
            }
            return false;
        }

        // ------------------------------------------------------------------ takedown wake-up

        // Takedown keeps most enemies paused or idle until you walk near them. Once the fight is on, while fewer than
        // MinAttackers attack you or the fight goes quiet, send the nearest waiting one at you every WakeInterval (it
        // then follows the mod's rules like everyone else).
        static void Wake(Vector3 pp, float now, int attackers)
        {
            if (!TacticalEnemyAIMod.TakedownWake.Value || !fight || now < nextWake) return;
            bool quiet = lastAnySight >= 0f && now - lastAnySight >= TacticalEnemyAIMod.QuietTime.Value;
            bool few = attackers < TacticalEnemyAIMod.MinAttackers.Value;
            if (!quiet && !few) return;
            var gl = ANBStaticGameManager.ANBmain;
            if (gl == null || !gl.MainPlaymodeTakedown) return;
            var es = gl.encounterSystem;
            var list = es?.allEnemies;
            if (list == null) return;
            nextWake = now + TacticalEnemyAIMod.WakeInterval.Value;
            ANBBasicNPC best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                var n = list[i];
                if (n == null || n.isDead || !n.isEnemy || !n.NPCStarted || n.isAttacking) continue;
                float d = Flat(Body(n), pp);
                if (d < bestD) { bestD = d; best = n; }
            }
            if (best == null) return;
            bool paused = best.NPCPaused;
            if (paused) es.toggleDynamicNPC(best, true);
            if (!best.isAttacking) best.attackPlayer();
            best.lastKnownPosition = pp;                                    // it starts with a stale one (often far away)
            D($"wakes a waiting enemy {bestD:0.0} m from you ({(paused ? "was paused" : "was idle")}; {(few ? $"{attackers} attacking you" : $"quiet {now - lastAnySight:0} s")})");
        }

        // ------------------------------------------------------------------ hook decisions

        // AL_targetCheck postfix: blind fire. Panic first, then suppression bursts while holding, peeking or backing off,
        // only along a firing line that isn't blocked right in front of it.
        internal static void AfterTargetCheck(ANBBasicNPC n)
        {
            var s = Get(n);
            if (s == null || s.Dead || !Managed(n) || n.isCovering) return;
            var pt = PlayerT();
            if (pt == null) return;
            float now = Time.time;
            if (TacticalEnemyAIMod.PanicFire.Value && now < s.PanicUntil)
            {
                // In sight the game already stands and fires at you (ShotError widens it); blind, spray your area.
                if (!n.targetInSight)
                {
                    n.blindfireTime = Mathf.Max(n.blindfireTime, 0.5f);
                    Vector2 d = UnityEngine.Random.insideUnitCircle * TacticalEnemyAIMod.PanicScatter.Value;
                    n.lastKnownPosition = pt.position + new Vector3(d.x, UnityEngine.Random.Range(-1.2f, 3f), d.y);   // some go high
                }
                return;
            }
            if (n.targetInSight) return;
            bool canFire = s.M == Mode.Hold || s.M == Mode.BackOff || (s.M == Mode.Peek && !s.HasMove);
            if (!canFire) { n.blindfireTime = 0f; return; }                 // moving, not shooting
            if (!TacticalEnemyAIMod.SuppressFire.Value) return;
            if (now >= s.FireToggleAt)
            {
                s.FireOn = !s.FireOn;
                s.FireToggleAt = now + (s.FireOn ? TacticalEnemyAIMod.R(TacticalEnemyAIMod.BurstMin, TacticalEnemyAIMod.BurstMax)
                                                : TacticalEnemyAIMod.R(TacticalEnemyAIMod.PauseMin, TacticalEnemyAIMod.PauseMax));
            }
            if (!s.FireOn) return;
            Vector3 aim = Around(pt.position, TacticalEnemyAIMod.SuppressScatter.Value);
            if (!FireLineClear(n, aim))
            {
                s.Blocked = true;                                          // Update steps it out to a peek spot
                n.blindfireTime = 0f;
                return;
            }
            s.Blocked = false;
            n.blindfireTime = Mathf.Max(n.blindfireTime, 0.3f);
            n.lastKnownPosition = aim;
        }

        // Your head height minus 0.6 m (where the game's own blind fire aims), scattered.
        static Vector3 Around(Vector3 head, float r)
        {
            Vector2 d = UnityEngine.Random.insideUnitCircle * r;
            return head + new Vector3(d.x, -0.6f + UnityEngine.Random.Range(-0.4f, 0.4f), d.y);
        }

        internal static float ShotError(ANBBasicNPC n, float error)
        {
            var s = Get(n);
            if (s == null || !TacticalEnemyAIMod.PanicFire.Value || Time.time >= s.PanicUntil) return error;
            return Mathf.Max(error, 2f) * TacticalEnemyAIMod.PanicSpread.Value;
        }

        // TakeDamage: prefix reads whether it saw you before the game's StartAttack resets it; postfix rolls panic and
        // cuts the hit stun (the game's 1.8 s outlasted most enemies: you shoot again before it ends).
        internal struct HitState { public bool Valid, Saw; }

        internal static void BeforeDamage(ANBBasicNPC n, ANBHVRGunBase gun, ref HitState st)
        {
            if (!TacticalEnemyAIMod.Enabled.Value || !TacticalEnemyAIMod.PanicFire.Value || gun == null || gun.EnemyGun || n.isDead || !n.isEnemy) return;
            st.Valid = true;
            st.Saw = n.isAttacking && n.targetInSight && Same(n.attackTarget, n.Playertarget);
        }

        internal static void AfterDamage(ANBBasicNPC n, HitState st)
        {
            if (!st.Valid || n.isDead || n.currentWeapon == "melee") return;
            var s = Track(n);
            if (s.LastHitFrame == Time.frameCount || Time.time < s.PanicUntil || (s.PanicArmed && Time.time - s.HitT < 5f)) return;   // shotgun pellets
            s.LastHitFrame = Time.frameCount;
            string saw = st.Saw ? "saw you" : "didn't see you";
            if (UnityEngine.Random.value >= TacticalEnemyAIMod.PanicChance.Value) { D($"{s.Tag} hit ({saw}): keeps its head"); return; }
            s.PanicArmed = true;
            s.HitT = Time.time;
            s.PanicLen = TacticalEnemyAIMod.R(TacticalEnemyAIMod.PanicMin, TacticalEnemyAIMod.PanicMax);
            bool legs = TacticalEnemyAIMod.KeepLegStun(n);
            if (n.isGettingHit && !legs) n.overallHitTime = Mathf.Min(n.overallHitTime, TacticalEnemyAIMod.PanicStun.Value);
            D($"{s.Tag} hit ({saw}): panic fire after {(legs ? "the game's leg stun" : $"a {TacticalEnemyAIMod.PanicStun.Value:0.0} s flinch")}");
        }

        // A failed cover roll in sight leaves the game's "stand and shoot" unset, so the enemy walks at you that pulse.
        internal static void AfterLookForCover(ANBBasicNPC n)
        {
            if (Searching != null || n.isCovering || !n.targetInSight || !n.targetInRange || !Managed(n)) return;
            n.canMove = false;
        }

        // While held by the mod it must not give up and start the game's hunt (running around near you).
        internal static bool AllowLoseTarget(ANBBasicNPC n)
        {
            var s = Get(n);
            return s == null || s.Dead || s.M == Mode.None || !Managed(n);
        }

        // The game's flank (out of sight) counts as an advance: only after the hold and with a free slot.
        internal static bool AllowFlank(ANBBasicNPC n)
        {
            var s = Get(n);
            if (s == null || s.M == Mode.None || !Managed(n)) return true;
            if (s.M == Mode.Bound) return s.BK == BoundKind.Flank;
            if (s.M != Mode.Hold || Time.time < s.HoldUntil || !SlotFree()) return false;
            s.BK = BoundKind.Flank;
            SetMode(s, Mode.Bound, $"flanks ({Movers() + 1} moving)");
            return true;
        }

        internal static bool AllowStrafe(ANBBasicNPC n)
        {
            var s = Get(n);
            return s == null || !s.HasMove;
        }

        // checkVisibilityRelatedActions / checkAbilities prefixes: never paused (outOfViewTime feeds the game's
        // active-enemy limit), never frozen by the camping check or nav blocks (StayPutTime).
        internal static void KeepActive(ANBBasicNPC n, bool stayPut)
        {
            if (!TacticalEnemyAIMod.Enabled.Value || !TacticalEnemyAIMod.KeepActive.Value) return;
            var s = Get(n);
            if (s == null || s.Dead || s.M == Mode.None || n.isDead) return;
            if (stayPut) { if (n.StayPutTime > 0f) n.StayPutTime = 0f; }
            else n.outOfViewTime = 0f;
        }

        // Locomotion: holding/peeking out of sight = movement off (the game's own "can't move" branch, agent stays on);
        // stepping to a peek spot or backing off = movement on even while it shoots; out of sight and near = walk.
        internal struct LocoState { public bool Touched, Move; public float Walk; }

        internal static void BeforeLocomotion(ANBBasicNPC n, ref LocoState st)
        {
            if (!TacticalEnemyAIMod.Enabled.Value) return;
            var s = Get(n);
            if (s == null || s.Dead || s.M == Mode.None || s.M == Mode.Covering) return;
            bool unseen = !n.targetInSight;
            bool move = s.HasMove && !n.isCovering && !n.isGettingHit;
            bool hold = !move && unseen && (s.M == Mode.Hold || s.M == Mode.Peek) && !n.isCovering && !n.isGettingHit;
            bool far = (s.M == Mode.Bound || s.M == Mode.Approach) && n.currentDistanceToPlayer > TacticalEnemyAIMod.FarDistance.Value;
            bool walk = (unseen || move) && !n.isCovering && !far;
            if (!hold && !move && !walk) return;
            st.Touched = true;
            st.Move = n.canMove;
            st.Walk = n.attackWalkDistance;
            if (hold) n.canMove = false;
            if (move) n.canMove = true;
            if (walk) n.attackWalkDistance = 10000f;
        }

        internal static void AfterLocomotion(ANBBasicNPC n, LocoState st)
        {
            if (!st.Touched) return;
            n.canMove = st.Move;
            n.attackWalkDistance = st.Walk;
        }

        // Navigation: in sight, stop at EngageDistance; stepping to a peek spot or backing off, go to that point (stop
        // distances small, destination re-set after the game's own SetDestination). Restored after the call.
        internal struct NavState { public bool Touched; public float Attack, Normal; }

        internal static void BeforeNavigation(ANBBasicNPC n, ref NavState st)
        {
            if (!TacticalEnemyAIMod.Enabled.Value || !n.isAttacking) return;
            var s = Get(n);
            if (s != null && s.HasMove)
            {
                st.Touched = true;
                st.Attack = n.agentStopDistanceAttack;
                st.Normal = n.agentStopDistanceNormal;
                n.agentStopDistanceAttack = n.agentStopDistanceNormal = 0.3f;
                return;
            }
            float engage = TacticalEnemyAIMod.EngageDistance.Value;
            if (engage <= 0f || !n.targetInSight || n.agentStopDistanceAttack >= engage || n.currentWeapon == "melee") return;
            st.Touched = true;
            st.Attack = n.agentStopDistanceAttack;
            st.Normal = n.agentStopDistanceNormal;
            n.agentStopDistanceAttack = engage;
        }

        internal static void AfterNavigation(ANBBasicNPC n, NavState st)
        {
            if (st.Touched)
            {
                n.agentStopDistanceAttack = st.Attack;
                n.agentStopDistanceNormal = st.Normal;
            }
            var s = Get(n);
            if (s == null || !s.HasMove || n.isCovering) return;
            var agent = n.agent;
            if (agent != null && agent.enabled && agent.isOnNavMesh && (agent.destination - s.MoveTarget).sqrMagnitude > 0.25f)
                agent.SetDestination(s.MoveTarget);
        }
    }

    // ------------------------------------------------------------------ Harmony

    [HarmonyLib.HarmonyPatch(typeof(ANBBasicNPC), nameof(ANBBasicNPC.AL_targetCheck))]
    internal static class TargetCheckPatch
    {
        static void Postfix(ANBBasicNPC __instance)
        {
            if (!TacticalEnemyAIMod.Enabled.Value) return;
            try { Brain.AfterTargetCheck(__instance); }
            catch (Exception ex) { TacticalEnemyAIMod.Warn("AL_targetCheck", ex); }
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(ANBBasicNPC), nameof(ANBBasicNPC.getShotErrorRate))]
    internal static class ShotErrorPatch
    {
        static void Postfix(ANBBasicNPC __instance, ref float __result)
        {
            if (!TacticalEnemyAIMod.Enabled.Value) return;
            try { __result = Brain.ShotError(__instance, __result); }
            catch (Exception ex) { TacticalEnemyAIMod.Warn("getShotErrorRate", ex); }
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(ANBBasicNPC), nameof(ANBBasicNPC.TakeDamage))]
    internal static class TakeDamagePatch
    {
        static void Prefix(ANBBasicNPC __instance, ANBHVRGunBase originGun, out Brain.HitState __state)
        {
            __state = default;
            try { Brain.BeforeDamage(__instance, originGun, ref __state); }
            catch (Exception ex) { TacticalEnemyAIMod.Warn("TakeDamage prefix", ex); }
        }

        static void Postfix(ANBBasicNPC __instance, Brain.HitState __state)
        {
            try { Brain.AfterDamage(__instance, __state); }
            catch (Exception ex) { TacticalEnemyAIMod.Warn("TakeDamage postfix", ex); }
        }
    }

    // pathSecure's only caller is LookForCover: one call per candidate cover spot.
    [HarmonyLib.HarmonyPatch(typeof(ANBBasicNPC), nameof(ANBBasicNPC.pathSecure))]
    internal static class PathSecurePatch
    {
        static void Postfix(ANBBasicNPC __instance, Vector3 avoidPos, Vector3 targetPos, ref bool __result)
        {
            if (!__result || !TacticalEnemyAIMod.Enabled.Value) return;
            try { __result = Brain.AllowSpot(__instance, avoidPos, targetPos); }
            catch (Exception ex) { TacticalEnemyAIMod.Warn("pathSecure", ex); }
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(ANBBasicNPC), nameof(ANBBasicNPC.LookForCover))]
    internal static class LookForCoverPatch
    {
        static void Postfix(ANBBasicNPC __instance)
        {
            if (!TacticalEnemyAIMod.Enabled.Value) return;
            try { Brain.AfterLookForCover(__instance); }
            catch (Exception ex) { TacticalEnemyAIMod.Warn("LookForCover", ex); }
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(ANBBasicNPC), nameof(ANBBasicNPC.LooseTarget))]
    internal static class LooseTargetPatch
    {
        static bool Prefix(ANBBasicNPC __instance)
        {
            if (!TacticalEnemyAIMod.Enabled.Value) return true;
            try { return Brain.AllowLoseTarget(__instance); }
            catch (Exception ex) { TacticalEnemyAIMod.Warn("LooseTarget", ex); return true; }
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(ANBBasicNPC), nameof(ANBBasicNPC.startFlanking))]
    internal static class StartFlankingPatch
    {
        static bool Prefix(ANBBasicNPC __instance)
        {
            if (!TacticalEnemyAIMod.Enabled.Value) return true;
            try { return Brain.AllowFlank(__instance); }
            catch (Exception ex) { TacticalEnemyAIMod.Warn("startFlanking", ex); return true; }
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(ANBBasicNPC), nameof(ANBBasicNPC.TryStrafe))]
    internal static class TryStrafePatch
    {
        static bool Prefix(ANBBasicNPC __instance)
        {
            if (!TacticalEnemyAIMod.Enabled.Value) return true;
            try { return Brain.AllowStrafe(__instance); }
            catch (Exception ex) { TacticalEnemyAIMod.Warn("TryStrafe", ex); return true; }
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(ANBBasicNPC), nameof(ANBBasicNPC.checkVisibilityRelatedActions))]
    internal static class VisibilityActionsPatch
    {
        static void Prefix(ANBBasicNPC __instance)
        {
            try { Brain.KeepActive(__instance, stayPut: false); }
            catch (Exception ex) { TacticalEnemyAIMod.Warn("checkVisibilityRelatedActions", ex); }
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(ANBBasicNPC), nameof(ANBBasicNPC.checkAbilities))]
    internal static class CheckAbilitiesPatch
    {
        static void Prefix(ANBBasicNPC __instance)
        {
            try { Brain.KeepActive(__instance, stayPut: true); }
            catch (Exception ex) { TacticalEnemyAIMod.Warn("checkAbilities", ex); }
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(ANBBasicNPC), nameof(ANBBasicNPC.Locomotion))]
    internal static class LocomotionPatch
    {
        static void Prefix(ANBBasicNPC __instance, out Brain.LocoState __state)
        {
            __state = default;
            try { Brain.BeforeLocomotion(__instance, ref __state); }
            catch (Exception ex) { TacticalEnemyAIMod.Warn("Locomotion prefix", ex); }
        }

        static void Postfix(ANBBasicNPC __instance, Brain.LocoState __state)
        {
            try { Brain.AfterLocomotion(__instance, __state); }
            catch (Exception ex) { TacticalEnemyAIMod.Warn("Locomotion postfix", ex); }
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(ANBBasicNPC), nameof(ANBBasicNPC.Navigation))]
    internal static class NavigationPatch
    {
        static void Prefix(ANBBasicNPC __instance, out Brain.NavState __state)
        {
            __state = default;
            try { Brain.BeforeNavigation(__instance, ref __state); }
            catch (Exception ex) { TacticalEnemyAIMod.Warn("Navigation prefix", ex); }
        }

        static void Postfix(ANBBasicNPC __instance, Brain.NavState __state)
        {
            try { Brain.AfterNavigation(__instance, __state); }
            catch (Exception ex) { TacticalEnemyAIMod.Warn("Navigation postfix", ex); }
        }
    }
}
