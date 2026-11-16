using System.Globalization;
using Oranda.Core.Text;

namespace Oranda.Core.Transfer.Copy;

public partial class CopyPrecheckService
{
    private sealed partial class Run
    {
        // ---- 시퀀스 ----

        /// <summary>
        /// 작업의 시퀀스 보정 연결을 점검하고, 대상에 없어 만들 시퀀스(SQ3, 결정 84)를 그 표 계획의 <see cref="CopyTablePlan.RuleSequences"/> 에 담은 순서를 돌려준다.
        /// </summary>
        private async Task<IReadOnlyList<CopyTablePlan>> CheckSequencesAsync(IReadOnlyList<CopyTablePlan> plans, IReadOnlyList<CopyTablePlan> order, CancellationToken ct)
        {
            var making = new Dictionary<string, List<CopySequencePlan>>(StringComparer.Ordinal);
            foreach (var rule in job.Sequences)
            {
                var plan = plans.FirstOrDefault(p => string.Equals(Key(p.Rule), rule.Table, StringComparison.Ordinal));
                if (plan is null)
                {
                    continue; // 제외 규칙이거나 점검에 실패한 표. 규칙이 아예 없는 연결은 앞의 RuleInvalid(CopyOwners.Problems)가 막았다.
                }

                progress?.Report($"시퀀스 {rule.Sequence}: 점검 중");
                if (await CheckSequenceAsync(rule, plan, making, ct).ConfigureAwait(false) is { } made)
                {
                    (making.TryGetValue(rule.Table, out var list) ? list : making[rule.Table] = []).Add(made);
                }
            }

            await SuggestSequencesAsync(plans, ct).ConfigureAwait(false);
            return making.Count == 0
                ? order
                : [.. order.Select(p => making.TryGetValue(Key(p.Rule), out var list) ? p with { RuleSequences = list } : p)];
        }

