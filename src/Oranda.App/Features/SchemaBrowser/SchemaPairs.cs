using System;
using System.Collections.Generic;
using System.Linq;
using Oranda.App.Features.PlsqlEditor;
using Oranda.Core.Metadata;

namespace Oranda.App.Features.SchemaBrowser;

/// <summary>
/// 스키마 트리에서 패키지·타입의 명세와 본문을 한 줄로 묶는다(PKG1, D-067). 「패키지」 범주는 패키지마다 한 노드이고,
/// 본문(PACKAGE BODY)은 그 노드의 상태 표로만 보인다. 명세 없는 본문(드묾)도 같은 이름의 노드로 보인다.
/// </summary>
internal static class SchemaPairs
{
    /// <summary>객체가 들어갈 범주 유형(본문은 명세 범주).</summary>
    public static string CategoryOf(string objectType) => PlsqlSourceText.SpecTypeOf(objectType);

    /// <summary>명세/본문 쌍이 있는 유형(PACKAGE·PACKAGE BODY·TYPE·TYPE BODY).</summary>
    public static bool IsPairType(string objectType) => PlsqlSourceText.SiblingType(objectType) is not null;

    /// <summary>목록 조회에 넘길 유형: 명세 범주를 고르면 그 본문도 읽는다(본문 상태 표). 빈 목록(모든 유형)은 그대로.</summary>
    public static IReadOnlyList<string> WithBodies(IReadOnlyList<string> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        if (types.Count == 0)
        {
            return types;
        }

        var all = new List<string>(types);
        foreach (var t in types)
        {
            if (IsPairType(t) && PlsqlSourceText.SiblingType(CategoryOf(t)) is { } body && !all.Contains(body, StringComparer.Ordinal))
            {
                all.Add(body);
            }
        }

        return all;
    }

    /// <summary>
    /// 같은 소유자·이름의 명세와 본문을 한 쌍으로 묶는다(처음 나온 차례). 쌍이 없는 유형은 넘기지 않는다(호출자가 따로 둔다).
    /// 대표 참조는 명세(없으면 본문)다.
    /// </summary>
    public static List<SchemaPair> Pair(IEnumerable<DbObjectRef> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        var order = new List<(string Owner, string Name, string Type)>();
        var specs = new Dictionary<(string, string, string), DbObjectRef>();
        var bodies = new Dictionary<(string, string, string), DbObjectRef>();
        foreach (var o in objects)
        {
            var key = (o.Owner, o.Name, CategoryOf(o.ObjectType));
            if (!specs.ContainsKey(key) && !bodies.ContainsKey(key))
            {
                order.Add(key);
            }

            if (PlsqlSourceText.IsBodyType(o.ObjectType))
            {
                bodies.TryAdd(key, o);
            }
            else
            {
                specs.TryAdd(key, o);
            }
        }

        return [.. order.Select(k =>
        {
            var spec = specs.GetValueOrDefault(k);
            var body = bodies.GetValueOrDefault(k);
            return new SchemaPair(spec ?? body!, spec, body);
        })];
    }

    /// <summary>쪽 상태: 없으면 <see cref="SchemaPartState.Missing"/>(INVALID 만 보기에서는 모름 = 표 없음), 있으면 INVALID 목록에 따라.</summary>
    public static SchemaPartState StateOf(DbObjectRef? part, Func<DbObjectRef, bool> isInvalid, bool invalidOnly)
        => part is null ? (invalidOnly ? SchemaPartState.None : SchemaPartState.Missing)
            : isInvalid(part) ? SchemaPartState.Invalid : SchemaPartState.Valid;
}
