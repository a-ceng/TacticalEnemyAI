using System;
using Il2Cpp;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TacticalEnemyAI
{
    // Debug overlay for flat mode: a box around every enemy and its state above its head, drawn on top of the screen
    // (so through walls). IMGUI draws to the desktop window only; in VR that is the mirror, not the headset.
    internal static class DebugOverlay
    {
        static bool keyBroken, drawBroken;
        static Key key = Key.F8;
        static string keyName;
        static GUIStyle text, small, corner;
        static float scale = 1f;

        // The Overlay setting is the switch (Mod Settings shows it); the key flips it.
        internal static void CheckKey()
        {
            if (keyBroken) return;
            try
            {
                if (keyName != TacticalEnemyAIMod.OverlayKey.Value)
                {
                    keyName = TacticalEnemyAIMod.OverlayKey.Value;
                    if (!Enum.TryParse(keyName, true, out key)) { key = Key.F8; TacticalEnemyAIMod.Log.Warning($"overlay key '{keyName}' unknown, using F8"); }
                }
                var kb = Keyboard.current;
                if (kb == null || !kb[key].wasPressedThisFrame) return;
                TacticalEnemyAIMod.Overlay.Value = !TacticalEnemyAIMod.Overlay.Value;
                TacticalEnemyAIMod.Log.Msg($"overlay {(TacticalEnemyAIMod.Overlay.Value ? "on" : "off")}");
            }
            catch (Exception ex) { keyBroken = true; TacticalEnemyAIMod.Warn("overlay key", ex); }
        }

        static Camera Cam()
        {
            var c = Camera.main;
            if (c != null && c.isActiveAndEnabled) return c;
            Camera best = null;
            foreach (var k in Camera.allCameras)
                if (k != null && k.isActiveAndEnabled && k.targetTexture == null && (best == null || k.depth > best.depth)) best = k;
            return best;
        }

        internal static void Draw()
        {
            if (!TacticalEnemyAIMod.Overlay.Value || drawBroken || !TacticalEnemyAIMod.Enabled.Value) return;
            try
            {
                if (Event.current == null || Event.current.type != EventType.Repaint) return;
                var list = ANBStaticGameManager.ANBmain?.encounterSystem?.allEnemies;
                var cam = Cam();
                if (list == null || cam == null) return;
                scale = Mathf.Max(0.5f, TacticalEnemyAIMod.OverlaySize.Value) * Screen.height / 1080f;
                if (text == null)
                {
                    text = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerCenter, wordWrap = false };
                    small = new GUIStyle(text) { fontStyle = FontStyle.Normal, alignment = TextAnchor.UpperCenter };
                    corner = new GUIStyle(small) { alignment = TextAnchor.UpperLeft };
                }
                text.fontSize = Mathf.RoundToInt(S(14));
                small.fontSize = corner.fontSize = Mathf.RoundToInt(S(12));
                var pt = Brain.PlayerT();
                Vector3 pp = pt != null ? pt.position : cam.transform.position;
                float now = Time.time;
                int alive = 0, attacking = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var n = list[i];
                    if (n == null || !n.isEnemy || n.isDead || !n.gameObject.activeInHierarchy) continue;
                    alive++;
                    if (Brain.Managed(n)) attacking++;
                    DrawEnemy(cam, n, pp, now);
                }
                Shadowed(new Rect(S(10), S(10), S(900), S(22)), $"TacticalEnemyAI overlay ({keyName})  alive {alive}  attacking you {attacking}  " +
                    $"{(Brain.aggressive ? "IMPATIENT" : "patient")}  max movers {(Brain.aggressive ? "all" : Brain.maxPushers.ToString())}",
                    Color.white, corner);
            }
            catch (Exception ex) { drawBroken = true; TacticalEnemyAIMod.Warn("overlay", ex); }
        }

        static void DrawEnemy(Camera cam, ANBBasicNPC n, Vector3 pp, float now)
        {
            Vector3 feet = Brain.Body(n), head = feet + Vector3.up * 1.9f;
            Vector3 sf = cam.WorldToScreenPoint(feet), sh = cam.WorldToScreenPoint(head);
            if (sf.z <= 0.1f || sh.z <= 0.1f) return;                     // behind you
            float yf = Screen.height - sf.y, yh = Screen.height - sh.y;
            float h = Mathf.Max(S(8), yf - yh), w = h * 0.45f, x = sh.x;

            var s = Brain.Get(n);
            State(n, s, now, out string label, out Color c);
            float dist = Vector3.Distance(new Vector3(feet.x, 0, feet.z), new Vector3(pp.x, 0, pp.z));
            float dy = feet.y + 1.5f - pp.y;
            string info = $"{dist:0} m" + (Mathf.Abs(dy) > 1.5f ? (dy < 0 ? "  floor below" : "  floor above") : "");
            if (n.targetInSight) info += "  sees you";
            if (n.isGettingHit) info += "  stunned";
            // Shooting: aimed (in sight) or blind (at its lastKnownPosition, drawn as a red dotted line).
            bool blind = !n.targetInSight && n.blindfireTime > 0f && n.isAttacking;
            if (blind) label += "  BLIND FIRE";
            else if (n.targetInSight && n.attackReady && n.isAttacking) label += "  FIRING";
            if (blind)
            {
                Vector3 sa = cam.WorldToScreenPoint(n.lastKnownPosition);
                if (sa.z > 0.1f)
                {
                    GUI.color = new Color(1f, 0.15f, 0.1f, 0.9f);
                    float ys = yh + h * 0.3f, ya = Screen.height - sa.y;
                    for (int k = 1; k <= 14; k++)
                    {
                        float t = k / 14f;
                        Dot(Mathf.Lerp(x, sa.x, t), Mathf.Lerp(ys, ya, t), 4);
                    }
                }
            }

            // Where it is going (cover spot, peek spot, advance).
            try
            {
                var agent = n.agent;
                if (agent != null && agent.enabled && agent.hasPath && agent.remainingDistance > 1f)
                {
                    Vector3 sd = cam.WorldToScreenPoint(agent.destination);
                    if (sd.z > 0.1f)
                    {
                        GUI.color = new Color(c.r, c.g, c.b, 0.7f);
                        for (int k = 1; k <= 10; k++)
                        {
                            float t = k / 10f;
                            Dot(Mathf.Lerp(sf.x, sd.x, t), Mathf.Lerp(yf, Screen.height - sd.y, t), 4);
                        }
                        Dot(sd.x, Screen.height - sd.y, 10);
                    }
                }
            }
            catch { }

            GUI.color = c;
            Box(x - w / 2, yh, w, h, S(2));
            GUI.color = Color.white;
            Shadowed(new Rect(x - S(250), yh - S(24), S(500), S(22)), label, c, text);
            Shadowed(new Rect(x - S(250), yf + S(2), S(500), S(22)), info, Color.white, small);
        }

        // Overlay sizes are given for 1080p at OverlaySize 1.
        static float S(float px) => px * scale;

        static void Dot(float x, float y, float size)
        {
            float d = S(size);
            GUI.DrawTexture(new Rect(x - d / 2, y - d / 2, d, d), Texture2D.whiteTexture);
        }

        static void State(ANBBasicNPC n, Brain.Npc s, float now, out string label, out Color c)
        {
            string id = s != null ? s.Tag + " " : "";
            if (s != null && s.PanicArmed) { label = id + "FLINCH"; c = new Color(1f, 0.3f, 0.8f); return; }
            if (s != null && now < s.PanicUntil) { label = id + "PANIC"; c = (int)(now * 6) % 2 == 0 ? new Color(1f, 0.2f, 0.9f) : Color.white; return; }
            if (s == null || s.M == Brain.Mode.None || !Brain.Managed(n))
            {
                c = Color.gray;
                label = id + (n.NPCPaused ? "Paused" : n.isAttacking ? "Attacking (game)" : n.isHunting ? "Hunting" : "Idle");
                return;
            }
            switch (s.M)
            {
                case Brain.Mode.Engaged: label = "Engaged"; c = new Color(1f, 0.25f, 0.2f); break;
                case Brain.Mode.Covering: label = n.isInCover ? $"Cover (peek in {Mathf.Max(0f, s.CoverPeekAt - now):0}s)" : "To cover"; c = new Color(0.3f, 0.7f, 1f); break;
                case Brain.Mode.Peek: label = s.HasMove ? "Peek: stepping out" : $"Peek ({Mathf.Max(0f, s.PeekUntil - now):0}s)"; c = new Color(1f, 0.6f, 0.1f); break;
                case Brain.Mode.Hold: label = now < s.HoldUntil ? $"Hold ({s.HoldUntil - now:0}s)" : "Hold (may advance)"; c = new Color(1f, 0.95f, 0.2f); break;
                case Brain.Mode.Bound: label = s.BK == Brain.BoundKind.Cover ? "Bound: to cover" : s.BK == Brain.BoundKind.Flank ? "Bound: flank" : "Bound: walk"; c = new Color(0.4f, 1f, 0.4f); break;
                case Brain.Mode.BackOff: label = "Back off"; c = new Color(0.75f, 0.45f, 1f); break;
                case Brain.Mode.Approach: label = "Approach (no contact)"; c = new Color(0.5f, 1f, 0.85f); break;
                default: label = s.M.ToString(); c = Color.white; break;
            }
            label = id + label;
        }

        static void Box(float x, float y, float w, float h, float t)
        {
            var tex = Texture2D.whiteTexture;
            GUI.DrawTexture(new Rect(x, y, w, t), tex);
            GUI.DrawTexture(new Rect(x, y + h - t, w, t), tex);
            GUI.DrawTexture(new Rect(x, y, t, h), tex);
            GUI.DrawTexture(new Rect(x + w - t, y, t, h), tex);
        }

        static void Shadowed(Rect r, string s, Color c, GUIStyle st)
        {
            float o = Mathf.Max(1f, Mathf.Round(S(1)));
            st.normal.textColor = Color.black;
            GUI.Label(new Rect(r.x + o, r.y + o, r.width, r.height), s, st);
            st.normal.textColor = c;
            GUI.Label(r, s, st);
        }
    }
}
