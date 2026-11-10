namespace Oranda.Core.Transfer.Copy;

public sealed record CopyOrderResult(IReadOnlyList<string> ParentsFirst, IReadOnlyList<IReadOnlyList<string>> Cycles);

/// <summary>FK 간선으로 부모 → 자식 순서를 정한다. 입력 순서를 지키는 안정 정렬(Kahn)이다.</summary>
public static class CopyOrder
{
    public static CopyOrderResult Sort(IReadOnlyList<string> tables, IReadOnlyList<(string Child, string Parent)> edges)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < tables.Count; i++) index.TryAdd(tables[i], i);

        // 자식 → 부모 집합. 자기 참조와 작업 밖 테이블이 낀 간선은 무시한다.
        var parents = tables.Distinct(StringComparer.Ordinal).ToDictionary(t => t, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var (child, parent) in edges)
        {
            if (child == parent || !parents.ContainsKey(child) || !parents.ContainsKey(parent)) continue;
            parents[child].Add(parent);
        }

        var done = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        var remaining = parents.Keys.OrderBy(t => index[t]).ToList();
        var cycles = new List<IReadOnlyList<string>>();

        void Drain()
        {
            while (remaining.Count > 0)
            {
                var next = remaining.FirstOrDefault(t => parents[t].All(done.Contains));
                if (next is null) return;
                remaining.Remove(next);
                done.Add(next);
                result.Add(next);
            }
        }

        Drain();
        if (remaining.Count > 0)
        {
            // 순환 묶음을 먼저 붙인 뒤, 순환에 기대어 막혀 있던 자손을 마저 정렬한다.
            cycles.AddRange(FindCycles(remaining, parents));
            var inCycle = cycles.SelectMany(c => c).ToHashSet(StringComparer.Ordinal);
            foreach (var t in remaining.Where(inCycle.Contains).ToList())
            {
                remaining.Remove(t);
                done.Add(t);
                result.Add(t);
            }
            Drain();
            result.AddRange(remaining);
        }
        return new CopyOrderResult(result, cycles);
    }

    /// <summary>
    /// 동시 복사(PF2)의 FK 단계. 단계 = 1 + 부모 단계 최댓값(부모가 없으면 1)이고, 한 단계의 테이블은 서로 FK 로 이어지지 않아 함께 적재해도 된다.
    /// 간선은 <see cref="Sort"/> 와 같다(자기 참조·작업 밖 테이블은 무시). FK 순환 묶음과 그 뒤에 기대는 테이블(순환 묶음의 자손)은
    /// 한 테이블씩 단독 단계로 맨 뒤에 둔다(<see cref="Sort"/> 의 직렬 순서 그대로). 단계 안 순서와 단계 사이 순서는 <see cref="Sort"/> 의 부모 → 자식 순서다.
    /// 단계를 모두 이으면 <see cref="CopyOrderResult.ParentsFirst"/> 와 같은 테이블을 한 번씩 담는다.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> Stages(IReadOnlyList<string> tables, IReadOnlyList<(string Child, string Parent)> edges)
    {
        var sorted = Sort(tables, edges);
        var known = sorted.ParentsFirst.ToHashSet(StringComparer.Ordinal);
        var parents = known.ToDictionary(t => t, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var (child, parent) in edges)
        {
            if (child == parent || !known.Contains(child) || !known.Contains(parent)) continue;
            parents[child].Add(parent);
        }

        // 순환 묶음과 그 자손: 부모 → 자식 순서로 한 번 지나며 부모가 이 집합에 있으면 넣는다(순환 묶음 뒤에 기대는 테이블은 순서상 그 뒤다).
        var serial = sorted.Cycles.SelectMany(c => c).ToHashSet(StringComparer.Ordinal);
        foreach (var t in sorted.ParentsFirst)
        {
            if (parents[t].Any(serial.Contains)) serial.Add(t);
        }

        var level = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in sorted.ParentsFirst.Where(t => !serial.Contains(t)))
        {
            // 직렬 집합 밖 테이블의 부모는 직렬 집합 밖이고 순서상 앞이다(이미 단계가 있다).
            level[t] = 1 + parents[t].Select(p => level.GetValueOrDefault(p)).DefaultIfEmpty(0).Max();
        }

        var stages = level.GroupBy(x => x.Value).OrderBy(g => g.Key)
            .Select(g => (IReadOnlyList<string>)[.. sorted.ParentsFirst.Where(t => level.TryGetValue(t, out var l) && l == g.Key)])
            .ToList();
        stages.AddRange(sorted.ParentsFirst.Where(serial.Contains).Select(t => (IReadOnlyList<string>)[t]));
        return stages;
    }

    // 남은 테이블 중 서로 도달 가능한 묶음(강한 연결 요소)을 입력 순서대로 낸다. 크기 1 이면 순환이 아니므로 뺀다.
    private static List<IReadOnlyList<string>> FindCycles(List<string> remaining, Dictionary<string, HashSet<string>> parents)
    {
        var set = remaining.ToHashSet(StringComparer.Ordinal);
        bool Reaches(string from, string to)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>();
            stack.Push(from);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                foreach (var p in parents[cur])
                {
                    if (!set.Contains(p)) continue;
                    if (p == to) return true;
                    if (seen.Add(p)) stack.Push(p);
                }
            }
            return false;
        }

        var cycles = new List<IReadOnlyList<string>>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in remaining)
        {
            if (used.Contains(t) || !Reaches(t, t)) continue;
            var group = remaining.Where(o => o == t || (Reaches(t, o) && Reaches(o, t))).ToList();
            foreach (var g in group) used.Add(g);
            cycles.Add(group);
        }
        return cycles;
    }
}
