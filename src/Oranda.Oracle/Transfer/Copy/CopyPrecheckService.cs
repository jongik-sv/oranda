using System.Globalization;
using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Composition;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Text;
using Oranda.Core.Transfer.Copy;

namespace Oranda.Oracle.Transfer.Copy;

/// <summary>
/// 접속 간 복사 사전 점검(설계 7장). 모든 조회는 대량 실행 경로의 읽기(<see cref="IBulkExecutionService.StreamRowsAsync"/>)로 보내고
/// 하나씩 끝까지 읽은 뒤 다음 조회를 보낸다. 표 구조는 <see cref="IMetadataProvider"/> 로 읽는다(원본·대상 모두 점검 때만, 메모 D3).
/// 판정 규칙 가운데 DB 가 필요 없는 것은 <see cref="PrecheckRules"/> 에 있다.
/// </summary>
[OrandaService(typeof(ICopyPrecheckService))]
internal sealed partial class CopyPrecheckService(IBulkExecutionService bulk, IMetadataProvider metadata, IExecutionPolicy policy) : ICopyPrecheckService
{
    public async Task<PrecheckReport> RunAsync(CopySessions sessions, CopyJob job, ResolvedPeriod period,
                                               IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(period);

        // 운영 대상이면 이 오류 하나만 낸다. 다른 조회도 하지 않는다(메모 D19).
        if (!CopyTargetGuard.IsAllowedTarget(sessions.Target.Profile))
        {
            return new PrecheckReport(
                [new CheckItem(null, "TargetProduction", CheckLevel.Error,
                    $"대상 접속 '{sessions.Target.Profile.Name}' 은 운영 접속이라 복사 대상으로 쓸 수 없습니다.")],
                [], [], new Dictionary<string, long>());
        }

        // 원본과 대상이 같은 DB 의 같은 소유자면 대상 정리가 원본 행을 지운다. 이 오류 하나만 낸다.
        if (CopyRunner.IsSameDatabaseAndOwner(sessions.Source.DbKey, sessions.Source.Server, sessions.Target.DbKey, sessions.Target.Server,
                                               job.SourceOwner, job.TargetOwner))
        {
            return new PrecheckReport(
                [new CheckItem(null, "SameSourceTarget", CheckLevel.Error,
                    $"원본과 대상이 같은 DB 의 같은 소유자({job.TargetOwner})입니다. 대상 정리가 원본 행을 지울 수 있어 실행할 수 없습니다.")],
                [], [], new Dictionary<string, long>());
        }

        var report = await new Run(bulk, metadata, policy, sessions, job, period, progress).ExecuteAsync(ct).ConfigureAwait(false);

        // 「테이블 다시 만들기」는 지우고 만드는 단계(T10)가 들어오기 전까지 실행할 수 없다(일반 INSERT 로 처리되어 중복·PK 충돌이 난다).
        var recreate = job.Tables.Where(r => r.Kind != RuleKind.Exclude && r.Action == TargetAction.Recreate)
            .Select(r => new CheckItem(r.Table, "RuleInvalid", CheckLevel.Error, "「테이블 다시 만들기」 대상 처리는 아직 실행할 수 없습니다."))
            .ToList();
        return recreate.Count == 0 ? report : report with { Items = [.. report.Items, .. recreate] };
    }

