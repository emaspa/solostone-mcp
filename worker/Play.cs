using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SabberStoneCore.Actions;
using SabberStoneCore.Enums;
using SabberStoneCore.Kettle;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;
using SabberStoneCore.Tasks;
using SabberStoneCore.Tasks.PlayerTasks;
using SabberStoneCore.Tasks.SimpleTasks;

namespace SoloStoneMcp
{
    public sealed partial class Worker
    {
        // ---------- state ----------
        JsonObject EntityBrief(Session s, IPlayable e)
        {
            var o = new JsonObject { ["id"] = e.Id, ["card"] = e.Card.Id, ["name"] = e.Card.Name };
            if (e is Character c)
            {
                o["atk"] = c.AttackDamage; o["health"] = c.Health;
                if (c.Damage > 0) o["damage"] = c.Damage;
                if (c is Minion) o["ready"] = c.CanAttack;
                var kw = new List<string>();
                if (c.HasTaunt) kw.Add("taunt"); if (c.HasDivineShield) kw.Add("divine shield"); if (c.IsFrozen) kw.Add("frozen");
                if (c.HasStealth) kw.Add("stealth"); if (c.HasWindfury) kw.Add("windfury"); if (c.HasLifeSteal) kw.Add("lifesteal");
                if (c[GameTag.CHARGE] == 1) kw.Add("charge"); if (c[GameTag.RUSH] == 1) kw.Add("rush"); if (c[GameTag.REBORN] == 1) kw.Add("reborn"); if (c[GameTag.POISONOUS] == 1) kw.Add("poisonous");
                if (c.IsImmune) kw.Add("immune"); if (c.CantBeTargetedBySpells) kw.Add("elusive");
                if (kw.Count > 0) o["keywords"] = string.Join(",", kw);
            }
            else if (e.Zone?.Type == Zone.HAND || e.Card.Type == CardType.SPELL) o["cost"] = e.Cost;
            if (e.Zone?.Type == Zone.HAND) o["cost"] = e.Cost;
            if (e.Zone?.Type == Zone.SECRET) o["exhausted"] = e.IsExhausted;
            foreach (var kv in s.Aliases) if (kv.Value == e.Id) { o["alias"] = "@" + kv.Key; break; }
            return o;
        }

        JsonObject PlayerState(Session s, Controller p, bool full)
        {
            var h = p.Hero;
            var o = new JsonObject
            {
                ["hero"] = new JsonObject { ["id"] = h.Id, ["card"] = h.Card.Id, ["health"] = h.Health, ["armor"] = h.Armor, ["atk"] = h.AttackDamage, ["canAttack"] = h.CanAttack },
                ["power"] = h.HeroPower == null ? null : new JsonObject { ["id"] = h.HeroPower.Id, ["name"] = h.HeroPower.Card.Name, ["exhausted"] = h.HeroPower.IsExhausted },
                ["weapon"] = h.Weapon == null ? null : new JsonObject { ["id"] = h.Weapon.Id, ["name"] = h.Weapon.Card.Name, ["atk"] = h.Weapon.AttackDamage, ["durability"] = h.Weapon.Durability },
                ["mana"] = new JsonObject { ["base"] = p.BaseMana, ["remaining"] = p.RemainingMana, ["overloadLocked"] = p.OverloadLocked, ["overloadOwed"] = p.OverloadOwed },
                ["hand"] = new JsonArray(p.HandZone.Select(e => (JsonNode)EntityBrief(s, e)).ToArray()),
                ["board"] = new JsonArray(p.BoardZone.Select(e => (JsonNode)EntityBrief(s, e)).ToArray()),
                ["secrets"] = new JsonArray(p.SecretZone.Select(e => (JsonNode)EntityBrief(s, e)).ToArray()),
                ["deck"] = p.DeckZone.Count,
                ["graveyard"] = p.GraveyardZone.Count,
                ["playState"] = p.PlayState.ToString(),
            };
            if (full)
            {
                o["deckCards"] = new JsonArray(p.DeckZone.Reverse().Select(e => (JsonNode)Label(e)).ToArray()); // next draw first
                o["graveyardCards"] = new JsonArray(p.GraveyardZone.Select(e => (JsonNode)Label(e)).ToArray());
                o["setaside"] = new JsonArray(p.SetasideZone.Select(e => (JsonNode)Label(e)).ToArray());
            }
            if (p.Choice != null) o["choice"] = ChoiceOf(s, p.Choice);
            return o;
        }

