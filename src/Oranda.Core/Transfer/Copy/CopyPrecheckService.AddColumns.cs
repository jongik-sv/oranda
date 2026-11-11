using Oranda.Core.Execution;
using Oranda.Core.Metadata;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 사전 점검의 「대상에 열 추가」(PC1, 결정 D-066, <see cref="TableRule.AddColumns"/>). 점검은 문장을 정하고 미리 보기에 넣기만 한다 — 대상에 DDL 을 보내지 않는다.
/// 실행기가 대상 정리·적재 전에 같은 글을 보낸다(<c>CopyRunner.AddColumnsAsync</c>).
/// </summary>
public partial class CopyPrecheckService
{
    private sealed partial class Run
    {
        /// <summary>규칙 키 → 적재 전에 대상에 더하는 열의 문장(<see cref="CopyTablePlan.AddColumns"/>).</summary>
        private readonly Dictionary<string, IReadOnlyList<string>> _addColumns = new(StringComparer.Ordinal);

        /// <summary>
        /// 이미 있는 대상 테이블에 규칙의 「대상에 열 추가」 열을 더한 구조. 더할 열은 대상 구조에 NULL 허용 열로 넣어 열 비교·복사 열·문장이 그 열을 쓰게 한다.
        /// 대상에 이미 있거나 건너뛸 열이거나 원본에 없는 열은 무시한다. 개발 접속이 아니거나, 대상 소유자 계정이 아니고 ALTER ANY TABLE 도 없거나, 옮길 형식이 없으면
        /// 그 열은 더하지 않고 오류(<see cref="CopyCheckCodes.AddColumnBlocked"/>)를 낸다. 더하는 열마다 확인 항목(<see cref="CopyCheckCodes.AddColumn"/>).
        /// </summary>
        private TableInfo WithAddedColumns(TableRule rule, string key, TableInfo source, TableInfo target)
        {
            if (rule.AddColumns is not { Count: > 0 } wanted)
            {
                return target;
            }

            var skip = rule.SkipColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var have = target.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            var owner = target.Table.Owner;
            var columns = target.Columns.ToList();
            var position = columns.Count == 0 ? 0 : columns.Max(c => c.Position);
            var ddl = new List<string>();
            foreach (var name in wanted.Distinct(StringComparer.Ordinal))
            {
                if (have.Contains(name) || skip.Contains(name) || source.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal)) is not { } column)
                {
                    continue;
                }

                string? reason = !CanRecreate
                    ? $"대상 접속 '{Target.Profile.Name}' 이 개발 접속이 아니라 열을 더할 수 없습니다(개발 접속에서만 더합니다)"
                    : !_targetAccess.IsOwner(owner) && !_targetAccess.Has("ALTER ANY TABLE")
                        ? $"대상 소유자({owner}) 계정이 아니고 ALTER ANY TABLE 권한도 없어 열을 더할 수 없습니다(지금 {_targetAccess.User})"
                        : null;
                var add = reason is null ? CopyAddColumnDdl.Plan(sourceParts.Dbms, targetDbms, column, owner, rule.Table, sourceParts.Codec, out reason) : null;
                if (add is null)
                {
                    _items.Add(new CheckItem(key, CopyCheckCodes.AddColumnBlocked, CheckLevel.Error,
                        $"{key}.{name}: 「대상에 열 추가」를 쓸 수 없습니다 — {reason}. 이 열 없이 복사하려면 「그 열 빼고 복사」를 고르세요.")
                    {
                        Column = name,
                    });
                    continue;
                }

                columns.Add(add.Column with { Position = ++position });
                ddl.Add(add.Ddl);
                _items.Add(new CheckItem(key, CopyCheckCodes.AddColumn, CheckLevel.Confirm,
                    $"{key}.{name}: 대상에 없는 열이라 복사 전에 대상 {owner}.{rule.Table} 에 더합니다({CreateTableDdl.TypeText(add.Column)}, NULL 허용·기본값 없음)."
                    + " 적재가 실패해도 더한 열은 남깁니다(지우려면 ALTER TABLE … DROP COLUMN). 더하는 문장은 미리 보기에 있습니다.")
                {
                    Column = name,
                });
            }

            if (ddl.Count == 0)
            {
                return target;
            }

            _addColumns[key] = ddl;
            return target with { Columns = columns };
        }

        /// <summary>점검 계획에 더할 열의 문장을 붙인다(<see cref="CopyTablePlan.AddColumns"/>). 표를 만드는 계획은 원본 구조 그대로 만들므로 붙이지 않는다.</summary>
        private IReadOnlyList<CopyTablePlan> WithAddColumnPlans(IReadOnlyList<CopyTablePlan> order)
            => _addColumns.Count == 0
                ? order
                : [.. order.Select(p => !p.CreateMissing && _addColumns.TryGetValue(Key(p.Rule), out var ddl) ? p with { AddColumns = ddl } : p)];

        /// <summary>더할 열의 문장(부모 → 자식 순서). 실행기가 보내는 글과 같다: 정책이 확인을 요구하면 화면이 미리 받은 확인으로 넘긴다.</summary>
        private async Task<IReadOnlyList<PreviewStatement>> AddColumnPreviewAsync(IReadOnlyList<CopyTablePlan> order, CancellationToken ct)
        {
            var list = new List<PreviewStatement>();
            foreach (var text in order.SelectMany(p => p.AddColumns))
            {
                list.Add(await PreviewAsync(Target, CopyCommonStatements.RecreateDdl(text), PreviewSide.Target, ct).ConfigureAwait(false));
            }

            return list;
        }
    }
}