    /// <summary>한 번의 점검이 쓰는 상태. 점검 항목을 순서대로 쌓는다.</summary>
    private sealed class Run(IBulkExecutionService bulk, IMetadataProvider metadata, IExecutionPolicy policy,
                             CopySessions sessions, CopyJob job, ResolvedPeriod period, IProgress<string>? progress)
    {
        /// <summary>읽기 시간 어림에 쓰는 초당 행 수(보수적으로 작게 잡는다).</summary>
        private const double AssumedRowsPerSecond = 2000;

        private readonly List<CheckItem> _items = [];
        private readonly Dictionary<string, long> _estimated = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TableInfo> _sourceTables = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TableInfo> _targetTables = new(StringComparer.Ordinal);
        private SessionAccess _sourceAccess = null!;
        private SessionAccess _targetAccess = null!;
        private CopySqlBuilder _builder = null!;

        private ISession Source => sessions.Source;

        private ISession Target => sessions.Target;

        public async Task<PrecheckReport> ExecuteAsync(CancellationToken ct)
        {
            _sourceAccess = await CopyCatalogQueries.ReadAccessAsync(bulk, Source, ct).ConfigureAwait(false);
            _targetAccess = await CopyCatalogQueries.ReadAccessAsync(bulk, Target, ct).ConfigureAwait(false);

            var active = job.Tables.Where(r => r.Kind != RuleKind.Exclude).ToList();
            await LoadTablesAsync(active, ct).ConfigureAwait(false);
            _builder = new CopySqlBuilder(job, period, _sourceTables, _targetTables);

            var plans = new List<CopyTablePlan>();
            foreach (var rule in active.Where(r => _sourceTables.ContainsKey(r.Table) && _targetTables.ContainsKey(r.Table)))
            {
                plans.Add(await PrepareTableAsync(rule, ct).ConfigureAwait(false));
            }

            CheckParents(active);
            _items.AddRange(PrecheckRules.CheckParentLinkMasks(job.Tables));

            var order = BuildOrder(plans);
            await CheckSequencesAsync(plans, ct).ConfigureAwait(false);
            await CheckReadConsistencyAsync(ct).ConfigureAwait(false);
            var preview = await BuildPreviewAsync(order, ct).ConfigureAwait(false);
            return new PrecheckReport(_items, order, preview, _estimated);
        }

        // ---- 표 구조 ----

        private async Task LoadTablesAsync(List<TableRule> active, CancellationToken ct)
        {
            foreach (var rule in active)
            {
                progress?.Report($"{rule.Table}: 구조 읽는 중");
                var source = await TryGetTableAsync(Source, job.SourceOwner, rule.Table, ct).ConfigureAwait(false);
                if (source is null)
                {
                    _items.Add(new CheckItem(rule.Table, "SourceNoSelect", CheckLevel.Error,
                        $"{rule.Table}: 원본에 표가 없거나 SELECT 권한이 없습니다(권한이 없으면 표가 없는 것처럼 보입니다)."));
                }

                var target = await TryGetTableAsync(Target, job.TargetOwner, rule.Table, ct).ConfigureAwait(false);
                if (target is null)
                {
                    _items.Add(new CheckItem(rule.Table, "TargetMissing", CheckLevel.Error,
                        $"{rule.Table}: 대상에 표가 없거나 접근 권한이 없습니다(권한이 없으면 표가 없는 것처럼 보입니다)."));
                }

                if (source is null || target is null)
                {
                    continue;
                }

                _sourceTables[rule.Table] = source;
                _targetTables[rule.Table] = await AdjustIdentityAsync(target, ct).ConfigureAwait(false);
            }
        }

        // 표가 안 보이면 InvalidOperationException 이다(권한이 없어도 같다).
        private async Task<TableInfo?> TryGetTableAsync(ISession s, string owner, string table, CancellationToken ct)
        {
            try
            {
                return await metadata.GetTableAsync(s, new DbObjectRef(owner, table, "TABLE"), ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        // ColumnInfo.IsIdentity 는 ALWAYS 와 BY DEFAULT 를 가리지 않는다. 값을 넣을 수 있는 BY DEFAULT 열은 거짓으로 바꾼다(메모 D12).
        private async Task<TableInfo> AdjustIdentityAsync(TableInfo target, CancellationToken ct)
        {
            if (!target.Columns.Any(c => c.IsIdentity))
            {
                return target;
            }

            HashSet<string> always;
            try
            {
                var rows = await CopyCatalogQueries.QueryAsync(bulk, Target, CopyCatalogQueries.IdentityColumns, ct,
                    ("OWN", job.TargetOwner), ("TBL", target.Table.Name)).ConfigureAwait(false);
                always = rows.Where(r => string.Equals(CopyCatalogQueries.Text(r[1]), "ALWAYS", StringComparison.OrdinalIgnoreCase))
                    .Select(r => CopyCatalogQueries.Text(r[0])).OfType<string>().ToHashSet(StringComparer.Ordinal);
            }
            catch (OracleException ex) when (CopyCatalogQueries.IsNoAccess(ex))
            {
                return target; // 구분할 수 없으면 모두 ALWAYS 로 본다(오류 쪽으로 기운다).
            }

            return target with { Columns = [.. target.Columns.Select(c => c.IsIdentity && !always.Contains(c.Name) ? c with { IsIdentity = false } : c)] };
        }

        // ---- 표 하나 ----

        private async Task<CopyTablePlan> PrepareTableAsync(TableRule rule, CancellationToken ct)
        {
            progress?.Report($"{rule.Table}: 점검 중");
            var source = _sourceTables[rule.Table];
            var target = _targetTables[rule.Table];

            _items.AddRange(PrecheckRules.CompareColumns(source, target, rule));
            var columns = PrecheckRules.SelectColumns(source, target, rule);
            var keys = PrecheckRules.KeyColumns(target, columns);
            _items.AddRange(PrecheckRules.CheckMasks(rule, source, keys));

            if (keys.Count == 0)
            {
                _items.Add(rule.Action == TargetAction.MergeByPk
                    ? new CheckItem(rule.Table, "NoPrimaryKey", CheckLevel.Error,
                        $"{rule.Table}: PK 기준 MERGE 인데 대상에 쓸 수 있는 PK 가 없습니다. 대상 처리를 바꾸세요.")
                    : new CheckItem(rule.Table, "NoPrimaryKey", CheckLevel.Confirm,
                        $"{rule.Table}: PK 없음. 다시 실행하면 행이 겹칠 수 있습니다."));
            }

            var readable = await CheckSourcePrivilegeAsync(rule, ct).ConfigureAwait(false);
            await CheckTargetPrivilegesAsync(rule, keys, ct).ConfigureAwait(false);
            AddTargetOnlyConstraints(rule, source, target);

            var (canDisableTriggers, triggers) = await CheckTriggersAsync(rule, ct).ConfigureAwait(false);
            var disablable = await CheckChildForeignKeysAsync(rule, keys, ct).ConfigureAwait(false);

            if (readable)
            {
                await CountAsync(rule, ct).ConfigureAwait(false);
                await CheckStringDatesAsync(rule, ct).ConfigureAwait(false);
            }

            return new CopyTablePlan(rule, source, target, columns, keys)
            {
                CanDisableTriggers = canDisableTriggers,
                Triggers = triggers,
                DisablableForeignKeys = disablable,
            };
        }

        private async Task<bool> CheckSourcePrivilegeAsync(TableRule rule, CancellationToken ct)
        {
            var held = await CopyCatalogQueries.ReadObjectPrivilegesAsync(bulk, Source, _sourceAccess, job.SourceOwner, rule.Table, "TABLE", ct)
                .ConfigureAwait(false);
            if (held.Contains("SELECT"))
            {
                return true;
            }

            _items.Add(new CheckItem(rule.Table, "SourceNoSelect", CheckLevel.Error, $"{rule.Table}: 원본 표에 SELECT 권한이 없습니다."));
            return false;
        }

        private async Task CheckTargetPrivilegesAsync(TableRule rule, IReadOnlyList<string> keys, CancellationToken ct)
        {
            var held = await CopyCatalogQueries.ReadObjectPrivilegesAsync(bulk, Target, _targetAccess, job.TargetOwner, rule.Table, "TABLE", ct)
                .ConfigureAwait(false);
            var missing = PrecheckRules.RequiredTargetPrivileges(rule, keys).Where(p => !held.Contains(p)).ToList();
            if (missing.Count > 0)
            {
                _items.Add(new CheckItem(rule.Table, "TargetNoWrite", CheckLevel.Error,
                    $"{rule.Table}: 대상 표에 필요한 권한이 없습니다: {string.Join(", ", missing)}."));
            }
        }

        // 대상에만 있는 CHECK·FK(켜진 것). 원본에 같은 조건·같은 열의 제약이 있으면 뺀다.
        private void AddTargetOnlyConstraints(TableRule rule, TableInfo source, TableInfo target)
        {
            var sourceChecks = source.Constraints.Where(c => c.Kind == ConstraintKind.Check && c.Condition is not null)
                .Select(c => Normalize(c.Condition!)).ToHashSet(StringComparer.Ordinal);
            var sourceForeignKeys = source.Constraints.Where(c => c.Kind == ConstraintKind.ForeignKey)
                .Select(ForeignKeySignature).ToHashSet(StringComparer.Ordinal);

            var onlyInTarget = new List<string>();
            foreach (var c in target.Constraints.Where(c => c.IsEnabled))
            {
                if (c.Kind == ConstraintKind.Check && c.Condition is not null && !sourceChecks.Contains(Normalize(c.Condition)))
                {
                    onlyInTarget.Add($"{c.Name}(CHECK)");
                }
                else if (c.Kind == ConstraintKind.ForeignKey && !sourceForeignKeys.Contains(ForeignKeySignature(c)))
                {
                    onlyInTarget.Add($"{c.Name}(FK)");
                }
            }

            if (onlyInTarget.Count > 0)
            {
                _items.Add(new CheckItem(rule.Table, "TargetOnlyConstraint", CheckLevel.Confirm,
                    $"{rule.Table}: 대상에만 있는 제약 {string.Join(", ", onlyInTarget)} 때문에 실패 행이 생길 수 있습니다."));
            }
        }

        private async Task<(bool CanDisable, IReadOnlyList<string> Triggers)> CheckTriggersAsync(TableRule rule, CancellationToken ct)
        {
            var rows = await CopyCatalogQueries.QueryAsync(bulk, Target, CopyCatalogQueries.EnabledTriggers, ct,
                ("OWN", job.TargetOwner), ("TBL", rule.Table)).ConfigureAwait(false);
            var all = rows.Select(r => (Owner: CopyCatalogQueries.Text(r[0]), Name: CopyCatalogQueries.Text(r[1]))).ToList();

            // 트리거 이름은 대상 소유자 기준이다(메모 D4). 다른 소유자의 트리거는 보여 주기만 한다.
            var owned = all.Where(t => t.Name is not null && string.Equals(t.Owner, job.TargetOwner, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Name!).ToList();
            var canDisable = _targetAccess.IsOwner(job.TargetOwner) || _targetAccess.Has("ALTER ANY TRIGGER");

            if (all.Count > 0)
            {
                var offer = canDisable && owned.Count > 0;
                _items.Add(new CheckItem(rule.Table, "TargetTriggers", CheckLevel.Confirm,
                    offer
                        ? $"{rule.Table}: 대상 트리거 {Number(all.Count)}개가 복사한 행에도 실행됩니다. 복사하는 동안 끄고 끝나면 다시 켤 수 있습니다."
                        : $"{rule.Table}: 복사한 행에도 트리거가 실행됩니다({Number(all.Count)}개).")
                {
                    Choices = offer ? [CheckChoice.DisableTriggers] : [],
                });
            }
            else if (!_targetAccess.IsOwner(job.TargetOwner))
            {
                // 소유자가 아닌 쓰기 계정은 ALL_TRIGGERS·ALL_CONSTRAINTS 에서 남의 표 트리거·참조 FK 를 못 볼 수 있다.
                _items.Add(new CheckItem(rule.Table, "TargetTriggers", CheckLevel.Pass,
                    $"{rule.Table}: 대상 계정이 소유자가 아니라 트리거·이 표를 참조하는 FK 가 보이지 않을 수 있습니다. 있으면 복사한 행에도 트리거가 실행됩니다."));
            }

            return (canDisable, owned);
        }

        // 이 표를 참조하는 대상 외래키 중 ORA-02292 를 낼 수 있는 것(메모 D15). 작업 밖 자식 표도 본다.
        private async Task<IReadOnlyList<DisabledObject>> CheckChildForeignKeysAsync(TableRule rule, IReadOnlyList<string> keys, CancellationToken ct)
        {
            if (rule.Action is not (TargetAction.DeleteAllInsert or TargetAction.DeleteRangeInsert))
            {
                return [];
            }

            var rows = await CopyCatalogQueries.QueryAsync(bulk, Target, CopyCatalogQueries.ReferencingForeignKeys, ct,
                ("OWN", job.TargetOwner), ("TBL", rule.Table)).ConfigureAwait(false);
            var blocking = rows
                .Select(r => (Owner: CopyCatalogQueries.Text(r[0]), Table: CopyCatalogQueries.Text(r[1]), Name: CopyCatalogQueries.Text(r[2])))
                .Where(fk => fk.Owner is not null && fk.Table is not null && fk.Name is not null && ChildKeepsRows(fk.Owner, fk.Table, rule.Table))
                .Select(fk => new DisabledObject(DisabledKind.ForeignKey, fk.Owner!, fk.Table!, fk.Name!))
                .ToList();
            if (blocking.Count == 0)
            {
                return [];
            }

            var canAlterAny = _targetAccess.Has("ALTER ANY TABLE");
            var disablable = blocking.Where(fk => canAlterAny || _targetAccess.IsOwner(fk.Owner)).ToList();
            var choices = new List<CheckChoice>();
            if (keys.Count > 0)
            {
                choices.Add(CheckChoice.SwitchToMerge);
            }

            if (disablable.Count > 0)
            {
                choices.Add(CheckChoice.DisableForeignKeys);
            }

            var children = string.Join(", ", blocking.Select(fk => $"{fk.Table}({fk.Name})").Distinct(StringComparer.Ordinal));
            _items.Add(new CheckItem(rule.Table, "ChildRowsMayBlock", CheckLevel.Confirm,
                $"{rule.Table}: 자식 표 {children} 의 행이 남아 있으면 지울 때 ORA-02292 가 날 수 있습니다. 대상 처리를 PK 기준 MERGE 로 바꾸세요.")
            {
                Choices = choices,
            });
            return disablable;
        }

        // 부모 행을 지워도 자식 행이 남을 수 있는가. 작업 안에서 자식이 먼저 비워지거나 부모 범위에 맞춰 지워지면 막지 않는다.
        private bool ChildKeepsRows(string childOwner, string childTable, string parentTable)
        {
            var childRule = string.Equals(childOwner, job.TargetOwner, StringComparison.OrdinalIgnoreCase)
                ? job.Tables.FirstOrDefault(r => string.Equals(r.Table, childTable, StringComparison.Ordinal))
                : null;
            if (childRule is null || childRule.Kind == RuleKind.Exclude)
            {
                return true;
            }

            return childRule.Action switch
            {
                TargetAction.DeleteAllInsert or TargetAction.Recreate => false,
                TargetAction.DeleteRangeInsert => !(childRule.Kind == RuleKind.ParentKey
                                                    && string.Equals(childRule.Parent?.ParentTable, parentTable, StringComparison.Ordinal)),
                _ => true,
            };
        }

        // ---- 건수·문자열 날짜 ----

        private async Task CountAsync(TableRule rule, CancellationToken ct)
        {
            progress?.Report($"{rule.Table}: 건수 세는 중");
            var count = Build(rule.Table, () => _builder.SourceCount(rule.Table));
            if (count is null)
            {
                return;
            }

            try
            {
                var rows = await CopyCatalogQueries.QueryAsync(bulk, Source, count.Sql, count.Binds, ct).ConfigureAwait(false);
                _estimated[rule.Table] = CopyCatalogQueries.ToLong(rows.FirstOrDefault()?[0]) ?? 0;
            }
            catch (OracleException ex) when (CopyCatalogQueries.IsNoAccess(ex))
            {
                _items.Add(new CheckItem(rule.Table, "SourceNoSelect", CheckLevel.Error, $"{rule.Table}: 원본 표를 읽을 권한이 없습니다."));
            }
            catch (OracleException ex)
            {
                AddInvalid(rule.Table, $"원본 건수를 세지 못했습니다: {ex.Message}");
            }
        }

        // 기간 범위 안의 행 가운데 길이가 형식과 다른 문자열 날짜를 센다. 기간 열마다 그 열의 범위만으로 센다(두 열 OR 조건과 섞지 않는다).
        private async Task CheckStringDatesAsync(TableRule rule, CancellationToken ct)
        {
            if (rule.Kind != RuleKind.Period)
            {
                return;
            }

            foreach (var pc in rule.PeriodColumns.Where(p => p.StringFormat is not null))
            {
                var format = pc.StringFormat!;
                if (!DateStringFormat.IsValid(format))
                {
                    AddInvalid(rule.Table, $"{pc.Column}: 지원하지 않는 날짜 형식입니다({format}).");
                    continue;
                }

                var length = DateStringFormat.Format(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), format).Length;
                var single = new CopySqlBuilder(
                    job with { Tables = [.. job.Tables.Select(r => r.Table == rule.Table ? rule with { PeriodColumns = [pc] } : r)] },
                    period, _sourceTables, _targetTables);
                var select = Build(rule.Table, () => single.SourceSelect(rule.Table, [pc.Column]));
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
                    var wrong = CopyCatalogQueries.ToLong(rows.FirstOrDefault()?[0]) ?? 0;
                    if (wrong > 0)
                    {
                        _items.Add(new CheckItem(rule.Table, "WrongLengthDates", CheckLevel.Confirm,
                            $"{rule.Table}.{pc.Column}: 기간 범위 안에 길이가 형식({format}, {Number(length)}자)과 다른 값이 {Number(wrong)}행 있습니다. 범위에서 빠지거나 겹칠 수 있습니다.")
                        {
                            Column = pc.Column,
                        });
                    }
                }
                catch (OracleException ex)
                {
                    AddInvalid(rule.Table, $"{pc.Column}: 문자열 날짜 길이를 세지 못했습니다: {ex.Message}");
                }
            }
        }

        // ---- 부모 키 규칙·순서 ----

        private void CheckParents(List<TableRule> active)
        {
            foreach (var rule in active.Where(r => r.Kind == RuleKind.ParentKey))
            {
                if (rule.Parent is null)
                {
                    AddInvalid(rule.Table, "부모 키 규칙에 부모 연결이 없습니다.");
                    continue;
                }

                var parent = job.Tables.FirstOrDefault(r => string.Equals(r.Table, rule.Parent.ParentTable, StringComparison.Ordinal));
                if (parent is null || parent.Kind == RuleKind.Exclude)
                {
                    _items.Add(new CheckItem(rule.Table, "ParentExcluded", CheckLevel.Confirm,
                        $"{rule.Table}: 부모 표 {rule.Parent.ParentTable} 가 제외되어 있어 부모 전체를 기준으로 연결합니다."));
                }
            }
        }

        // 부모 → 자식. 간선은 원본·대상 FK 중 작업 안 표끼리와 부모 키 규칙이다. 제외 규칙 표와 점검에 실패한 표는 넣지 않는다.
        private IReadOnlyList<CopyTablePlan> BuildOrder(List<CopyTablePlan> plans)
        {
            var names = plans.Select(p => p.Rule.Table).ToList();
            var inJob = names.ToHashSet(StringComparer.Ordinal);
            var edges = new List<(string Child, string Parent)>();
            foreach (var plan in plans)
            {
                edges.AddRange(ForeignKeyEdges(plan.Rule.Table, plan.Source, job.SourceOwner, inJob));
                edges.AddRange(ForeignKeyEdges(plan.Rule.Table, plan.Target, job.TargetOwner, inJob));
                if (plan.Rule.Kind == RuleKind.ParentKey && plan.Rule.Parent is { } parent)
                {
                    edges.Add((plan.Rule.Table, parent.ParentTable));
                }
            }

            var sorted = CopyOrder.Sort(names, edges);
            foreach (var cycle in sorted.Cycles)
            {
                _items.Add(new CheckItem(null, "FkCycle", CheckLevel.Confirm,
                    $"표 {string.Join(", ", cycle)} 가 외래키로 서로 이어져 있어 부모 → 자식 순서를 정할 수 없습니다. 적재 순서에 따라 실패하는 행이 생길 수 있습니다."));
            }

            var byName = plans.ToDictionary(p => p.Rule.Table, StringComparer.Ordinal);
            return [.. sorted.ParentsFirst.Select(n => byName[n])];
        }

        private static IEnumerable<(string Child, string Parent)> ForeignKeyEdges(string table, TableInfo info, string owner, HashSet<string> inJob)
            => info.Constraints
                .Where(c => c.Kind == ConstraintKind.ForeignKey && c.ReferencedTable is { } r
                            && string.Equals(r.Owner, owner, StringComparison.OrdinalIgnoreCase) && inJob.Contains(r.Name))
                .Select(c => (table, c.ReferencedTable!.Name));

        // ---- 시퀀스 ----

        private async Task CheckSequencesAsync(IReadOnlyList<CopyTablePlan> plans, CancellationToken ct)
        {
            foreach (var rule in job.Sequences)
            {
                var plan = plans.FirstOrDefault(p => string.Equals(p.Rule.Table, rule.Table, StringComparison.Ordinal));
                if (plan is null)
                {
                    continue;
                }

                progress?.Report($"시퀀스 {rule.Sequence}: 점검 중");
                await CheckSequenceAsync(rule, ct).ConfigureAwait(false);
            }

            await SuggestSequencesAsync(plans, ct).ConfigureAwait(false);
        }

        // NEXTVAL 을 부르지 않는다(부작용, 메모 D16). LAST_NUMBER 와 원본 범위의 최대값으로 어림한다.
        private async Task CheckSequenceAsync(SequenceRule rule, CancellationToken ct)
        {
            var cannot = new CheckItem(rule.Table, "SequenceGap", CheckLevel.Confirm,
                $"{rule.Table}.{rule.Column}: 시퀀스 {rule.Sequence} 를 볼 수 없어(없거나 SELECT 권한 없음) 보정하지 않습니다. 테스트 중 PK 충돌이 날 수 있습니다.")
            {
                Column = rule.Column,
            };

            var info = await CopyCatalogQueries.QueryAsync(bulk, Target, CopyCatalogQueries.SequenceInfo, ct,
                ("OWN", job.TargetOwner), ("SEQ", rule.Sequence)).ConfigureAwait(false);
            var held = await CopyCatalogQueries.ReadObjectPrivilegesAsync(bulk, Target, _targetAccess, job.TargetOwner, rule.Sequence, "SEQUENCE", ct)
                .ConfigureAwait(false);
            if (info.Count == 0 || !held.Contains("SELECT"))
            {
                _items.Add(cannot);
                return;
            }

            var next = CopyCatalogQueries.ToLong(info[0][0]) ?? 0;
            var increment = CopyCatalogQueries.ToLong(info[0][1]) ?? 1;
            var count = Build(rule.Table, () => _builder.SourceCount(rule.Table));
            if (count is null || !count.Sql.StartsWith(CountPrefix, StringComparison.Ordinal))
            {
                return;
            }

            long max;
            try
            {
                var sql = $"SELECT MAX({SqlIdentifier.QuoteAlways(rule.Column)}){count.Sql[CountPrefix.Length..]}";
                var rows = await CopyCatalogQueries.QueryAsync(bulk, Source, sql, count.Binds, ct).ConfigureAwait(false);
                max = CopyCatalogQueries.ToLong(rows.FirstOrDefault()?[0]) ?? long.MinValue;
            }
            catch (OracleException ex)
            {
                _items.Add(new CheckItem(rule.Table, "SequenceGap", CheckLevel.Confirm,
                    $"{rule.Table}.{rule.Column}: 원본 최대값을 읽지 못해 시퀀스를 보정할 수 있는지 알 수 없습니다: {ex.Message}")
                {
                    Column = rule.Column,
                });
                return;
            }

            var gap = PrecheckRules.SequenceGap(next, max, increment);
            if (gap <= 0)
            {
                return;
            }

            var large = gap >= job.Options.SequenceGapConfirm;
            _items.Add(new CheckItem(rule.Table, "SequenceGap", CheckLevel.Confirm,
                large
                    ? $"{rule.Table}.{rule.Column}: 시퀀스 {rule.Sequence} 를 {Number(gap)}번 소비해야 해서 시간이 오래 걸릴 수 있습니다. 진행할지 정하세요."
                    : $"{rule.Table}.{rule.Column}: 복사 뒤 시퀀스 {rule.Sequence} 를 약 {Number(gap)}번 소비해 값을 앞당깁니다.")
            {
                Column = rule.Column,
                Choices = large ? [CheckChoice.ProceedSequenceGap] : [],
            });
        }

        // 작업에 시퀀스 규칙이 없는 숫자 키 열에 이름 규칙 후보를 알려 준다(통과 항목, 사용자가 작업에 연결하면 보정 대상이 된다).
        private async Task SuggestSequencesAsync(IReadOnlyList<CopyTablePlan> plans, CancellationToken ct)
        {
            var candidates = plans.Select(p => (Plan: p, Numeric: p.KeyColumns.Where(k => IsNumber(p.Target, k)).ToList())).Where(x => x.Numeric.Count > 0).ToList();
            if (candidates.Count == 0)
            {
                return;
            }

            List<string> names;
            try
            {
                var rows = await CopyCatalogQueries.QueryAsync(bulk, Target, CopyCatalogQueries.SequenceNames, ct, ("OWN", job.TargetOwner)).ConfigureAwait(false);
                names = [.. rows.Select(r => CopyCatalogQueries.Text(r[0])).OfType<string>()];
            }
            catch (OracleException ex) when (CopyCatalogQueries.IsNoAccess(ex))
            {
                return;
            }

            foreach (var (plan, numeric) in candidates)
            {
                foreach (var guess in SequenceGuess.Candidates(plan.Rule.Table, numeric, names))
                {
                    if (job.Sequences.Any(s => s.Table == guess.Table && s.Column == guess.Column))
                    {
                        continue;
                    }

                    _items.Add(new CheckItem(guess.Table, "SequenceGap", CheckLevel.Pass,
                        $"{guess.Table}.{guess.Column}: 시퀀스 후보 {guess.Sequence}. 작업에 연결하면 복사 뒤 값을 맞춥니다.")
                    {
                        Column = guess.Column,
                    });
                }
            }
        }

        // ---- 읽기 일관성 ----

        private async Task CheckReadConsistencyAsync(CancellationToken ct)
        {
            if (!job.ConsistentRead)
            {
                return;
            }

            long? undo = null;
            try
            {
                var rows = await CopyCatalogQueries.QueryAsync(bulk, Source, CopyCatalogQueries.ParameterValue, ct, ("PNAME", "undo_retention")).ConfigureAwait(false);
                undo = CopyCatalogQueries.ToLong(rows.FirstOrDefault()?[0]);
            }
            catch (OracleException)
            {
                // V$PARAMETER 를 읽을 권한이 없으면 알 수 없음으로 둔다.
            }

            if (undo is not { } retention)
            {
                _items.Add(new CheckItem(null, "ReadConsistency", CheckLevel.Pass,
                    "원본 UNDO_RETENTION 을 읽을 수 없어 같은 시점 읽기가 끝까지 유지되는지는 알 수 없습니다."));
                return;
            }

            var seconds = Math.Ceiling(_estimated.Values.Sum() / AssumedRowsPerSecond);
            var tooLong = seconds > retention;
            _items.Add(new CheckItem(null, "ReadConsistency", tooLong ? CheckLevel.Confirm : CheckLevel.Pass,
                tooLong
                    ? $"예상 읽기 시간 약 {Number((long)seconds)}초가 원본 UNDO_RETENTION {Number(retention)}초보다 깁니다. ORA-01555 로 멈출 수 있으니 기간을 줄이거나 나눠 실행하세요."
                    : $"예상 읽기 시간 약 {Number((long)seconds)}초, 원본 UNDO_RETENTION {Number(retention)}초."));
        }

        // ---- 미리 보기 ----

        // 원본 SELECT, 대상 DELETE(자식 → 부모), 끌 수 있는 트리거·FK, 시퀀스 소비 순서. 정책은 문장을 보낼 세션의 프로필로 판단한다.
        private async Task<IReadOnlyList<PreviewStatement>> BuildPreviewAsync(IReadOnlyList<CopyTablePlan> order, CancellationToken ct)
        {
            var list = new List<PreviewStatement>();
            foreach (var plan in order)
            {
                if (Build(plan.Rule.Table, () => _builder.SourceSelect(plan.Rule.Table, plan.Columns)) is { } select)
                {
                    list.Add(await PreviewAsync(Source, CopyStatements.Query(select), PreviewSide.Source, ct).ConfigureAwait(false));
                }
            }

            foreach (var plan in order.Reverse())
            {
                if (Build(plan.Rule.Table, () => _builder.TargetDelete(plan.Rule.Table)) is { } delete)
                {
                    list.Add(await PreviewAsync(Target, CopyStatements.Delete(delete), PreviewSide.Target, ct).ConfigureAwait(false));
                }
            }

            foreach (var plan in order.Where(p => p.CanDisableTriggers))
            {
                foreach (var trigger in plan.Triggers)
                {
                    list.Add(await PreviewAsync(Target, CopyStatements.DisableTrigger(job.TargetOwner, trigger), PreviewSide.Target, ct).ConfigureAwait(false));
                }
            }

            foreach (var fk in order.SelectMany(p => p.DisablableForeignKeys).Distinct())
            {
                list.Add(await PreviewAsync(Target, CopyStatements.DisableConstraint(fk), PreviewSide.Target, ct).ConfigureAwait(false));
            }

            foreach (var rule in job.Sequences.Where(r => order.Any(p => string.Equals(p.Rule.Table, r.Table, StringComparison.Ordinal))))
            {
                list.Add(await PreviewAsync(Target, CopyStatements.NextValues(job.TargetOwner, rule.Sequence), PreviewSide.Target, ct).ConfigureAwait(false));
            }

            return list;
        }

        private async Task<PreviewStatement> PreviewAsync(ISession s, SqlStatement stmt, PreviewSide side, CancellationToken ct)
        {
            var decision = await policy.EvaluateAsync(new PolicyContext(s.Profile, s.Server, HistorySource.DataCopy), stmt, ct).ConfigureAwait(false);
            return new PreviewStatement(stmt.Text, side, decision.Kind, decision.Message, decision.RetypeText);
        }

        // ---- 도우미 ----

        private const string CountPrefix = "SELECT COUNT(*)";

        // 규칙이 어긋나 문장을 못 만들면 그 표만 오류로 알리고 점검은 이어 간다.
        private T? Build<T>(string table, Func<T?> make)
            where T : class
        {
            try
            {
                return make();
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                AddInvalid(table, ex.Message);
                return null;
            }
        }

        private void AddInvalid(string table, string message)
        {
            var text = $"{table}: {message}";
            if (!_items.Any(i => i.Code == "RuleInvalid" && i.Message == text))
            {
                _items.Add(new CheckItem(table, "RuleInvalid", CheckLevel.Error, text));
            }
        }

        private static bool IsNumber(TableInfo info, string column)
            => info.Columns.FirstOrDefault(c => string.Equals(c.Name, column, StringComparison.Ordinal))?.DataType
                is { } t && (t.StartsWith("NUMBER", StringComparison.OrdinalIgnoreCase) || t.StartsWith("FLOAT", StringComparison.OrdinalIgnoreCase));

        private static string Normalize(string condition) => Whitespace().Replace(condition, "").ToUpperInvariant();

        private static string ForeignKeySignature(ConstraintInfo c)
            => string.Join(',', c.Columns) + ">" + c.ReferencedTable?.Name;

        private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

        private static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Whitespace();
}