        JsonObject ChoiceOf(Session s, Choice ch) => new JsonObject
        {
            ["type"] = ch.ChoiceType.ToString(),
            ["source"] = s.G.IdEntityDic.TryGetValue(ch.SourceId, out var src) ? Label(src) : "#" + ch.SourceId,
            ["options"] = new JsonArray(ch.Choices.Select((id, i) => (JsonNode)new JsonObject
            {
                ["index"] = i, ["id"] = id,
                ["card"] = s.G.IdEntityDic.TryGetValue(id, out var e) ? e.Card.Id : null,
                ["name"] = s.G.IdEntityDic.TryGetValue(id, out var e2) ? e2.Card.Name : null,
            }).ToArray()),
        };

        JsonNode State(Session s, bool full)
        {
            var g = s.G;
            var o = new JsonObject
            {
                ["turn"] = g.Turn, ["current"] = Side(s, g.CurrentPlayer), ["step"] = g.Step.ToString(), ["state"] = g.State.ToString(),
                ["me"] = PlayerState(s, g.Player1, full), ["opp"] = PlayerState(s, g.Player2, full),
            };
            if (s.Aliases.Count > 0)
            {
                var a = new JsonObject();
                foreach (var kv in s.Aliases) a["@" + kv.Key] = g.IdEntityDic.TryGetValue(kv.Value, out var e) ? Label(e) + " " + (e.Zone?.Type.ToString() ?? "NOZONE") : "#" + kv.Value;
                o["aliases"] = a;
            }
            return o;
        }

        JsonNode EntityFull(Session s, IPlayable e)
        {
            var o = EntityBrief(s, e);
            o["owner"] = Side(s, e); o["zone"] = e.Zone?.Type.ToString() ?? "NONE"; o["zonePos"] = e.ZonePosition;
            o["text"] = Clean(e.Card.Text);
            var tags = new JsonObject();
            foreach (var kv in e.NativeTags.OrderBy(k => (int)k.Key)) tags[TagName(kv.Key)] = TagValue(kv.Key, kv.Value);
            o["tags"] = tags;
            if (e.AppliedEnchantments != null && e.AppliedEnchantments.Count > 0)
                o["enchantments"] = new JsonArray(e.AppliedEnchantments.Select(x => (JsonNode)(x.Card.Id + " " + x.Card.Name)).ToArray());
            return o;
        }

        static string TagName(GameTag t) => Enum.IsDefined(typeof(GameTag), t) ? t.ToString() : ((int)t).ToString();

        static JsonNode TagValue(GameTag t, int v)
        {
            switch (t)
            {
                case GameTag.ZONE: return ((Zone)v).ToString();
                case GameTag.STEP: case GameTag.NEXT_STEP: return ((Step)v).ToString();
                case GameTag.PLAYSTATE: return ((PlayState)v).ToString();
                case GameTag.CARDTYPE: return ((CardType)v).ToString();
                case GameTag.MULLIGAN_STATE: return ((Mulligan)v).ToString();
                case GameTag.STATE: return ((State)v).ToString();
                default: return v;
            }
        }

        // ---------- options ----------
        JsonNode OptionsOf(Session s, Controller who)
        {
            var p = who ?? PendingChooser(s) ?? s.G.CurrentPlayer;
            var list = Options(s, p);
            return new JsonObject
            {
                ["player"] = Side(s, p),
                ["options"] = new JsonArray(list.Select((t, i) => (JsonNode)Describe(s, t, i)).ToArray()),
            };
        }

        Controller PendingChooser(Session s) =>
            s.G.CurrentPlayer.Choice != null ? s.G.CurrentPlayer : s.G.CurrentPlayer.Opponent.Choice != null ? s.G.CurrentPlayer.Opponent : null;

        List<PlayerTask> Options(Session s, Controller p)
        {
            if (p.Choice != null && p != s.G.CurrentPlayer)
                return p.Choice.Choices.Select(id => (PlayerTask)ChooseTask.Pick(p, id)).ToList();
            return p.Options();
        }

