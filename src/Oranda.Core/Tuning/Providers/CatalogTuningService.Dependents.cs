using Oranda.Core.Catalog.Providers;
using Oranda.Core.Sessions;
using static Oranda.Core.Catalog.Providers.CatalogValues;

namespace Oranda.Core.Tuning.Providers;

/// <summary>객체에 기대는 것들(TSK-15-09, AI 도구 <c>get_dependents</c>). 카탈로그 <c>tuning.object_dependents</c> 한 번으로 네 갈래를 함께 읽는다.</summary>
public abstract partial class CatalogTuningService
{
    public async Task<IReadOnlyList<ObjectDependentRow>> GetObjectDependentsAsync(ISession s, string owner, string name, CancellationToken ct)
    {
        var rows = (await QueryAsync(s, "tuning.object_dependents",
            CatalogBinds.Defer(() =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(owner);
                ArgumentException.ThrowIfNullOrWhiteSpace(name);
                return [CatalogBinds.Trimmed("owner", owner), CatalogBinds.Trimmed("name", name)];
            }), TuningQueryOptions, ct)).Rows;
        return [.. rows.Select(ToDependent).OfType<ObjectDependentRow>()];
    }

    /// <summary>카탈로그 한 줄(kind, owner, name, detail, status) → 줄. 모르는 갈래는 버린다.</summary>
    internal static ObjectDependentRow? ToDependent(object?[] r)
    {
        ArgumentNullException.ThrowIfNull(r);
        ObjectDependentKind? kind = Str(r[0])?.Trim().ToUpperInvariant() switch
        {
            "DEPENDENT" => ObjectDependentKind.Dependent,
            "FK" => ObjectDependentKind.ForeignKey,
            "TRIGGER" => ObjectDependentKind.Trigger,
            "SYNONYM" => ObjectDependentKind.Synonym,
            _ => null,
        };
        if (kind is not { } k || Str(r[1]) is not { Length: > 0 } o || Str(r[2]) is not { Length: > 0 } n)
        {
            return null;
        }

        return new ObjectDependentRow(k, o, n, Blank(Str(r[3])), Blank(Str(r[4])));
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
