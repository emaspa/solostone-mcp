using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;

namespace SoloStoneMcp
{
    public sealed partial class Worker
    {
        // Each expectation returns {expect, pass, actual?}. History patterns are case-insensitive regexes over compact lines.
        internal JsonObject Expect(Session s, JsonArray expects, int stepStart)
        {
            var results = new JsonArray();
            bool all = true;
            foreach (var node in expects ?? new JsonArray())
            {
                var e = node.AsObject();
                var r = new JsonObject { ["expect"] = e.DeepClone() };
                try
                {
                    var fails = new List<string>();
                    Check(s, e, stepStart, fails, r);
                    r["pass"] = fails.Count == 0;
                    if (fails.Count > 0) r["why"] = string.Join("; ", fails);
                }
                catch (UserError ex) { r["pass"] = false; r["why"] = ex.Message; }
                catch (Exception ex) { r["pass"] = false; r["why"] = ex.GetType().Name + ": " + ex.Message; }
                if (!(bool)r["pass"]) all = false;
                results.Add(r);
            }
            return new JsonObject { ["pass"] = all, ["results"] = results };
        }

        static void Eq(List<string> fails, string what, object want, object got)
        {
            if (want == null) return;
            if (!string.Equals(want.ToString(), got?.ToString(), StringComparison.OrdinalIgnoreCase)) fails.Add(what + ": want " + want + ", got " + (got ?? "null"));
        }

        static object Want(JsonObject o, string k) => o[k] is JsonValue v ? (v.TryGetValue(out int i) ? i : v.TryGetValue(out bool b) ? (object)b : (object)v.ToString()) : null;

        void Check(Session s, JsonObject e, int stepStart, List<string> fails, JsonObject r)
        {
            var g = s.G;
            if (e["entity"] != null)
            {
                IPlayable x = null;
                try { x = Resolve(s, g.CurrentPlayer, e["entity"]); }
                catch (UserError) when (e["exists"] is JsonValue ev && ev.TryGetValue(out bool b) && !b) { return; }
                if (e["exists"] is JsonValue ev2 && ev2.TryGetValue(out bool want) && !want)
                {
                    if (x.Zone?.Type == Zone.PLAY || x.Zone?.Type == Zone.HAND || x.Zone?.Type == Zone.SECRET) fails.Add(Label(x) + " exists in " + x.Zone.Type);
                    return;
                }
                r["actual"] = new JsonObject { ["entity"] = Label(x), ["zone"] = x.Zone?.Type.ToString() ?? "NONE", ["owner"] = Side(s, x) };
                Eq(fails, "zone", Want(e, "zone"), x.Zone?.Type.ToString() ?? "NONE");
                Eq(fails, "owner", Want(e, "owner"), Side(s, x));
                if (x is Character c)
                {
                    r["actual"]["health"] = c.Health; r["actual"]["damage"] = c.Damage; r["actual"]["atk"] = c.AttackDamage;
                    Eq(fails, "health", Want(e, "health"), c.Health);
                    Eq(fails, "damage", Want(e, "damage"), c.Damage);
                    Eq(fails, "atk", Want(e, "atk"), c.AttackDamage);
                    Eq(fails, "armor", Want(e, "armor"), c.Armor);
                }
                Eq(fails, "exhausted", Want(e, "exhausted"), x.IsExhausted);
                Eq(fails, "card", Want(e, "card"), x.Card.Id);
                if (e["tags"] is JsonObject tags)
                    foreach (var kv in tags) { var t = ParseTag(kv.Key); Eq(fails, "tag " + kv.Key, kv.Value.ToString(), TagValue(t, x[t]).ToString()); }
                return;
            }
            if (e["zone"] != null)
            {
                string z = Str(e, "zone");
                int dot = z.IndexOf('.');
                if (dot < 0) throw new UserError("zone expectation wants me.board, opp.secret, ...");
                var list = ZoneOfPublic(Player(s, z.Substring(0, dot)), z.Substring(dot + 1)).ToList();
                r["actual"] = new JsonArray(list.Select(x => (JsonNode)Label(x)).ToArray());
                Eq(fails, "count", Want(e, "count"), list.Count);
                foreach (var h in Arr(e, "has")) if (!list.Any(x => Matches(x, (string)h) || ("@" + AliasOf(s, x)) == (string)h)) fails.Add(z + " lacks " + h);
                foreach (var h in Arr(e, "hasNot")) if (list.Any(x => Matches(x, (string)h) || ("@" + AliasOf(s, x)) == (string)h)) fails.Add(z + " holds " + h);
                return;
            }
            if (e["history"] != null)
            {
                string range = Str(e, "history");
                int from = range == "full" ? 0 : stepStart;
                var lines = Lines(s, from, g.PowerHistory.Full.Count, int.MaxValue).Select(n => Regex.Replace((string)n, "^\\d+: ", "")).ToList();
                foreach (var p in Arr(e, "contains")) { var re = new Regex((string)p, RegexOptions.IgnoreCase); if (!lines.Any(l => re.IsMatch(l))) fails.Add("history lacks /" + p + "/"); }
                foreach (var p in Arr(e, "none")) { var re = new Regex((string)p, RegexOptions.IgnoreCase); var hit = lines.FirstOrDefault(l => re.IsMatch(l)); if (hit != null) fails.Add("history has /" + p + "/: " + hit.Trim()); }
                if (e["order"] is JsonArray order)
                {
                    int at = 0;
                    foreach (var p in order)
                    {
                        var re = new Regex((string)p, RegexOptions.IgnoreCase);
                        int found = lines.FindIndex(at, l => re.IsMatch(l));
                        if (found < 0) { fails.Add("history order breaks at /" + p + "/ (searched from line " + at + ")"); break; }
                        at = found + 1;
                    }
                }
                return;
            }
            if (e["current"] != null) { Eq(fails, "current", Str(e, "current"), Side(s, g.CurrentPlayer)); return; }
            if (e["state"] != null) { Eq(fails, "state", Str(e, "state"), g.State.ToString()); return; }
            if (e["winner"] != null)
            {
                string winner = g.Player1.PlayState == PlayState.WON ? "me" : g.Player2.PlayState == PlayState.WON ? "opp" : "none";
                Eq(fails, "winner", Str(e, "winner"), winner); return;
            }
            if (e["choice"] != null)
            {
                var p = Player(s, Str(e, "choice"));
                if (p.Choice == null) { if (!(e["pending"] is JsonValue pv && pv.TryGetValue(out bool pb) && !pb)) fails.Add(Str(e, "choice") + " has no choice pending"); return; }
                var offered = p.Choice.Choices.Select(id => g.IdEntityDic.TryGetValue(id, out var x) ? x : null).Where(x => x != null).ToList();
                r["actual"] = new JsonArray(offered.Select(x => (JsonNode)Label(x)).ToArray());
                foreach (var h in Arr(e, "offersInclude")) if (!offered.Any(x => Matches(x, (string)h))) fails.Add("choice lacks " + h);
                foreach (var h in Arr(e, "offersExclude")) if (offered.Any(x => Matches(x, (string)h))) fails.Add("choice offers " + h);
                return;
            }
            if (e["mana"] != null)
            {
                var p = Player(s, Str(e, "mana"));
                Eq(fails, "remaining", Want(e, "remaining"), p.RemainingMana);
                Eq(fails, "base", Want(e, "base"), p.BaseMana);
                return;
            }
            throw new UserError("unknown expectation; use entity, zone, history, current, state, winner, choice, mana");
        }

