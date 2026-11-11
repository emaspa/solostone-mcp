using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using SabberStoneCore.Config;
using SabberStoneCore.Enums;
using SabberStoneCore.Kettle;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;
using SabberStoneCore.Tasks;
using SabberStoneCore.Tasks.PlayerTasks;

namespace SoloStoneMcp
{
    public sealed partial class Worker
    {
        static readonly CardClass[] FuzzClasses = { CardClass.DEATHKNIGHT, CardClass.DEMONHUNTER, CardClass.DRUID, CardClass.HUNTER, CardClass.MAGE,
            CardClass.PALADIN, CardClass.PRIEST, CardClass.ROGUE, CardClass.SHAMAN, CardClass.WARLOCK, CardClass.WARRIOR };

        sealed class FuzzGame
        {
            public long Seed; public CardClass C1, C2; public List<int> Picks = new List<int>();
            public Session S; public List<JsonObject> Findings = new List<JsonObject>();
        }

        Session FuzzSession(long seed, CardClass c1, CardClass c2, bool standard)
        {
            var g = new Game(new GameConfig
            {
                StartPlayer = (int)(Math.Abs(seed) % 2) + 1, Player1HeroClass = c1, Player2HeroClass = c2, FillDecks = true, Shuffle = true,
                Logging = false, History = true, SkipMulligan = true, RandomSeed = seed, FormatType = standard ? FormatType.FT_STANDARD : FormatType.FT_WILD,
            });
            g.StartGame();
            return new Session { G = g, Seed = seed };
        }

        // Plays one game. picks == null: choose at random and record; else replay the recorded option indices.
        FuzzGame PlayGame(long seed, CardClass c1, CardClass c2, int maxActions, List<int> replay, bool standard, int stopAt = int.MaxValue)
        {
            var fg = new FuzzGame { Seed = seed, C1 = c1, C2 = c2, S = FuzzSession(seed, c1, c2, standard) };
            var s = fg.S; var g = s.G;
            var rng = new Random(unchecked((int)(seed * 7919 + 17)));
            JsonObject Find(string inv, string detail, int at) => new JsonObject
            {
                ["invariant"] = inv, ["detail"] = detail, ["action"] = at, ["seed"] = seed, ["classes"] = c1 + "," + c2, ["standard"] = standard,
                ["turn"] = g.Turn, ["picks"] = new JsonArray(fg.Picks.Select(p => (JsonNode)p).ToArray()),
                ["historyTail"] = Lines(s, Math.Max(0, g.PowerHistory.Full.Count - 60), g.PowerHistory.Full.Count, 60),
            };
            for (int a = 0; a < maxActions && a < stopAt; a++)
            {
                if (g.State == SabberStoneCore.Enums.State.COMPLETE) break;
                var p = PendingChooser(s) ?? g.CurrentPlayer;
                List<PlayerTask> opts;
                try { opts = Options(s, p); }
                catch (Exception e) { fg.Findings.Add(Find("E1-options", e.GetType().Name + ": " + e.Message + " @ " + FirstFrame(e), a)); break; }
                if (opts.Count == 0) { fg.Findings.Add(Find("E3-stuck", "no options for " + Side(s, p) + " at step " + g.Step, a)); break; }
                int i;
                if (replay != null) { if (a >= replay.Count) break; i = replay[a]; if (i >= opts.Count) { fg.Findings.Add(Find("replay-diverged", "option " + i + " of " + opts.Count, a)); break; } }
                else
                {
                    // Prefer doing something over ending the turn, so games reach complex boards.
                    var nonEnd = Enumerable.Range(0, opts.Count).Where(k => !(opts[k] is EndTurnTask)).ToList();
                    i = nonEnd.Count > 0 && rng.NextDouble() < 0.85 ? nonEnd[rng.Next(nonEnd.Count)] : rng.Next(opts.Count);
                }
                fg.Picks.Add(i);
                var task = opts[i];
                string printed = SafePrint(task);
                bool ok;
                string err = null;
                try { ok = Submit(g, task, out err); }
                catch (Exception e) { fg.Findings.Add(Find("E1-exception", e.GetType().Name + ": " + e.Message + " @ " + FirstFrame(e) + " | task " + printed, a)); break; }
                if (!ok) fg.Findings.Add(Find("E2-refused", "offered option refused: " + printed + " (" + err + ")", a));
                string bad = Sanity(s);
                if (bad != null) { fg.Findings.Add(Find("E4-state", bad + " | after " + printed, a)); break; }
            }
            if (g.State != SabberStoneCore.Enums.State.COMPLETE && fg.Picks.Count >= maxActions && replay == null)
                fg.Findings.Add(Find("E3-long", "game still running after " + maxActions + " actions (turn " + g.Turn + ")", fg.Picks.Count));
            return fg;
        }

