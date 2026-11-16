using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Text;

namespace Oranda.Core.Transfer.Copy;

public partial class CopyPrecheckService
{
    private sealed partial class Run
    {
        // ---- 건수·문자열 날짜 ----

        private async Task CountAsync(TableRule rule, CancellationToken ct)
        {
            var key = Key(rule);
            progress?.Report($"{key}: 건수 세는 중");
            var count = Build(key, () => _builder.SourceCount(key));
            if (count is null)
            {
                return;
            }

            try
            {
                var rows = await CopyCatalogQueries.QueryAsync(bulk, Source, count.Sql, count.Binds, ct).ConfigureAwait(false);
                _estimated[key] = sourceParts.Codec.ToInt64(rows.FirstOrDefault()?[0]) ?? 0;
            }
            catch (Exception ex) when (CopyErrors.IsNoAccess(errors, ex))
            {
                _items.Add(new CheckItem(key, "SourceNoSelect", CheckLevel.Error, $"{key}: 원본 테이블(또는 부모 키 규칙의 부모 테이블)를 읽을 권한이 없습니다."));
            }
            catch (Exception ex) when (CopyErrors.IsServer(errors, ex))
            {
                AddInvalid(key, $"원본 건수를 세지 못했습니다: {ex.Message}");
            }
        }

        // 기간 범위 안의 행 가운데 길이가 형식과 다른 문자열 날짜를 센다. 기간 열마다 그 열의 범위만으로 센다(두 열 OR 조건과 섞지 않는다).
        private async Task CheckStringDatesAsync(TableRule rule, CancellationToken ct)
        {
            if (rule.Kind != RuleKind.Period)
            {
                return;
            }

            var key = Key(rule);
            foreach (var pc in rule.PeriodColumns.Where(p => p.StringFormat is not null))
            {
                var format = pc.StringFormat!;
                if (!DateStringFormat.IsValid(format))
                {
                    AddInvalid(key, $"{pc.Column}: 지원하지 않는 날짜 형식입니다({format}).");
                    continue;
                }

                var length = DateStringFormat.Format(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), format).Length;
                var single = new CopySqlBuilder(
                    job with { Tables = [.. job.Tables.Select(r => ReferenceEquals(r, rule) ? rule with { PeriodColumns = [pc] } : r)] },
                    period, _sourceTables, _targetTables);
                var select = Build(key, () => single.SourceSelect(key, [pc.Column]));
                if (select is null)
                {
                    continue;
                }

                var binds = new Dictionary<string, BindValue>(select.Binds, StringComparer.Ordinal)
                {
                    ["LEN_CHK"] = new BindValue(BindDataType.Number, (decimal)length),
                };
                var sql = $"SELECT COUNT(*) FROM ({select.Sql}) WHERE LENGTH({SqlIdentifier.QuoteAlways(pc.Column)}) <> :LEN_CHK";
                try
                {
                    var rows = await CopyCatalogQueries.QueryAsync(bulk, Source, sql, binds, ct).ConfigureAwait(false);
                    var wrong = sourceParts.Codec.ToInt64(rows.FirstOrDefault()?[0]) ?? 0;
                    if (wrong > 0)
                    {
                        _items.Add(new CheckItem(key, "WrongLengthDates", CheckLevel.Confirm,
                            $"{key}.{pc.Column}: 기간 범위 안에 길이가 형식({format}, {Number(length)}자)과 다른 값이 {Number(wrong)}행 있습니다. 범위에서 빠지거나 겹칠 수 있습니다.")
                        {
                            Column = pc.Column,
                        });
                    }
                }
                catch (Exception ex) when (CopyErrors.IsServer(errors, ex))
                {
                    AddInvalid(key, $"{pc.Column}: 문자열 날짜 길이를 세지 못했습니다: {ex.Message}");
                }
            }
        }

        // ---- 부모 키 규칙·순서 ----