        JsonObject Describe(Session s, PlayerTask t, int i)
        {
            var o = new JsonObject { ["index"] = i, ["kind"] = t.PlayerTaskType.ToString() };
            if (t.Source != null) o["source"] = Label(t.Source);
            if (t.ActionTarget != null) o["target"] = Label(t.ActionTarget);
            if (t is PlayCardTask pc && pc.ZonePosition >= 0 && pc.Source is Minion) o["pos"] = pc.ZonePosition;
            if (t.ChooseOne > 0) o["chooseOne"] = t.ChooseOne;
            if (t is ChooseTask ct)
            {
                var ids = ChoiceIds(ct);
                o["choices"] = string.Join(",", ids.Select(id => s.G.IdEntityDic.TryGetValue(id, out var e) ? Label(e) : "#" + id));
            }
            return o;
        }

        static List<int> ChoiceIds(ChooseTask ct) => ct.Choices.ToList();

        // Choices go through the decision API (Game.TryAnswer), as NativeMatch.AnswerDecision does in the game:
        // Process(ChooseTask) refuses while an effect is suspended on that choice. Other tasks go through Process.
        internal static bool Submit(Game g, PlayerTask t, out string error)
        {
            error = null;
            if (t is ChooseTask ct)
            {
                var decision = g.GetPendingDecisions().FirstOrDefault(d => d.ControllerId == ct.Controller.Id
                    && d.Kind != SabberStoneCore.Model.Decisions.DecisionKind.Options);
                if (decision == null) { error = "no pending decision for player " + ct.Controller.PlayerId; return false; }
                return g.TryAnswer(new SabberStoneCore.Model.Decisions.DecisionAnswer(decision.Id, decision.ControllerId, ct.Choices), out error);
            }
            bool ok = g.Process(t);
            if (!ok) error = "the engine refused " + SafePrint(t);
            return ok;
        }

        // ---------- acting ----------
        JsonNode Act(Session s, JsonArray steps)
        {
            var results = new JsonArray();
            bool stop = false;
            foreach (var node in steps)
            {
                if (stop) { results.Add(new JsonObject { ["step"] = node?.DeepClone(), ["skipped"] = true }); continue; }
                var r = Step(s, node.AsObject());
                results.Add(r);
                if (!(bool)r["ok"]) stop = true;
            }
            return new JsonObject { ["steps"] = results, ["state"] = State(s, false) };
        }

        internal JsonObject Step(Session s, JsonObject st)
        {
            var g = s.G;
            int start = g.PowerHistory.Full.Count;
            s.StepStart = start;
            var r = new JsonObject { ["step"] = st.DeepClone() };
            try
            {
                why = null;
                bool processed = Do(s, st);
                r["ok"] = processed;
                if (!processed) r["error"] = why ?? "the engine refused the task (Process returned false)";
            }
            catch (UserError e) { r["ok"] = false; r["error"] = e.Message; }
            catch (Exception e)
            {
                var inner = e is System.Reflection.TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
                r["ok"] = false; r["exception"] = inner.GetType().Name + ": " + inner.Message; r["trace"] = Program.Trim(inner.StackTrace);
            }
            if (!Bool(st, "quiet")) r["history"] = Lines(s, start, g.PowerHistory.Full.Count, 400);
            return r;
        }

        string why;

