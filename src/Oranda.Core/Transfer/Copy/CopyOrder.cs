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