        static string FirstFrame(Exception e)
        {
            var line = (e.StackTrace ?? "").Split('\n').FirstOrDefault(l => l.Contains("SabberStoneCore")) ?? "";
            return line.Trim();
        }

        static string SafePrint(PlayerTask t) { try { return t.FullPrint(); } catch { return t.PlayerTaskType.ToString(); } }

        string Sanity(Session s)
        {
            var g = s.G;
            foreach (var p in new[] { g.Player1, g.Player2 })
            {
                if (p.BoardZone.Count > 7) return Side(s, p) + " board holds " + p.BoardZone.Count;
                if (p.HandZone.Count > 10) return Side(s, p) + " hand holds " + p.HandZone.Count;
                if (p.SecretZone.Count > 5) return Side(s, p) + " has " + p.SecretZone.Count + " secrets";
                foreach (var m in p.BoardZone)
                    if (m is Minion mm && mm.Health <= 0 && !mm.ToBeDestroyed && g.State == SabberStoneCore.Enums.State.RUNNING && g.TaskQueue.IsEmpty)
                        return Label(mm) + " alive with health " + mm.Health;
                if (p.Hero.Health <= 0 && g.State == SabberStoneCore.Enums.State.RUNNING && g.TaskQueue.IsEmpty) return Side(s, p) + " hero at " + p.Hero.Health + " but the game runs";
                var ids = p.SecretZone.Select(x => x.Card.Name).ToList();
                if (ids.Distinct().Count() != ids.Count) return Side(s, p) + " holds two copies of one secret: " + string.Join(",", ids);
                foreach (var e in p.HandZone.Concat(p.BoardZone).Concat(p.SecretZone))
                    if (e.Controller != p) return Label(e) + " sits in " + Side(s, p) + "'s zone but is controlled by " + Side(s, e.Controller);
                // Every entity listed in a zone must say it is in that zone; one left behind (in the deck after being summoned
                // from it) is drawn or summoned a second time later.
                foreach (var zone in new SabberStoneCore.Model.Zones.IZone[] { p.HandZone, p.BoardZone, p.SecretZone, p.DeckZone, p.GraveyardZone })
                    foreach (var e in ((IEnumerable<IPlayable>)zone).ToList())
                        if (e.Zone != zone) return Label(e) + " is listed in " + Side(s, p) + "'s " + zone.Type + " but its zone is " + (e.Zone?.Type.ToString() ?? "none");
            }
            return null;
        }

        // E8: a top-level ATTACK block whose attack never resolved (no combat damage step) must not tell the client
        // ATTACKING=1, or the client draws a hit that never happened (the Freezing Trap bug).
        internal IEnumerable<string> AttackLint(Session s)
        {
            var full = s.G.PowerHistory.Full;
            int depth = 0;
            for (int i = 0; i < full.Count; i++)
            {
                if (full[i] is PowerHistoryBlockEnd) { depth = Math.Max(0, depth - 1); continue; }
                if (!(full[i] is PowerHistoryBlockStart b)) continue;
                depth++;
                if (depth != 1 || b.BlockType != BlockType.ATTACK) continue;
                int d = 1; bool attacking = false, defending = false, resolved = false;
                int j = i + 1;
                for (; j < full.Count && d > 0; j++)
                {
                    var h = full[j];
                    if (h is PowerHistoryBlockStart) d++;
                    else if (h is PowerHistoryBlockEnd) d--;
                    else if (h is PowerHistoryTagChange t)
                    {
                        if (t.Tag == GameTag.ATTACKING && t.Value == 1) attacking = true;
                        if (t.Tag == GameTag.DEFENDING && t.Value == 1) defending = true;
                        if (t.Tag == GameTag.NUM_ATTACKS_THIS_TURN || (t.Tag == GameTag.PREDAMAGE) || t.Tag == GameTag.DAMAGE || t.Tag == GameTag.DIVINE_SHIELD || t.Tag == GameTag.ARMOR) resolved = true;
                    }
                }
                if ((attacking || defending) && !resolved)
                    yield return "entry " + i + ": ATTACK block from " + Name(s, b.Source) + " to " + Name(s, b.Target) + " sends ATTACKING/DEFENDING but no combat follows";
            }
        }

