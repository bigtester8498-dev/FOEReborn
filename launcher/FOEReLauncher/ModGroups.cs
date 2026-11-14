using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FOEReLauncher
{
    /// <summary>A run of list rows: one group with its members, or one mod on its own (empty <see cref="GroupId"/>).</summary>
    public sealed class ModBlock<T>
    {
        public ModBlock(string groupId)
        {
            GroupId = groupId;
        }

        public string GroupId { get; }

        public List<T> Items { get; } = new List<T>();

        public bool IsGroup => GroupId.Length > 0;
    }

    /// <summary>What switching a whole group on or off changes, worked out with the game's rules before anything is saved.</summary>
    public sealed class GroupTogglePlan
    {
        /// <summary>Mods of the group that change, in the order they are switched.</summary>
        public List<string> Members { get; } = new List<string>();

        /// <summary>Mods outside the group that change with them (requirements switched on, dependents switched off).</summary>
        public List<string> Outside { get; } = new List<string>();

        /// <summary>Mods of the group left off, with the mod they conflict with or the game's reason.</summary>
        public List<KeyValuePair<string, string>> Skipped { get; } = new List<KeyValuePair<string, string>>();

        /// <summary>Code mods that are switched on without the player's consent for their version yet.</summary>
        public List<string> NeedConsent { get; } = new List<string>();

        public bool On { get; set; }

        public IEnumerable<string> All => Members.Concat(Outside);
    }

    /// <summary>The result of a run of one-by-one operations.</summary>
    public sealed class BatchResult<T>
    {
        public List<T> Done { get; } = new List<T>();

        public List<KeyValuePair<T, Exception>> Failed { get; } = new List<KeyValuePair<T, Exception>>();

        /// <summary>Items not started because the run was cancelled.</summary>
        public List<T> NotRun { get; } = new List<T>();

        public bool Cancelled { get; set; }
    }

    /// <summary>Mod groups: rows in group order, whole-group switches through <see cref="ModRules"/>, moves, batch runs.</summary>
    public static class ModGroups
    {
        /// <summary>
        /// Puts items into blocks in the order of their first appearance: a group's members are pulled up to its first
        /// member and keep their own order; an item without a group is a block of its own.
        /// </summary>
        public static List<ModBlock<T>> Arrange<T>(IEnumerable<T> items, Func<T, string> groupOf)
        {
            var blocks = new List<ModBlock<T>>();
            var byGroup = new Dictionary<string, ModBlock<T>>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                var g = groupOf(item) ?? string.Empty;
                if (g.Length == 0)
                {
                    var single = new ModBlock<T>(string.Empty);
                    single.Items.Add(item);
                    blocks.Add(single);
                    continue;
                }

                if (!byGroup.TryGetValue(g, out var block))
                {
                    block = new ModBlock<T>(g);
                    byGroup[g] = block;
                    blocks.Add(block);
                }

                block.Items.Add(item);
            }

            return blocks;
        }

        /// <summary>
        /// Plans switching every mod of a group on or off as single switches would, one after another: switching on
        /// takes the mods they require (also outside the group) and leaves off a mod that conflicts with one already on
        /// (the game would refuse to load both); switching off takes the enabled mods outside the group that require
        /// one of them. Broken or duplicate packages are skipped.
        /// </summary>
        public static GroupTogglePlan PlanToggle(IList<string> members, bool on, IList<ModPackage> all, ModSelection sel)
        {
            var plan = new GroupTogglePlan { On = on };
            var byId = new Dictionary<string, ModPackage>(StringComparer.Ordinal);
            foreach (var p in all)
            {
                if (!p.Core && p.State != ModState.Duplicate && !byId.ContainsKey(p.Id))
                {
                    byId[p.Id] = p;
                }
            }

            var work = sel.Clone();
            if (on)
            {
                foreach (var id in members)
                {
                    if (!byId.TryGetValue(id, out var p) || work.IsEnabled(id))
                    {
                        continue;
                    }

                    if (p.State == ModState.Invalid)
                    {
                        plan.Skipped.Add(new KeyValuePair<string, string>(id, string.Empty));
                        continue;
                    }

                    var needs = ModRules.ToEnable(id, all, work);
                    var conflict = new[] { id }.Concat(needs).Select(x => ConflictWith(x, byId, work)).FirstOrDefault(x => x != null);
                    if (conflict != null)
                    {
                        plan.Skipped.Add(new KeyValuePair<string, string>(id, conflict));
                        continue;
                    }

                    foreach (var x in needs.Concat(new[] { id }))
                    {
                        if (work.IsEnabled(x))
                        {
                            continue;
                        }

                        work.SetEnabled(x, true);
                        (members.Contains(x) ? plan.Members : plan.Outside).Add(x);
                        if (byId.TryGetValue(x, out var px) && px.HasCode && !sel.HasConsent(x, px.Manifest.Version))
                        {
                            plan.NeedConsent.Add(x);
                        }
                    }
                }
            }
            else
            {
                foreach (var id in members)
                {
                    if (!work.IsEnabled(id))
                    {
                        continue;
                    }

                    plan.Members.Add(id);
                    foreach (var x in ModRules.ToDisable(id, all, work))
                    {
                        if (!members.Contains(x) && !plan.Outside.Contains(x))
                        {
                            plan.Outside.Add(x);
                        }
                    }
                }

                foreach (var x in plan.Members.Concat(plan.Outside).ToList())
                {
                    work.SetEnabled(x, false);
                }
            }

            return plan;
        }

        /// <summary>The selection after <paramref name="plan"/>, with consent stored for the given code mods.</summary>
        public static ModSelection Apply(ModSelection sel, GroupTogglePlan plan, IEnumerable<ModPackage> consented)
        {
            var res = sel.Clone();
            foreach (var p in consented)
            {
                res.Consent[p.Id] = p.Manifest.Version;
            }

            foreach (var id in plan.All)
            {
                res.SetEnabled(id, plan.On);
            }

            return res;
        }

        /// <summary>An enabled mod that <paramref name="id"/> is incompatible with, or that is incompatible with it.</summary>
        private static string? ConflictWith(string id, IDictionary<string, ModPackage> byId, ModSelection sel)
        {
            if (!byId.TryGetValue(id, out var p))
            {
                return null;
            }

            foreach (var other in sel.Enabled)
            {
                if (other == id || !byId.TryGetValue(other, out var q))
                {
                    continue;
                }

                if (p.Manifest.Incompatible.Any(r => r.Id == q.Id && ModVersion.Satisfies(q.Manifest.Version, r.Range)) ||
                    q.Manifest.Incompatible.Any(r => r.Id == p.Id && ModVersion.Satisfies(p.Manifest.Version, r.Range)))
                {
                    return other;
                }
            }

            return null;
        }

        /// <summary>
        /// The load order after moving one row by one place in the list as shown: a mod moves among the members of its
        /// group (or among the blocks when it has none), a group header moves the whole group past the neighbouring
        /// block. The returned order is the whole list as shown, so the saved order and the screen agree.
        /// </summary>
        public static List<string> Move(List<ModBlock<string>> blocks, string? groupId, string? modId, int delta)
        {
            var copy = blocks.Select(b =>
            {
                var c = new ModBlock<string>(b.GroupId);
                c.Items.AddRange(b.Items);
                return c;
            }).ToList();

            if (!string.IsNullOrEmpty(groupId) || copy.Any(b => !b.IsGroup && b.Items.Contains(modId ?? string.Empty)))
            {
                var i = copy.FindIndex(b => !string.IsNullOrEmpty(groupId) ? b.GroupId == groupId : !b.IsGroup && b.Items.Contains(modId!));
                var j = i + Math.Sign(delta);
                if (i >= 0 && j >= 0 && j < copy.Count)
                {
                    var b = copy[i];
                    copy.RemoveAt(i);
                    copy.Insert(j, b);
                }
            }
            else
            {
                var block = copy.FirstOrDefault(b => b.Items.Contains(modId ?? string.Empty));
                if (block != null)
                {
                    var i = block.Items.IndexOf(modId!);
                    var j = i + Math.Sign(delta);
                    if (j >= 0 && j < block.Items.Count)
                    {
                        block.Items.RemoveAt(i);
                        block.Items.Insert(j, modId!);
                    }
                }
            }

            return copy.SelectMany(b => b.Items).ToList();
        }

        /// <summary>
        /// Runs <paramref name="work"/> for every item, strictly one after another. A failure of one item is recorded
        /// and the run goes on; cancelling stops it: the running item ends as cancelled and the rest are not started.
        /// </summary>
        public static async Task<BatchResult<T>> RunOneByOneAsync<T>(IList<T> items, Func<T, int, CancellationToken, Task> work, CancellationToken ct)
        {
            var res = new BatchResult<T>();
            for (var i = 0; i < items.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    res.Cancelled = true;
                    res.NotRun.AddRange(items.Skip(i));
                    break;
                }

                try
                {
                    await work(items[i], i, ct);
                    res.Done.Add(items[i]);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    res.Cancelled = true;
                    res.NotRun.AddRange(items.Skip(i));
                    break;
                }
                catch (Exception ex)
                {
                    res.Failed.Add(new KeyValuePair<T, Exception>(items[i], ex));
                }
            }

            return res;
        }
    }
}