        static string AliasOf(Session s, IPlayable x) { foreach (var kv in s.Aliases) if (kv.Value == x.Id) return kv.Key; return null; }
        static IEnumerable<IPlayable> ZoneOfPublic(Controller p, string zone) => ZoneOf(p, zone);

        // ---------- scenarios ----------
        // {name, setup, steps:[...], expect:[...]} ; a step may carry its own "expect" (checked right after it).
        internal JsonObject RunScenario(JsonObject sc, long? seed, out Session s, bool verbose)
        {
            s = Build(sc["setup"]?.AsObject() ?? new JsonObject(), seed ?? Long(sc, "seed"));
            var stepsOut = new JsonArray();
            bool pass = true;
            foreach (var node in sc["steps"] as JsonArray ?? new JsonArray())
            {
                var st = node.AsObject();
                var r = Step(s, st);
                if (!verbose) r.Remove("history");
                bool ok = (bool)r["ok"];
                if (st["expect"] is JsonArray ex)
                {
                    var er = Expect(s, ex, s.StepStart);
                    r["expect"] = er;
                    if (!(bool)er["pass"]) pass = false;
                    if (!verbose && (bool)er["pass"]) r.Remove("expect");
                }
                if (!ok && !Bool(st, "mayFail")) { pass = false; stepsOut.Add(r); break; }
                if (!verbose && ok) r.Remove("step");
                if (!verbose && ok) r["step"] = st.DeepClone().AsObject().Let(o => { o.Remove("expect"); return o; });
                stepsOut.Add(r);
            }
            var final = Expect(s, sc["expect"] as JsonArray, s.StepStart);
            if (!(bool)final["pass"]) pass = false;
            return new JsonObject { ["name"] = Str(sc, "name"), ["seed"] = s.Seed, ["pass"] = pass, ["steps"] = stepsOut, ["expect"] = final };
        }

        JsonNode Scenario(JsonObject sc, bool keep)
        {
            var r = RunScenario(sc, null, out var s, Bool(sc, "verbose"));
            if (keep)
            {
                string id = "g" + nextSession++;
                sessions[id] = s;
                r["session"] = id;
            }
            r["state"] = State(s, false);
            return r;
        }

        // Tries seeds until the scenario passes (for RNG-dependent setups, e.g. a Discover offer or a random target).
        JsonNode SeedSearch(JsonObject req)
        {
            var sc = req["scenario"].AsObject();
            long from = Long(req, "from") ?? 1;
            int count = Int(req, "count") ?? 500;
            int want = Int(req, "want") ?? 1;
            var found = new JsonArray();
            string lastWhy = null;
            for (long seed = from; seed < from + count && found.Count < want; seed++)
            {
                var r = RunScenario(sc, seed, out _, false);
                if ((bool)r["pass"]) found.Add(seed);
                else lastWhy = r.ToJsonString().Length > 600 ? r.ToJsonString().Substring(0, 600) : r.ToJsonString();
            }
            return new JsonObject { ["seeds"] = found, ["tried"] = count, ["lastFailure"] = found.Count == 0 ? lastWhy : null };
        }
    }

    internal static class Ext
    {
        internal static TR Let<T, TR>(this T x, Func<T, TR> f) => f(x);
    }
}