        // NEXTVAL 을 부르지 않는다(부작용, 메모 D16). LAST_NUMBER 와 원본 범위의 최대값으로 어림한다.
        // 대상에 시퀀스가 없으면 만들 계획을 돌려준다(SQ3). 그 밖에는 null.
        private async Task<CopySequencePlan?> CheckSequenceAsync(SequenceRule rule, CopyTablePlan plan, Dictionary<string, List<CopySequencePlan>> making, CancellationToken ct)
        {
            var cannot = new CheckItem(rule.Table, "SequenceGap", CheckLevel.Confirm,
                $"{rule.Table}.{rule.Column}: 시퀀스 {rule.Sequence} 를 볼 수 없어(없거나 SELECT 권한 없음) 보정하지 않습니다. 테스트 중 PK 충돌이 날 수 있습니다.")
            {
                Column = rule.Column,
            };

            var owner = CopyOwners.SequenceOwner(job, rule);
            var info = await CopyCatalogQueries.QueryAsync(bulk, Target, TargetSql("copy.sequence_info"), ct,
                ("OWN", owner), ("SEQ", rule.Sequence)).ConfigureAwait(false);
            if (info.Count == 0)
            {
                // 표를 만들 때 함께 만드는 시퀀스(SQ1)이거나 이 작업의 다른 연결이 이미 만들기로 정한 시퀀스: 만든 뒤 지금처럼 보정한다.
                if (plan.CreateSequences.Any(p => SameSequence(p, owner, rule.Sequence))
                    || making.Values.SelectMany(l => l).Any(p => SameSequence(p, owner, rule.Sequence)))
                {
                    return null;
                }

                if (await PlanRuleSequenceAsync(rule, plan, owner, ct).ConfigureAwait(false) is { } made)
                {
                    return made;
                }

                _items.Add(cannot);
                return null;
            }

            var held = await CopyCatalogQueries.ReadObjectPrivilegesAsync(bulk, catalog, codec, Target, _targetAccess, owner, rule.Sequence, "SEQUENCE", dialect.SupportsReadPrivilege, ct)
                .ConfigureAwait(false);
            if (!held.Contains("SELECT"))
            {
                _items.Add(cannot);
                return null;
            }

            var next = codec.ToInt64(info[0][0]) ?? 0;
            var increment = codec.ToInt64(info[0][1]) ?? 1;
            var count = Build(rule.Table, () => _builder.SourceCount(rule.Table));
            if (count is null || !count.Sql.StartsWith(CountPrefix, StringComparison.Ordinal))
            {
                return null;
            }

            long max;
            try
            {
                var sql = $"SELECT MAX({SqlIdentifier.QuoteAlways(rule.Column)}){count.Sql[CountPrefix.Length..]}";
                var rows = await CopyCatalogQueries.QueryAsync(bulk, Source, sql, count.Binds, ct).ConfigureAwait(false);
                max = sourceParts.Codec.ToInt64(rows.FirstOrDefault()?[0]) ?? long.MinValue;
            }
            catch (Exception ex) when (CopyErrors.IsServer(errors, ex))
            {
                _items.Add(new CheckItem(rule.Table, "SequenceGap", CheckLevel.Confirm,
                    $"{rule.Table}.{rule.Column}: 원본 최대값을 읽지 못해 시퀀스를 보정할 수 있는지 알 수 없습니다: {ex.Message}")
                {
                    Column = rule.Column,
                });
                return null;
            }

            var gap = PrecheckRules.SequenceGap(next, max, increment);
            if (gap <= 0)
            {
                return null;
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
            return null;
        }

        private static bool SameSequence(CopySequencePlan plan, string owner, string name)
            => CopyOwners.SameOwner(plan.Owner, owner) && string.Equals(plan.Name, name, StringComparison.Ordinal);

        /// <summary>
        /// 시퀀스 보정 연결의 시퀀스가 대상 계정에 없을 때 만들 계획(SQ3, 결정 84). 원본 표 계정에 같은 이름 시퀀스가 있으면 그 속성(START WITH 는 원본 다음 값,
        /// 원본 키 최댓값이 그 이상이면 최댓값 + 증가값 — 적재 뒤 보정이 NEXTVAL 을 덜 쓰게), 없거나 읽지 못하면 기본 속성(START WITH 원본 키 최댓값 + 1, INCREMENT BY 1, 캐시는 DBMS 기본).
        /// 대상이 읽기 전용이거나 CREATE SEQUENCE(다른 계정이면 CREATE ANY SEQUENCE) 권한이 없으면 null(지금처럼 SequenceGap 안내).
        /// 대상 시퀀스 목록에 이미 있으면(보이기만 하고 정보를 못 읽음) 만들지 않는다.
        /// </summary>
        private async Task<CopySequencePlan?> PlanRuleSequenceAsync(SequenceRule rule, CopyTablePlan plan, string owner, CancellationToken ct)
        {
            var canCreate = _targetAccess.IsOwner(owner)
                ? _targetAccess.Has("CREATE SEQUENCE") || _targetAccess.Has("CREATE ANY SEQUENCE")
                : _targetAccess.Has("CREATE ANY SEQUENCE");
            if (Target.Profile.ReadOnly || !canCreate)
            {
                return null;
            }

            if (await ReadSequenceNamesAsync(owner, ct).ConfigureAwait(false) is not { } names || names.Contains(rule.Sequence))
            {
                return null; // 목록을 읽지 못했거나 있다: 만들지 않는다(볼 수 없어 보정하지 않는다는 안내 그대로)
            }

            var sourceOwner = SourceOwner(plan.Rule);
            var max = await SourceKeyMaxAsync(rule, ct).ConfigureAwait(false);
            var fromSource = await ReadSourceSequenceAsync(sourceOwner, rule.Sequence, ct).ConfigureAwait(false);
            SequenceSpec spec;
            string origin;
            if (fromSource is not null)
            {
                spec = fromSource;
                if (max is { } top && CopySequenceDdl.TryNumber(spec.StartWith, out var start) && CopySequenceDdl.TryNumber(spec.Increment, out var step) && step > 0 && top >= start)
                {
                    spec = spec with { StartWith = (top + step).ToString(CultureInfo.InvariantCulture) };
                }

                origin = $"원본 시퀀스 {sourceOwner}.{rule.Sequence} 의 속성(증가·최소·최대·캐시·순환·순서)";
            }
            else
            {
                spec = SequenceSpec.Defaults(((max ?? 0) + 1).ToString(CultureInfo.InvariantCulture));
                origin = $"원본 {sourceOwner} 에도 없어 기본 속성(INCREMENT BY 1, 캐시는 DBMS 기본)";
            }

            var made = new CopySequencePlan(owner, rule.Sequence, spec) { KeyColumns = [rule.Column] };
            _items.Add(new CheckItem(rule.Table, CopyCheckCodes.SequenceCreated, CheckLevel.Confirm,
                $"{rule.Table}.{rule.Column}: 시퀀스 {owner}.{rule.Sequence} 이(가) 대상에 없어 적재 전에 {origin}으로 만들고(START WITH {spec.StartWith}) 복사 뒤 값을 맞춥니다."
                + " 만드는 문장은 미리 보기에 있습니다.")
            {
                Column = rule.Column,
                Sequence = rule.Sequence,
            });
            return made;
        }

        /// <summary>시퀀스 보정 연결 열의 원본 최댓값(작업 범위). 범위를 만들 수 없거나 읽지 못하거나 비었으면 null.</summary>
        private async Task<decimal?> SourceKeyMaxAsync(SequenceRule rule, CancellationToken ct)
        {
            var count = Build(rule.Table, () => _builder.SourceCount(rule.Table));
            if (count is null || !count.Sql.StartsWith(CountPrefix, StringComparison.Ordinal))
            {
                return null;
            }

            try
            {
                var sql = $"SELECT MAX({SqlIdentifier.QuoteAlways(rule.Column)}){count.Sql[CountPrefix.Length..]}";
                var rows = await CopyCatalogQueries.QueryAsync(bulk, Source, sql, count.Binds, ct).ConfigureAwait(false);
                return sourceParts.Codec.NumberText(rows.FirstOrDefault()?[0]) is { } text && CopySequenceDdl.TryNumber(text, out var value) ? value : null;
            }
            catch (Exception ex) when (CopyErrors.IsServer(errors, ex))
            {
                return null;
            }
        }

        // 작업에 시퀀스 규칙이 없는 숫자 키 열에 이름 규칙 후보를 알려 준다(통과 항목, 사용자가 작업에 연결하면 보정 대상이 된다).
        private async Task SuggestSequencesAsync(IReadOnlyList<CopyTablePlan> plans, CancellationToken ct)
        {
            var candidates = plans.Select(p => (Plan: p, Numeric: p.KeyColumns.Where(k => IsNumber(p.Target, k)).ToList())).Where(x => x.Numeric.Count > 0).ToList();
            if (candidates.Count == 0)
            {
                return;
            }

            // 시퀀스 이름은 표의 대상 계정마다 한 번 읽는다(후보는 그 표의 대상 계정에 있는 시퀀스).
            var namesByOwner = new Dictionary<string, List<string>>(CopyOwners.OwnerComparer);
            foreach (var owner in candidates.Select(c => TargetOwner(c.Plan.Rule)).Distinct(CopyOwners.OwnerComparer))
            {
                try
                {
                    var rows = await CopyCatalogQueries.QueryAsync(bulk, Target, TargetSql("copy.sequence_names"), ct, ("OWN", owner)).ConfigureAwait(false);
                    namesByOwner[owner] = [.. rows.Select(r => codec.ToText(r[0])).OfType<string>()];
                }
                catch (Exception ex) when (CopyErrors.IsNoAccess(errors, ex))
                {
                    namesByOwner[owner] = [];
                }
            }

            foreach (var (plan, numeric) in candidates)
            {
                var key = Key(plan.Rule);
                foreach (var guess in SequenceGuess.Candidates(plan.Rule.Table, numeric, namesByOwner[TargetOwner(plan.Rule)]))
                {
                    if (job.Sequences.Any(s => s.Table == key && s.Column == guess.Column))
                    {
                        continue;
                    }

                    _items.Add(new CheckItem(key, "SequenceGap", CheckLevel.Pass,
                        $"{key}.{guess.Column}: 시퀀스 후보 {TargetOwner(plan.Rule)}.{guess.Sequence}. 작업에 연결하면 복사 뒤 값을 맞춥니다.")
                    {
                        Column = guess.Column,
                        Sequence = guess.Sequence,
                    });
                }
            }
        }
    }
}
