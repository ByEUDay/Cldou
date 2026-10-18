using System;
using System.Collections.Generic;
using System.Linq;
using Cldou.Core.Manifests;
using Cldou.Core.Versioning;

namespace Cldou.Core.Resolution
{
    /// <summary>被排除的 mod 及原因。</summary>
    public sealed class ExcludedEntry
    {
        public CldouManifest Mod;
        public string Reason;
    }

    /// <summary>依赖求解结果。</summary>
    public sealed class LoadPlan
    {
        /// <summary>最终加载顺序（先加载者在前）。</summary>
        public List<CldouManifest> Ordered = new List<CldouManifest>();

        /// <summary>被排除的 mod 及原因。</summary>
        public List<ExcludedEntry> Excluded = new List<ExcludedEntry>();

        /// <summary>求解过程中的警告（软边放宽、重复 id、缺失可选项等）。</summary>
        public List<string> Warnings = new List<string>();

        /// <summary>声明为 manual、但因被启动态 mod 依赖而需要强制随启动加载的 id 集合。</summary>
        public HashSet<string> ForcedActivation = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// 确定性依赖求解器。
    ///
    /// 规则：
    ///   1. 重复 id：保留版本最高者，其余记录警告（shadowed）。
    ///   2. 硬依赖（dependencies）：缺失 / 版本不满足 → 排除该 mod；被排除者的依赖方级联排除。
    ///   3. 可选依赖（optionalDependencies）：存在且满足 → 作为软排序边；存在问题但不满足 → 警告；不存在 → 忽略。
    ///   4. 排序边：依赖 → 依赖方（硬边）；loadAfter / loadBefore（软边）。
    ///   5. 环处理：优先移除环上的软边（放宽排序）；若为硬依赖环，则排除环上全部 mod。
    ///   6. 拓扑排序采用字典序决胜，保证多次求解结果完全一致。
    /// </summary>
    public static class DependencyGraph
    {
        private sealed class EdgeSet
        {
            public readonly Dictionary<string, HashSet<string>> Direct =
                new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            public readonly HashSet<string> Soft = new HashSet<string>(StringComparer.Ordinal);

            public void Add(string from, string to, bool soft, HashSet<string> removedSoft)
            {
                if (from == to)
                {
                    return;
                }

                if (soft && removedSoft != null && removedSoft.Contains(Key(from, to)))
                {
                    return;
                }

                HashSet<string> targets;
                if (!Direct.TryGetValue(from, out targets))
                {
                    targets = new HashSet<string>(StringComparer.Ordinal);
                    Direct[from] = targets;
                }
                targets.Add(to);

                if (soft)
                {
                    Soft.Add(Key(from, to));
                }
            }

            public void Remove(string from, string to)
            {
                HashSet<string> targets;
                if (Direct.TryGetValue(from, out targets))
                {
                    targets.Remove(to);
                }
                Soft.Remove(Key(from, to));
            }

            public static string Key(string from, string to)
            {
                return from + "|" + to;
            }
        }