        bool Do(Session s, JsonObject st)
        {
            var g = s.G;
            // Who acts: explicit "by", else whoever owes a choice, else the current player.
            Controller by = st["by"] != null ? Player(s, Str(st, "by")) : (st["choose"] != null ? PendingChooser(s) ?? g.CurrentPlayer : g.CurrentPlayer);
            string alias = Str(st, "as")?.TrimStart('@');

            bool Run(PlayerTask t)
            {
                if (by != g.CurrentPlayer && !(t is ChooseTask)) { why = Side(s, by) + " is not the current player (current: " + Side(s, g.CurrentPlayer) + ")"; return false; }
                bool ok = Submit(g, t, out string err);
                if (!ok) why = err;
                return ok;
            }

            if (st["option"] != null)
            {
                int i = (int)st["option"];
                var list = Options(s, by);
                if (i < 0 || i >= list.Count) { why = "option " + i + " out of range (" + list.Count + ")"; return false; }
                return Run(list[i]);
            }
            if (st["play"] != null)
            {
                var src = Resolve(s, by, st["play"]);
                var tgt = Resolve(s, by, st["target"]);
                if (alias != null) s.Aliases[alias] = src.Id;
                return Run(PlayCardTask.Any(by, src, tgt, Int(st, "pos") ?? -1, Int(st, "choose") ?? 0));
            }
            if (st["attack"] != null)
            {
                var src = Resolve(s, by, st["attack"]);
                var tgt = (ICharacter)Resolve(s, by.Opponent, st["target"] ?? throw new UserError("attack needs target"));
                if (src is Hero) return Run(HeroAttackTask.Any(by, tgt));
                return Run(MinionAttackTask.Any(by, src, tgt));
            }
            if (st["power"] != null)
            {
                var tgt = (ICharacter)Resolve(s, by, st["target"]);
                return Run(HeroPowerTask.Any(by, tgt, Int(st, "choose") ?? 0));
            }
            if (st["trade"] != null) return Run(TradeCardTask.Any(by, Resolve(s, by, st["trade"])));
            if (st["activate"] != null) return Run(new ActivateLocationTask(by, Resolve(s, by, st["activate"]), Resolve(s, by, st["target"])));
            if (st["end_turn"] != null || Str(st, "do") == "end_turn") return Run(EndTurnTask.Any(by));
            if (st["concede"] != null) return Run(ConcedeTask.Any(by));
            if (st["mulligan"] != null)
            {
                if (by.Choice == null) { why = Side(s, by) + " has no mulligan pending"; return false; }
                var keep = Arr(st, "mulligan").Select(x => Resolve(s, by, x).Id).ToList();
                return Run(ChooseTask.Mulligan(by, keep));
            }
            if (st["choose"] != null)
            {
                if (by.Choice == null) { why = Side(s, by) + " has no choice pending"; return false; }
                int pick = PickFrom(s, by.Choice, st["choose"]);
                if (alias != null) s.Aliases[alias] = pick;
                return Run(ChooseTask.Pick(by, pick));
            }
            // Forced effects, outside the rules: what another card would do. "for" names the receiving player.
            if (st["cast"] != null)
            {
                var user = st["for"] != null ? Player(s, Str(st, "for")) : by;
                var creator = st["creator"] != null ? Resolve(s, by, st["creator"]) : null;
                var tags = new EntityData();
                if (creator != null) tags[GameTag.CREATOR] = creator.Id;
                var spell = Entity.FromCard(user, CardOf(Str(st, "cast")), tags);
                if (alias != null) s.Aliases[alias] = spell.Id;
                new PlayTask(PlayType.SPELL, true).Process(g, user, (IEntity)creator ?? user.Hero, null, new TaskStack { Playables = new[] { spell } });
                g.DeathProcessingAndAuraUpdate();
                return true;
            }
            if (st["summon"] != null)
            {
                var owner = st["for"] != null ? Player(s, Str(st, "for")) : by;
                var m = Entity.FromCard(owner, CardOf(Str(st, "summon"))) as Minion ?? throw new UserError("summon needs a minion card");
                Generic.SummonBlock(g, m, Int(st, "pos") ?? -1, null);
                if (Bool(st, "ready")) m.IsExhausted = false;
                if (alias != null) s.Aliases[alias] = m.Id;
                g.DeathProcessingAndAuraUpdate();
                return true;
            }
            if (st["give"] != null)
            {
                var owner = st["for"] != null ? Player(s, Str(st, "for")) : by;
                string zone = Str(st, "zone") ?? "hand";
                var card = CardOf(Str(st, "give"));
                IPlayable e = zone == "deck" ? Entity.FromCard(owner, card, zone: owner.DeckZone)
                    : zone == "secret" ? Entity.FromCard(owner, card, zone: owner.SecretZone)
                    : Entity.FromCard(owner, card, zone: owner.HandZone);
                if (zone == "secret") { e.Card.Power?.Trigger?.Activate(e); e.IsExhausted = owner == g.CurrentPlayer; }
                if (alias != null) s.Aliases[alias] = e.Id;
                return true;
            }
            if (st["set"] != null)
            {
                var e = Resolve(s, by, st["set"]);
                foreach (var kv in st["tags"]?.AsObject() ?? new JsonObject()) e[ParseTag(kv.Key)] = (int)kv.Value;
                if (Int(st, "damage") is int d && e is Character c) c.Damage = d;
                g.DeathProcessingAndAuraUpdate();
                return true;
            }
            if (st["checkpoint"] != null) { Checkpoint(s, Str(st, "checkpoint")); return true; }
            throw new UserError("unknown step; use play, attack, power, trade, activate, choose, mulligan, end_turn, concede, option, cast, summon, give, set");
        }

