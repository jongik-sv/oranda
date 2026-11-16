using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Sessions;

namespace Oranda.Core.Transfer.Copy;

public partial class CopyPrecheckService
{
    private sealed partial class Run
    {
        // ---- 미리 보기 ----

        // 원본 SELECT, 대상 DELETE(자식 → 부모), 다시 만들 표의 DROP(자식 → 부모), 끌 수 있는 트리거·FK, 시퀀스 소비 순서. 정책은 문장을 보낼 세션의 프로필로 판단한다.
        private async Task<IReadOnlyList<PreviewStatement>> BuildPreviewAsync(IReadOnlyList<CopyTablePlan> order, CancellationToken ct)
        {
            var list = new List<PreviewStatement>();
            foreach (var plan in order)
            {
                var key = Key(plan.Rule);
                // 같은 시점 읽기 + 동시(PF2b)면 실행과 같은 글(AS OF 절 + 시점 바인드)로 보인다: 미리 받은 확인은 글을 그대로 비교한다.
                var point = _readPoint is null ? null : CopyReadPoint.Preview(_readPoint);
                if (Build(key, () => _builder.SourceSelect(key, plan.Columns, point)) is { } select)
                {
                    list.Add(await PreviewAsync(Source, CopyCommonStatements.Query(select), PreviewSide.Source, ct).ConfigureAwait(false));
                }
            }

            // DELETE 는 표마다 그 대상 계정이 붙는다(다른 계정 표를 지우는지 미리 보기에서 보인다).
            foreach (var plan in order.Reverse())
            {
                var key = Key(plan.Rule);
                if (Build(key, () => _builder.TargetDelete(key)) is { } delete)
                {
                    list.Add(await PreviewAsync(Target, CopyCommonStatements.Delete(delete), PreviewSide.Target, ct).ConfigureAwait(false));
                }
            }

            // 다시 만들 표의 DROP(자식 → 부모). 개발 라벨 대상이 아니면 만들지 않는다. 화면이 대상 접속 이름을 한 번 다시 입력받으면 미리 받은 확인으로 넘긴다.
            if (CanRecreate)
            {
                foreach (var plan in order.Reverse().Where(p => IsRecreate(p.Rule)))
                {
                    list.Add(await PreviewAsync(Target, statements.DropTable(TargetOwner(plan.Rule), plan.Rule.Table), PreviewSide.Target, ct).ConfigureAwait(false));
                }
            }

            // 표보다 먼저 만드는 시퀀스(다시 만들기·없으면 만들기, 부모 → 자식, 같은 시퀀스는 한 번), 그리고 쓰는 표를 모두 지우게 되면 함께 지우는 DROP SEQUENCE(위험 명령).
            var sequences = order.Where(p => p.CreateMissing || (IsRecreate(p.Rule) && CanRecreate)).SelectMany(p => p.CreateSequences)
                .DistinctBy(p => (p.Owner, p.Name)).ToList();
            foreach (var sequence in sequences)
            {
                list.Add(await PreviewAsync(Target, CopyCommonStatements.RecreateDdl(sequence.CreateDdl), PreviewSide.Target, ct).ConfigureAwait(false));
            }

            foreach (var sequence in Enumerable.Reverse(sequences))
            {
                list.Add(await PreviewAsync(Target, CopyCommonStatements.DropCreatedSequence(sequence.Owner, sequence.Name), PreviewSide.Target, ct).ConfigureAwait(false));
            }

            // 시퀀스 보정 연결이 쓰는데 대상에 없어 적재 전에 만드는 시퀀스(SQ3). 쓰는 표가 모두 이번에 만드는 표면 그 표를 지울 때 함께 지운다(SQ1 과 같은 DROP SEQUENCE).
            var ruleSequences = order.SelectMany(p => p.RuleSequences.Select(s => (Plan: p, Sequence: s)))
                .Where(x => !sequences.Any(c => SameSequence(c, x.Sequence.Owner, x.Sequence.Name)))
                .GroupBy(x => (x.Sequence.Owner, x.Sequence.Name)).ToList();
            foreach (var group in ruleSequences)
            {
                list.Add(await PreviewAsync(Target, CopyCommonStatements.RecreateDdl(group.First().Sequence.CreateDdl), PreviewSide.Target, ct).ConfigureAwait(false));
            }

            foreach (var group in Enumerable.Reverse(ruleSequences).Where(g => g.All(x => x.Plan.CreateMissing || (IsRecreate(x.Plan.Rule) && CanRecreate))))
            {
                list.Add(await PreviewAsync(Target, CopyCommonStatements.DropCreatedSequence(group.Key.Owner, group.Key.Name), PreviewSide.Target, ct).ConfigureAwait(false));
            }

            // 대상에 없어 만들 표(부모 → 자식): 만드는 문장 전부, 그리고 적재가 실패하면 만든 표를 지우는 DROP … PURGE(자식 → 부모).
            // 지우는 문장은 위험 명령이라 실행 중에 확인을 받을 수 없다: 여기 들어 있어야 화면이 미리 받은 확인으로 넘어간다.
            var created = order.Where(p => p.CreateMissing).ToList();
            foreach (var plan in created)
            {
                var owner = TargetOwner(plan.Rule);
                // 실행기(CopyRunner.CreateMissingTablesAsync)와 같은 함수·같은 입력이라 보여 주는 문장과 보내는 문장이 같다(교차면 대상 DBMS 로 옮긴 구조).
                // 새 표는 PK·UK·인덱스를 적재 뒤에 만든다(PF4a): 그 문장은 CREATE 문장 뒤에 이어서 보인다.
                var built = CrossTableMap.Build(sourceParts.Dbms, targetDbms, plan.Source, owner, new HashSet<string>(StringComparer.Ordinal),
                    name => sourceParts.PrecheckDialect.IsServerName(name, SourceOwner(plan.Rule)), sourceParts.Codec, keysAfterLoad: true);
                foreach (var text in built.Statements.Concat(built.AfterLoad))
                {
                    list.Add(await PreviewAsync(Target, CopyCommonStatements.RecreateDdl(text), PreviewSide.Target, ct).ConfigureAwait(false));
                }
            }

            foreach (var plan in Enumerable.Reverse(created))
            {
                list.Add(await PreviewAsync(Target, statements.DropCreatedTable(TargetOwner(plan.Rule), plan.Rule.Table), PreviewSide.Target, ct).ConfigureAwait(false));
            }

            // 만드는 표에 적재 뒤 함께 만드는 객체(SQ2): 실행기가 같은 글을 보낸다.
            foreach (var plan in order.Where(p => !p.Companions.IsEmpty))
            {
                foreach (var text in plan.Companions.Statements)
                {
                    list.Add(await PreviewAsync(Target, CopyCommonStatements.RecreateDdl(text), PreviewSide.Target, ct).ConfigureAwait(false));
                }
            }

            foreach (var plan in order.Where(p => p.CanDisableTriggers))
            {
                foreach (var trigger in plan.Triggers)
                {
                    list.Add(await PreviewAsync(Target, statements.DisableTrigger(TargetOwner(plan.Rule), trigger), PreviewSide.Target, ct).ConfigureAwait(false));
                }
            }

            foreach (var fk in order.SelectMany(p => p.DisablableForeignKeys).Distinct())
            {
                list.Add(await PreviewAsync(Target, statements.DisableConstraint(fk), PreviewSide.Target, ct).ConfigureAwait(false));
            }

            foreach (var rule in job.Sequences.Where(r => order.Any(p => string.Equals(Key(p.Rule), r.Table, StringComparison.Ordinal))))
            {
                list.Add(await PreviewAsync(Target, statements.NextValues(CopyOwners.SequenceOwner(job, rule), rule.Sequence), PreviewSide.Target, ct).ConfigureAwait(false));
            }

            return list;
        }

        private async Task<PreviewStatement> PreviewAsync(ISession s, SqlStatement stmt, PreviewSide side, CancellationToken ct)
        {
            var decision = await policy.EvaluateAsync(new PolicyContext(s.Profile, s.Server, HistorySource.DataCopy), stmt, ct).ConfigureAwait(false);
            return new PreviewStatement(stmt.Text, side, decision.Kind, decision.Message, decision.RetypeText);
        }
    }
}