        JsonNode Fuzz(JsonObject req)
        {
            long seed0 = Long(req, "seed") ?? DateTime.UtcNow.Ticks % 1000000;
            int games = Int(req, "games") ?? 50;
            int maxActions = Int(req, "maxActions") ?? 400;
            bool standard = Str(req, "format") == "standard";
            bool determinism = !(req["determinism"] is JsonValue dv && dv.TryGetValue(out bool db) && !db);
            var classes = Arr(req, "classes").Select(c => ParseClass((string)c, CardClass.MAGE)).ToArray();
            var deadline = DateTime.UtcNow.AddSeconds(Int(req, "seconds") ?? 240);
            var findings = new JsonArray();
            var seen = new HashSet<string>();
            int played = 0, actions = 0, turns = 0;
            for (int k = 0; k < games && DateTime.UtcNow < deadline; k++)
            {
                long seed = seed0 + k;
                var rng = new Random(unchecked((int)seed));
                var pool = classes.Length > 0 ? classes : FuzzClasses;
                var c1 = pool[rng.Next(pool.Length)]; var c2 = pool[rng.Next(pool.Length)];
                FuzzGame fg;
                try { fg = PlayGame(seed, c1, c2, maxActions, null, standard); }
                catch (Exception e) { findings.Add(new JsonObject { ["invariant"] = "E1-setup", ["detail"] = e.GetType().Name + ": " + e.Message, ["seed"] = seed, ["classes"] = c1 + "," + c2 }); continue; }
                played++; actions += fg.Picks.Count; turns += fg.S.G.Turn;
                foreach (var lint in AttackLint(fg.S).Take(3))
                    fg.Findings.Add(new JsonObject { ["invariant"] = "E8-attack", ["detail"] = lint, ["seed"] = seed, ["classes"] = c1 + "," + c2, ["standard"] = standard, ["picks"] = new JsonArray(fg.Picks.Select(p => (JsonNode)p).ToArray()) });
                if (determinism && fg.Findings.Count == 0)
                {
                    var again = PlayGame(seed, c1, c2, maxActions, fg.Picks, standard);
                    string h1 = fg.S.G.Hash(), h2 = again.S.G.Hash();
                    if (h1 != h2) fg.Findings.Add(new JsonObject { ["invariant"] = "E6-determinism", ["detail"] = "same seed and picks, different end state", ["seed"] = seed, ["classes"] = c1 + "," + c2, ["standard"] = standard, ["picks"] = new JsonArray(fg.Picks.Select(p => (JsonNode)p).ToArray()) });
                }
                foreach (var f in fg.Findings)
                {
                    string sig = (string)f["invariant"] + "|" + System.Text.RegularExpressions.Regex.Replace((string)f["detail"] ?? "", "#\\d+|\\d+", "N");
                    if (seen.Add(sig)) findings.Add(f);
                }
            }
            return new JsonObject { ["games"] = played, ["actions"] = actions, ["turns"] = turns, ["firstSeed"] = seed0, ["findings"] = findings };
        }

        // Replays a finding's recorded game and reports what happens now (for verifying a fix).
        JsonNode Replay(JsonObject req)
        {
            long seed = Long(req, "seed") ?? throw new UserError("seed required");
            var cls = (Str(req, "classes") ?? "MAGE,HUNTER").Split(',');
            var picks = Arr(req, "picks").Select(p => (int)p).ToList();
            bool standard = Bool(req, "standard");
            int stopAt = Int(req, "stopAt") ?? picks.Count;
            var fg = PlayGame(seed, ParseClass(cls[0], CardClass.MAGE), ParseClass(cls[1], CardClass.HUNTER), picks.Count, picks, standard, stopAt);
            foreach (var lint in AttackLint(fg.S).Take(3))
                fg.Findings.Add(new JsonObject { ["invariant"] = "E8-attack", ["detail"] = lint });
            var r = new JsonObject { ["actions"] = fg.Picks.Count, ["findings"] = new JsonArray(fg.Findings.Select(f => (JsonNode)f).ToArray()), ["state"] = State(fg.S, false) };
            if (Bool(req, "keep")) { string id = "g" + nextSession++; sessions[id] = fg.S; r["session"] = id; }
            return r;
        }
    }
}