        public static LoadPlan Resolve(IEnumerable<CldouManifest> manifests)
        {
            var plan = new LoadPlan();

            // ---- 1. 去重（保留最高版本） ----
            var byId = new Dictionary<string, CldouManifest>(StringComparer.Ordinal);
            foreach (CldouManifest m in manifests)
            {
                if (m == null || string.IsNullOrEmpty(m.Id))
                {
                    continue;
                }

                CldouManifest existing;
                if (byId.TryGetValue(m.Id, out existing))
                {
                    CldouManifest winner = PickWinner(existing, m);
                    CldouManifest loser = ReferenceEquals(winner, existing) ? m : existing;
                    byId[m.Id] = winner;
                    AddWarn(plan, "重复的 Mod Id '" + m.Id + "'：保留 " + winner.Version + "（" + winner.SourcePath
                        + "），忽略 " + loser.Version + "（" + loser.SourcePath + "）");
                }
                else
                {
                    byId[m.Id] = m;
                }
            }

            var excluded = new Dictionary<string, string>(StringComparer.Ordinal);
            var removedSoft = new HashSet<string>(StringComparer.Ordinal);
            List<string> order = new List<string>();

            // ---- 主循环：级联排除 → 建边 → 拓扑 →（软边放宽 / 硬环排除）----
            int guard = 0;
            int maxIterations = byId.Count * 3 + 8;
            while (true)
            {
                guard++;
                if (guard > maxIterations)
                {
                    AddWarn(plan, "依赖求解达到迭代上限，按当前状态收敛");
                    break;
                }

                CascadeExclusions(byId, excluded, plan);

                List<string> kept = byId.Keys
                    .Where(id => !excluded.ContainsKey(id))
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();

                var edges = BuildEdges(byId, kept, plan, removedSoft);

                List<string> leftover;
                order = TopoSort(kept, edges.Direct, out leftover);

                if (leftover.Count == 0)
                {
                    break;
                }

                List<string> cycle = FindCycle(edges.Direct, leftover);
                if (cycle == null || cycle.Count == 0)
                {
                    AddWarn(plan, "存在无法定位的依赖环，剩余节点按字典序追加");
                    order.AddRange(leftover.OrderBy(id => id, StringComparer.Ordinal));
                    break;
                }

                // 优先尝试放宽环上的软边
                string softFrom = null;
                string softTo = null;
                for (int i = 0; i < cycle.Count; i++)
                {
                    string from = cycle[i];
                    string to = cycle[(i + 1) % cycle.Count];
                    if (edges.Soft.Contains(EdgeSet.Key(from, to)))
                    {
                        softFrom = from;
                        softTo = to;
                        break;
                    }
                }

                if (softFrom != null)
                {
                    removedSoft.Add(EdgeSet.Key(softFrom, softTo));
                    AddWarn(plan, "排序约束循环已放宽：移除软排序边 " + softFrom + " → " + softTo);
                    continue; // 重建后重新求解
                }

                // 硬依赖环：排除环上所有节点
                var cycleText = string.Join(" → ", cycle.ToArray()) + " → " + cycle[0];
                AddWarn(plan, "检测到硬依赖循环（相关 Mod 将被排除）：" + cycleText);
                foreach (string id in cycle)
                {
                    excluded[id] = "循环依赖：" + cycleText;
                }
            }

            // ---- 收尾：manual 强制激活提示 ----
            foreach (CldouManifest manual in order.Select(id => byId[id]).Where(m => m.IsManualActivation))
            {
                bool needed = order.Any(other =>
                {
                    CldouManifest candidate = byId[other];
                    return !candidate.IsManualActivation
                        && !ReferenceEquals(candidate, manual)
                        && candidate.Dependencies.ContainsKey(manual.Id);
                });

                if (needed)
                {
                    plan.ForcedActivation.Add(manual.Id);
                    AddWarn(plan, "'" + manual.Id + "' 声明为 manual，但被启动态 Mod 依赖，将强制随启动加载");
                }
            }

            // ---- 组装结果 ----
            plan.Ordered = order.Select(id => byId[id]).ToList();
            plan.Excluded = excluded
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new ExcludedEntry { Mod = byId[pair.Key], Reason = pair.Value })
                .ToList();
            return plan;
        }

        private static CldouManifest PickWinner(CldouManifest a, CldouManifest b)
        {
            if (a.SemVersion != null && b.SemVersion != null)
            {
                int cmp = a.SemVersion.CompareTo(b.SemVersion);
                if (cmp != 0)
                {
                    return cmp > 0 ? a : b;
                }
            }
            else if (a.SemVersion != null)
            {
                return a;
            }
            else if (b.SemVersion != null)
            {
                return b;
            }

            // 版本相同：取路径字典序较小者，保证确定性
            return string.CompareOrdinal(a.SourcePath ?? "", b.SourcePath ?? "") <= 0 ? a : b;
        }