        private async Task CheckParentsAsync(List<TableRule> active, CancellationToken ct)
        {
            foreach (var rule in active.Where(r => r.Kind == RuleKind.ParentKey))
            {
                var key = Key(rule);
                if (rule.Parent is null)
                {
                    AddInvalid(key, "부모 키 규칙에 부모 연결이 없습니다.");
                    continue;
                }

                // 부모 표를 이번에 새로 만들면(비어 있다) 대상에 있는 이 표의 범위 지우기가 아무것도 지우지 못해 행이 겹친다.
                if (!_creating.Contains(key) && rule.Action == TargetAction.DeleteRangeInsert && _creating.Contains(CopyOwners.ParentKey(job, rule)))
                {
                    _items.Add(new CheckItem(key, "ParentWillBeCreated", CheckLevel.Error,
                        $"{key}: 부모 테이블 {rule.Parent.ParentTable} 를 이번에 새로 만들어 비어 있어서, 부모 범위로 대상을 지우는 이 규칙은 아무것도 지우지 못합니다. 대상 처리를 바꾸거나 부모의 「없으면 만들기」를 끄세요."));
                }

                var parent = CopyOwners.Parent(job, rule);
                if (parent is not null && parent.Kind != RuleKind.Exclude)
                {
                    continue; // 부모 규칙이 자기 표의 권한을 본다.
                }

                var sourceParent = $"{CopyOwners.ParentSource(job, rule)}.{rule.Parent.ParentTable}";
                var targetParent = $"{CopyOwners.ParentTarget(job, rule)}.{rule.Parent.ParentTable}";
                _items.Add(new CheckItem(key, "ParentExcluded", CheckLevel.Confirm,
                    $"{key}: 부모 테이블 {sourceParent} 가 {(parent is null ? "작업에 없어" : "제외되어 있어")} 부모 전체를 기준으로 연결합니다(대상 쪽은 {targetParent})."));

                // 작업 밖 부모는 다른 규칙이 권한을 보지 않는다: 원본 조회·대상 범위 지우기·건수 비교가 부모 표를 읽으므로 양쪽 SELECT 를 본다.
                var sourceHeld = await CopyCatalogQueries.ReadObjectPrivilegesAsync(bulk, sourceParts.Catalog, sourceParts.Codec, Source, _sourceAccess,
                    CopyOwners.ParentSource(job, rule), rule.Parent.ParentTable, "TABLE", sourceParts.PrecheckDialect.SupportsReadPrivilege, ct).ConfigureAwait(false);
                if (!sourceHeld.Contains("SELECT"))
                {
                    _items.Add(new CheckItem(key, "SourceNoSelect", CheckLevel.Error,
                        $"{key}: 부모 키 규칙이 읽는 원본 부모 테이블 {sourceParent} 에 SELECT 권한이 없습니다."));
                }

                var targetHeld = await CopyCatalogQueries.ReadObjectPrivilegesAsync(bulk, catalog, codec, Target, _targetAccess,
                    CopyOwners.ParentTarget(job, rule), rule.Parent.ParentTable, "TABLE", dialect.SupportsReadPrivilege, ct).ConfigureAwait(false);
                if (!targetHeld.Contains("SELECT"))
                {
                    _items.Add(new CheckItem(key, "TargetNoWrite", CheckLevel.Error,
                        $"{key}: 부모 키 규칙이 대상 범위를 정할 때 읽는 대상 부모 테이블 {targetParent} 에 SELECT 권한이 없습니다."));
                }
            }
        }

        // 부모 → 자식. 간선은 원본·대상 FK 중 작업 안 표끼리와 부모 키 규칙이다. 제외 규칙 표와 점검에 실패한 표는 넣지 않는다.
        private IReadOnlyList<CopyTablePlan> BuildOrder(List<CopyTablePlan> plans)
        {
            var names = plans.Select(p => Key(p.Rule)).ToList();
            // FK 의 부모(계정, 표) → 규칙 키. 원본 FK 는 원본 계정·표로, 대상 FK 는 대상 계정·표로 찾는다.
            var bySource = plans.GroupBy(p => CopyOwners.QualifiedKey(SourceOwner(p.Rule), p.Rule.Table), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => Key(g.First().Rule), StringComparer.OrdinalIgnoreCase);
            var byTarget = plans.GroupBy(p => CopyOwners.QualifiedKey(TargetOwner(p.Rule), p.Rule.Table), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => Key(g.First().Rule), StringComparer.OrdinalIgnoreCase);
            var edges = new List<(string Child, string Parent)>();
            foreach (var plan in plans)
            {
                var key = Key(plan.Rule);
                edges.AddRange(ForeignKeyEdges(key, plan.Source, bySource));
                edges.AddRange(ForeignKeyEdges(key, plan.Target, byTarget));
                if (plan.Rule.Kind == RuleKind.ParentKey && plan.Rule.Parent is not null)
                {
                    edges.Add((key, CopyOwners.ParentKey(job, plan.Rule)));
                }
            }

            var sorted = CopyOrder.Sort(names, edges);
            _stages = CopyOrder.Stages(names, edges); // 동시 복사(PF2)의 FK 단계: 같은 간선
            foreach (var cycle in sorted.Cycles)
            {
                _items.Add(new CheckItem(null, "FkCycle", CheckLevel.Confirm,
                    $"테이블 {string.Join(", ", cycle)} 가 외래키로 서로 이어져 있어 부모 → 자식 순서를 정할 수 없습니다. 적재 순서에 따라 실패하는 행이 생길 수 있습니다."));
            }

            var byName = plans.ToDictionary(p => Key(p.Rule), StringComparer.Ordinal);
            return [.. sorted.ParentsFirst.Select(n => byName[n])];
        }

        private static IEnumerable<(string Child, string Parent)> ForeignKeyEdges(string key, TableInfo info, Dictionary<string, string> parents)
            => info.Constraints
                .Where(c => c.Kind == ConstraintKind.ForeignKey && c.ReferencedTable is not null)
                .Select(c => parents.TryGetValue(CopyOwners.QualifiedKey(c.ReferencedTable!.Owner, c.ReferencedTable.Name), out var parent) ? parent : null)
                .OfType<string>()
                .Select(parent => (key, parent));
    }
}