        int PickFrom(Session s, Choice ch, JsonNode spec)
        {
            var g = s.G;
            Card CardOfId(int id) => g.IdEntityDic.TryGetValue(id, out var e) ? e.Card : null;
            string Offer() => string.Join(", ", ch.Choices.Select(id => CardOfId(id)?.Name + "#" + id + " " + CardOfId(id)?.Id));
            if (spec is JsonValue v && v.TryGetValue(out int n))
            {
                if (ch.Choices.Contains(n)) return n;
                if (n >= 0 && n < ch.Choices.Count) return ch.Choices[n];
                throw new UserError("choice " + n + " is neither an offered id nor an index; offered: " + Offer());
            }
            if (spec is JsonObject o)
            {
                if (Int(o, "index") is int idx) return ch.Choices[idx];
                if (Str(o, "not") is string not)
                    return ch.Choices.FirstOrDefault(id => !(CardOfId(id) is Card c && (c.Id == not || c.Name == not))) is int pick && pick != 0 ? pick : throw new UserError("every offer is " + not);
            }
            string key = (string)spec;
            foreach (int id in ch.Choices)
                if (CardOfId(id) is Card c && (string.Equals(c.Id, key, StringComparison.OrdinalIgnoreCase) || string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase))) return id;
            throw new UserError(key + " is not offered; offered: " + Offer());
        }

        // ---------- history ----------
        internal string Name(Session s, int id)
        {
            var g = s.G;
            if (id == 1) return "Game";
            if (id == g.Player1.Id) return "Me";
            if (id == g.Player2.Id) return "Opp";
            return g.IdEntityDic.TryGetValue(id, out var e) ? e.Card.Name + "#" + id : "#" + id;
        }

        internal string Line(Session s, IPowerHistoryEntry h)
        {
            switch (h)
            {
                case PowerHistoryTagChange t: return "TAG " + Name(s, t.EntityId) + " " + TagName(t.Tag) + "=" + TagValue(t.Tag, t.Value).ToString();
                case PowerHistoryBlockStart b:
                    return "BLOCK " + b.BlockType + " src=" + Name(s, b.Source) + (b.Target > 0 ? " tgt=" + Name(s, b.Target) : "")
                        + (string.IsNullOrEmpty(b.EffectCardId) ? "" : " effect=" + b.EffectCardId) + (b.Index >= 0 ? " index=" + b.Index : "");
                case PowerHistoryBlockEnd _: return "END";
                case PowerHistoryFullEntity f: return "FULL " + EntityLine(s, f.Entity);
                case PowerHistoryShowEntity sh: return "SHOW " + EntityLine(s, sh.Entity);
                case PowerHistoryHideEntity hi: return "HIDE " + Name(s, hi.EntityID) + " zone=" + hi.Zone;
                case PowerHistoryChangeEntity c: return "CHANGE " + Name(s, c.Entity.Id) + " -> " + c.CardId;
                case PowerHistoryMetaData m: return "META " + m.Type + " data=" + m.Data + (m.Info != null && m.Info.Count > 0 ? " info=" + string.Join(",", m.Info.Select(i => Name(s, i))) : "");
                default: return h.Print().Replace("\n", " | ");
            }
        }

        string EntityLine(Session s, PowerHistoryEntity e)
        {
            var tags = e.Tags;
            string zone = tags != null && tags.TryGetValue(GameTag.ZONE, out int z) ? " zone=" + (Zone)z : "";
            string ctl = tags != null && tags.TryGetValue(GameTag.CONTROLLER, out int c) ? " ctl=" + (c == 1 ? "Me" : "Opp") : "";
            return (string.IsNullOrEmpty(e.Name) ? "?" : e.Name) + "#" + e.Id + " (" + Name(s, e.Id) + ")" + zone + ctl;
        }

