using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;

namespace Oranda.Language.Ai.Tools;

/// <summary>객체 하나: 이름(<c>OWNER.NAME</c>), 형식, 상태(VALID·INVALID, 모르면 null).</summary>
public sealed record DependentObjectDescription(string Name, string? Type, string? Status);

/// <summary>이 표를 참조하는 FK 하나: 자식 표 <c>OWNER.NAME</c>, 제약 이름, 상태(ENABLED·DISABLED).</summary>
public sealed record ReferencingForeignKeyDescription(string Table, string? Constraint, string? Status);

/// <summary>이 표의 트리거 하나: <c>OWNER.NAME</c>, 걸리는 사건, 상태.</summary>
public sealed record TableTriggerDescription(string Trigger, string? Event, string? Status);

/// <summary>
/// <c>get_dependents</c> 응답(TSK-15-09): 바꾸면 무효화될 수 있는 객체·이 표를 참조하는 FK·트리거·이 객체를 가리키는 동의어 수.
/// 목록은 갈래마다 <see cref="TuningToolbox.MaxDependentItems"/> 줄까지만 싣고, 수(<c>*_count</c>)는 모두 센다. 줄을 줄였으면 <see cref="Truncated"/>.
/// </summary>
public sealed record DependentsDescription(
    string Target,
    int InvalidatedCount, IReadOnlyList<DependentObjectDescription> Invalidated,
    int ForeignKeyCount, IReadOnlyList<ReferencingForeignKeyDescription> ForeignKeys,
    int TriggerCount, IReadOnlyList<TableTriggerDescription> Triggers,
    int SynonymCount, int PublicSynonymCount, bool Truncated);

public sealed partial class TuningToolbox
{
    /// <summary><c>get_dependents</c> 가 갈래마다 싣는 줄 수(수는 모두 센다).</summary>
    public const int MaxDependentItems = 25;

    /// <summary><c>get_dependents</c>: 객체에 기대는 것들(메타데이터 수준, 읽기 전용).</summary>
    public Task<DependentsDescription> GetDependentsAsync(ISession s, DbObjectRef obj, CancellationToken ct) =>
        Cached(s, "dependents", obj, string.Empty, () => GetDependentsCoreAsync(s, obj, ct), ct);

    private async Task<DependentsDescription> GetDependentsCoreAsync(ISession s, DbObjectRef obj, CancellationToken ct)
    {
        var rows = await tuning.GetObjectDependentsAsync(s, obj.Owner, obj.Name, ct).ConfigureAwait(false);
        return DescribeDependents(Name(obj), rows);
    }

    /// <summary>줄 목록 → 응답(갈래별로 나누고 수를 센다, 같은 줄은 한 번만).</summary>
    internal static DependentsDescription DescribeDependents(string name, IReadOnlyList<ObjectDependentRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var distinct = rows.Distinct().ToList();
        static string Key(ObjectDependentRow r) => r.Owner + "." + r.Name;
        var dependents = distinct.Where(r => r.Kind == ObjectDependentKind.Dependent).ToList();
        var keys = distinct.Where(r => r.Kind == ObjectDependentKind.ForeignKey).ToList();
        var triggers = distinct.Where(r => r.Kind == ObjectDependentKind.Trigger).ToList();
        var synonyms = distinct.Where(r => r.Kind == ObjectDependentKind.Synonym).ToList();
        var truncated = dependents.Count > MaxDependentItems || keys.Count > MaxDependentItems || triggers.Count > MaxDependentItems;
        return new DependentsDescription(
            name,
            dependents.Count, [.. dependents.Take(MaxDependentItems).Select(r => new DependentObjectDescription(Key(r), r.Detail, r.Status))],
            keys.Count, [.. keys.Take(MaxDependentItems).Select(r => new ReferencingForeignKeyDescription(Key(r), r.Detail, r.Status))],
            triggers.Count, [.. triggers.Take(MaxDependentItems).Select(r => new TableTriggerDescription(Key(r), r.Detail, r.Status))],
            synonyms.Count, synonyms.Count(r => string.Equals(r.Owner, "PUBLIC", StringComparison.OrdinalIgnoreCase)),
            truncated);
    }
}
