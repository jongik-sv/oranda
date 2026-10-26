using System;
using System.Collections.Generic;
using System.Linq;
using Oranda.App.Features.Binds;
using Oranda.Core.Execution;

namespace Oranda.App.Features.DataTransfer;

/// <summary>서버에서 다시 읽는 내보내기에 넘길 바인드 값과, 값을 못 정했을 때의 이유.</summary>
internal sealed record ExportBindResult(IReadOnlyDictionary<string, BindValue> Binds, string? Problem);

/// <summary>
/// 내보내기가 질의를 서버에서 다시 실행할 때 쓸 바인드 값을 정한다. 문장에 나온 이름마다
/// (1) 그 결과를 만들 때 쓴 값(결과 탭이 남긴 것) → (2) 탭의 「바인드 변수」 목록 값 순서로 찾고,
/// 둘 다 없거나 목록 값이 형식에 맞지 않으면 서버 오류(ORA-01008) 대신 이유를 돌려준다. 문장에 없는 이름은 담지 않는다.
/// </summary>
internal static class ExportBinds
{
    public static ExportBindResult Resolve(Oranda.Core.Execution.SqlStatement query, IReadOnlyDictionary<string, BindValue>? ran, BindVariableStore? list)
    {
        ArgumentNullException.ThrowIfNull(query);
        var binds = new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        string? invalid = null;
        foreach (var name in query.BindNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ran is not null && ran.TryGetValue(name, out var used))
            {
                binds[name] = used;
            }
            else if (list?.Find(name) is { } row)
            {
                if (row.ToBindValue(out var error) is { } value)
                {
                    binds[name] = value;
                }
                else
                {
                    invalid ??= ExportText.BindInvalid(name, error);
                }
            }
            else
            {
                missing.Add(name);
            }
        }

        var problem = invalid ?? (missing.Count > 0 ? ExportText.BindMissing(missing) : null);
        return new ExportBindResult(binds, problem);
    }
}