        internal JsonArray Lines(Session s, int from, int to, int limit, Func<string, bool> filter = null)
        {
            var full = s.G.PowerHistory.Full;
            var arr = new JsonArray();
            int depth = 0, shown = 0;
            for (int i = from; i < to && i < full.Count; i++)
            {
                var h = full[i];
                if (h is PowerHistoryBlockEnd) depth = Math.Max(0, depth - 1);
                string line = new string(' ', depth * 2) + Line(s, h);
                if (h is PowerHistoryBlockStart) depth++;
                if (filter != null && !filter(line)) continue;
                if (shown++ >= limit) { arr.Add("... " + (to - i) + " more entries (use game_history with from=" + i + ")"); break; }
                arr.Add(i + ": " + line);
            }
            return arr;
        }

        JsonNode History(Session s, JsonObject req)
        {
            var full = s.G.PowerHistory.Full;
            string range = Str(req, "range") ?? "step";
            int from = Int(req, "from") ?? (range == "full" ? 0 : s.StepStart);
            int to = Int(req, "to") ?? full.Count;
            string grep = Str(req, "grep");
            Regex re = grep == null ? null : new Regex(grep, RegexOptions.IgnoreCase);
            return new JsonObject { ["from"] = from, ["to"] = to, ["total"] = full.Count, ["lines"] = Lines(s, from, to, Int(req, "limit") ?? 400, re == null ? null : (Func<string, bool>)(l => re.IsMatch(l))) };
        }

        // Engine-internal gates that make Game.Process refuse a task (see Game.Process).
        JsonNode Diag(Session s)
        {
            var g = s.G;
            var o = new JsonObject
            {
                ["autonomousTurn"] = SabberStoneCore.Actions.AutonomousTurnRules.IsActive(g),
                ["rewindStage"] = g.Rewind?.Stage.ToString(),
                ["suspendedEffect"] = g.Resolution.HasSuspendedEffect,
                ["spellCompletion"] = g.SpellCompletion != null,
                ["taskQueueEmpty"] = g.TaskQueue.IsEmpty,
                ["pendingDecisions"] = new JsonArray(g.GetPendingDecisions().Select(d => (JsonNode)d.ToString()).ToArray()),
                // Every entity's activated trigger: the owner, its zone and the trigger type (private in the engine, read by reflection).
                ["triggers"] = new JsonArray(g.IdEntityDic.Values.OfType<IPlayable>().Where(e => e.ActivatedTrigger != null).Select(e => {
                    var tr = e.ActivatedTrigger; var owner = (IEntity)e;
                    var type = typeof(SabberStoneCore.Triggers.Trigger).GetField("_triggerType", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(tr);
                    return (JsonNode)new JsonObject { ["owner"] = owner is IPlayable p ? Label(p) : owner?.ToString(), ["zone"] = (owner as IPlayable)?.Zone?.Type.ToString(),
                        ["type"] = type?.ToString(), ["source"] = tr.TriggerSource.ToString() };
                }).ToArray()),
            };
            return o;
        }

        // ---------- checkpoints ----------
        JsonNode Checkpoint(Session s, string name)
        {
            s.Checkpoints[name] = (s.G.SaveSnapshot(), new Dictionary<string, int>(s.Aliases));
            return new JsonObject { ["checkpoint"] = name, ["bytes"] = s.Checkpoints[name].snap.Length };
        }

        JsonNode Restore(Session s, string name)
        {
            if (!s.Checkpoints.TryGetValue(name, out var cp)) throw new UserError("no checkpoint " + name + "; have " + string.Join(",", s.Checkpoints.Keys));
            var config = (SabberStoneCore.Config.GameConfig)typeof(Game).GetField("_gameConfig", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(s.G);
            config = config.Clone();
            config.History = true; config.Logging = false;
            s.G = Game.LoadSnapshot(cp.snap, config);
            s.Aliases.Clear();
            foreach (var kv in cp.aliases) s.Aliases[kv.Key] = kv.Value;
            s.StepStart = s.G.PowerHistory?.Full.Count ?? 0;
            return State(s, false);
        }
    }
}