        private static void CascadeExclusions(Dictionary<string, CldouManifest> byId, Dictionary<string, string> excluded, LoadPlan plan)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var pair in byId.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    string id = pair.Key;
                    CldouManifest m = pair.Value;
                    if (excluded.ContainsKey(id))
                    {
                        continue;
                    }

                    foreach (KeyValuePair<string, string> dep in m.Dependencies)
                    {
                        string why;
                        if (excluded.TryGetValue(dep.Key, out why))
                        {
                            excluded[id] = "依赖的 '" + dep.Key + "' 已被排除（" + why + "）";
                            changed = true;
                            break;
                        }

                        CldouManifest depMod;
                        if (!byId.TryGetValue(dep.Key, out depMod))
                        {
                            excluded[id] = "缺少依赖 '" + dep.Key + "'（需要 " + dep.Value + "）";
                            changed = true;
                            break;
                        }

                        if (!ConstraintSatisfied(depMod, dep.Value, plan, m))
                        {
                            excluded[id] = "依赖版本不满足：需要 '" + dep.Key + "' " + dep.Value + "，实际为 " + depMod.Version;
                            changed = true;
                            break;
                        }
                    }
                }
            }
        }

        private static bool ConstraintSatisfied(CldouManifest dependency, string constraint, LoadPlan plan, CldouManifest dependent)
        {
            if (dependency.SemVersion == null)
            {
                AddWarn(plan, "'" + dependent.Id + "' 的依赖 '" + dependency.Id + "' 版本号无法解析，跳过版本检查");
                return true;
            }

            VersionRange range;
            string error;
            if (!VersionRange.TryParse(constraint, out range, out error))
            {
                AddWarn(plan, "'" + dependent.Id + "' 的依赖约束 '" + constraint + "' 无法解析（按 * 处理）：" + error);
                return true;
            }

            return range.Contains(dependency.SemVersion);
        }

        private static EdgeSet BuildEdges(Dictionary<string, CldouManifest> byId, List<string> kept, LoadPlan plan, HashSet<string> removedSoft)
        {
            var keptSet = new HashSet<string>(kept, StringComparer.Ordinal);
            var edges = new EdgeSet();

            foreach (string id in kept)
            {
                CldouManifest m = byId[id];

                // 硬依赖：dependency → dependent
                foreach (KeyValuePair<string, string> dep in m.Dependencies)
                {
                    if (keptSet.Contains(dep.Key))
                    {
                        edges.Add(dep.Key, id, false, removedSoft);
                    }
                }

                // 可选依赖：存在且满足 → 软边（不影响排除）
                foreach (KeyValuePair<string, string> dep in m.OptionalDependencies)
                {
                    CldouManifest depMod;
                    if (!byId.TryGetValue(dep.Key, out depMod))
                    {
                        continue; // 可选，不存在则忽略
                    }

                    if (!keptSet.Contains(dep.Key))
                    {
                        AddWarn(plan, "'" + id + "' 的可选依赖 '" + dep.Key + "' 已被排除");
                        continue;
                    }

                    if (ConstraintSatisfied(depMod, dep.Value, plan, m))
                    {
                        edges.Add(dep.Key, id, true, removedSoft);
                    }
                    else
                    {
                        AddWarn(plan, "'" + id + "' 的可选依赖 '" + dep.Key + "' 版本不满足（需要 " + dep.Value + "，实际 " + depMod.Version + "），已忽略");
                    }
                }

                // loadAfter：目标 → 自己（软边）
                foreach (string after in m.LoadAfter)
                {
                    if (keptSet.Contains(after))
                    {
                        edges.Add(after, id, true, removedSoft);
                    }
                    else if (!byId.ContainsKey(after))
                    {
                        AddWarn(plan, "'" + id + "' 的 loadAfter 目标 '" + after + "' 不存在，已忽略");
                    }
                }

                // loadBefore：自己 → 目标（软边）
                foreach (string before in m.LoadBefore)
                {
                    if (keptSet.Contains(before))
                    {
                        edges.Add(id, before, true, removedSoft);
                    }
                    else if (!byId.ContainsKey(before))
                    {
                        AddWarn(plan, "'" + id + "' 的 loadBefore 目标 '" + before + "' 不存在，已忽略");
                    }
                }
            }

            return edges;
        }

        private static List<string> TopoSort(List<string> nodes, Dictionary<string, HashSet<string>> edges, out List<string> leftover)
        {
            var indegree = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string node in nodes)
            {
                indegree[node] = 0;
            }

            foreach (var pair in edges)
            {
                if (!indegree.ContainsKey(pair.Key))
                {
                    continue;
                }
                foreach (string to in pair.Value)
                {
                    if (indegree.ContainsKey(to))
                    {
                        indegree[to] = indegree[to] + 1;
                    }
                }
            }

            var ready = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var pair in indegree)
            {
                if (pair.Value == 0)
                {
                    ready.Add(pair.Key);
                }
            }

            var order = new List<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal);

            while (ready.Count > 0)
            {
                string node = ready.Min;
                ready.Remove(node);
                order.Add(node);
                visited.Add(node);

                HashSet<string> targets;
                if (edges.TryGetValue(node, out targets))
                {
                    foreach (string to in targets.OrderBy(x => x, StringComparer.Ordinal))
                    {
                        if (!indegree.ContainsKey(to))
                        {
                            continue;
                        }
                        indegree[to] = indegree[to] - 1;
                        if (indegree[to] == 0)
                        {
                            ready.Add(to);
                        }
                    }
                }
            }

            leftover = nodes.Where(n => !visited.Contains(n)).ToList();
            return order;
        }

        /// <summary>在候选中寻找一个环。返回形如 [a, b, c]，边为 a→b→c→a。</summary>
        private static List<string> FindCycle(Dictionary<string, HashSet<string>> edges, List<string> candidates)
        {
            var candidateSet = new HashSet<string>(candidates, StringComparer.Ordinal);
            var color = new Dictionary<string, int>(StringComparer.Ordinal); // 0 未见 / 1 在栈 / 2 完成
            var parent = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (string start in candidates.OrderBy(x => x, StringComparer.Ordinal))
            {
                if (color.ContainsKey(start))
                {
                    continue;
                }

                var stack = new Stack<string>();
                var iterators = new Dictionary<string, IEnumerator<string>>(StringComparer.Ordinal);
                stack.Push(start);
                color[start] = 1;
                iterators[start] = GetNeighbors(edges, start, candidateSet).GetEnumerator();

                while (stack.Count > 0)
                {
                    string node = stack.Peek();
                    IEnumerator<string> iterator = iterators[node];

                    if (iterator.MoveNext())
                    {
                        string next = iterator.Current;
                        int state;
                        if (color.TryGetValue(next, out state))
                        {
                            if (state == 1)
                            {
                                // 找到环：node → next，沿 parent 回溯
                                var cycle = new List<string>();
                                string walk = node;
                                cycle.Add(walk);
                                while (walk != next)
                                {
                                    walk = parent[walk];
                                    cycle.Add(walk);
                                }
                                cycle.Reverse(); // [next, ..., node]
                                return cycle;
                            }
                            continue;
                        }

                        color[next] = 1;
                        parent[next] = node;
                        iterators[next] = GetNeighbors(edges, next, candidateSet).GetEnumerator();
                        stack.Push(next);
                    }
                    else
                    {
                        color[node] = 2;
                        iterators.Remove(node);
                        stack.Pop();
                    }
                }
            }

            return null;
        }

        private static List<string> GetNeighbors(Dictionary<string, HashSet<string>> edges, string node, HashSet<string> candidateSet)
        {
            HashSet<string> targets;
            if (!edges.TryGetValue(node, out targets))
            {
                return new List<string>();
            }
            return targets
                .Where(t => candidateSet.Contains(t))
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList();
        }

        private static void AddWarn(LoadPlan plan, string message)
        {
            if (!plan.Warnings.Contains(message))
            {
                plan.Warnings.Add(message);
            }
        }
    }
}
