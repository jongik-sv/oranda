using System.Globalization;
using Oranda.Core.Execution;
using Oranda.Core.Text;

namespace Oranda.Core.Transfer.Copy;

public partial class CopyRunner
{
    // ---- 4. 시퀀스 보정 ----

    private async Task AdvanceSequencesAsync(RunState run, bool proceedLargeGap, CancellationToken ct)
    {
        // 읽기 전용 대상은 NEXTVAL 조회도 시퀀스 상태를 바꾸므로 보정하지 않는다(조회로 분류돼 정책을 지난다).
        if (run.Target.Profile.ReadOnly)
        {
            return;
        }

        foreach (var rule in run.Job.Sequences)
        {
            // 적재를 끝내고 되돌리지 않은 표만 보정한다(실패·되돌린 표의 시퀀스는 그대로 둔다).
            if (!run.Tables.TryGetValue(rule.Table, out var loaded)
                || !loaded.LoadCompleted || loaded.WasRolledBack || loaded.Outcome == CopyOutcome.Failed || loaded.Loaded == 0)
            {
                continue;
            }

            run.Progress(rule.Table, 0, null, PhaseSequence);
            // 시퀀스는 그 계정(없으면 표의 대상 계정), 최대값은 표의 대상 계정에서 읽는다.
            var owner = CopyOwners.SequenceOwner(run.Job, rule);
            try
            {
                var next = _dialect.NextValues(owner, rule.Sequence);
                var maxQuery = CopyCommonStatements.Query($"SELECT MAX({SqlIdentifier.QuoteAlways(rule.Column)}) FROM {CopyCommonStatements.Qualified(loaded.Owner, loaded.TableName)}");
                run.LogStatement(run.Target, maxQuery.Text);
                var maxCell = await ScalarAsync(run, run.Target, maxQuery, NoBinds, ct);
                if (_codec.NumberText(maxCell) is not { } maxText)
                {
                    run.Log("→ 대상 테이블이 비어 보정하지 않습니다");
                    continue; // 대상 표가 비었다.
                }

                run.Log("→ 최대값 " + maxText);

                var info = await RowsAsync(run, run.Target, CopyCommonStatements.Query(CopyCatalogQueries.Sql(_catalog, run.Target.Server, "copy.sequence_increment")),
                    new Dictionary<string, BindValue>
                    {
                        ["OWNER"] = new(BindDataType.Varchar2, owner),
                        ["NAME"] = new(BindDataType.Varchar2, rule.Sequence),
                    }, ct);
                if (info.Count == 0 || _codec.NumberText(info[0][0]) is null)
                {
                    run.Notices.Add($"시퀀스 {rule.Sequence}: SELECT 권한이 없어 보정하지 않았습니다. 테스트 중 PK 충돌이 날 수 있습니다.");
                    continue;
                }

                long need;
                try
                {
                    var step = _codec.ToInt64Exact(info[0][0]);
                    var max = _codec.ToInt64Exact(maxCell);

                    // 다음 값은 LAST_NUMBER − 캐시 × 증가값 보다 작을 수 없다. 그것이 이미 최대값보다 크면 NEXTVAL 을 하나도 쓰지 않는다.
                    // RAC NOORDER 는 다른 인스턴스 캐시에 더 작은 값이 남을 수 있어 이 하한을 쓰지 않는다.
                    // 숫자인지 먼저 보고 long 으로는 step > 0 뒤에 바꾼다(바꾸다 넘치면 「값이 너무 큼」, 예전 순서 그대로).
                    if (!run.Target.Server.IsRac && _codec.NumberText(info[0][1]) is not null && _codec.NumberText(info[0][2]) is not null
                        && step > 0 && checked(_codec.ToInt64Exact(info[0][1]) - (_codec.ToInt64Exact(info[0][2]) * step)) > max)
                    {
                        continue;
                    }

                    var first = await ScalarAsync(run, run.Target, next, CountBind(1), ct);
                    if (_codec.NumberText(first) is null)
                    {
                        continue;
                    }

                    need = SequenceAdvance.Plan(checked(_codec.ToInt64Exact(first) + step), max, step);
                }
                catch (OverflowException)
                {
                    run.Notices.Add($"시퀀스 {rule.Sequence}: 값이 너무 커서 보정하지 않았습니다.");
                    continue;
                }

                if (need >= run.Job.Options.SequenceGapConfirm && !proceedLargeGap)
                {
                    run.Notices.Add($"시퀀스 {rule.Sequence}: 모자란 수({need.ToString(CultureInfo.InvariantCulture)})가 커서 보정을 건너뛰었습니다.");
                    continue;
                }

                if (need > 0)
                {
                    var rounds = (need + SequenceAdvance.ChunkSize - 1) / SequenceAdvance.ChunkSize;
                    run.Log(CopyLogText.Statement(run.SideOf(run.Target), next.Text)
                            + $"  [:{CopyCommonStatements.NextValuesBind} = 한 번에 최대 {SequenceAdvance.ChunkSize.ToString("N0", CultureInfo.InvariantCulture)}, {rounds.ToString("N0", CultureInfo.InvariantCulture)}회 · 모두 {need.ToString("N0", CultureInfo.InvariantCulture)}개]",
                            CopyLogKind.Statement);
                }
                else
                {
                    run.Log("→ 시퀀스가 이미 최대값 뒤에 있어 보정하지 않습니다");
                }

                for (var left = need; left > 0;)
                {
                    var chunk = Math.Min(left, SequenceAdvance.ChunkSize);
                    await foreach (var _ in _bulk.StreamRowsAsync(run.Target, next, CountBind(chunk), run.BulkOptions, ct))
                    {
                        // 행을 끝까지 읽어야 그만큼 소비된다.
                    }

                    left -= chunk;
                }

                if (need > 0)
                {
                    run.Notices.Add($"시퀀스 {rule.Sequence}: NEXTVAL {need.ToString(CultureInfo.InvariantCulture)}개를 소비해 {rule.Table}.{rule.Column} 최대값 뒤로 옮겼습니다.");
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && CopyErrors.IsServer(_errors, ex))
            {
                run.Notices.Add($"시퀀스 {rule.Sequence}: 보정하지 못했습니다({SafeError(ex.Message)}). 테스트 중 PK 충돌이 날 수 있습니다.");
            }
            catch (BulkDeniedException ex)
            {
                run.Notices.Add($"시퀀스 {rule.Sequence}: 정책이 거부해 보정하지 않았습니다({SafeError(ex.Message)}).");
            }
        }
    }

    private static Dictionary<string, BindValue> CountBind(long n) =>
        new() { [CopyCommonStatements.NextValuesBind] = new BindValue(BindDataType.Number, (decimal)n) };
}
