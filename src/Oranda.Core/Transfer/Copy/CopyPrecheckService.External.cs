using System.Globalization;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy.ExternalLoad;

namespace Oranda.Core.Transfer.Copy;

/// <summary>사전 점검의 적재 방식 판정(외부 적재 도구, 조정 결정 88·89). 판정 규칙은 <see cref="CopyLoad"/>.</summary>
public partial class CopyPrecheckService
{
    private sealed partial class Run
    {
        /// <summary>외부 도구로 넣는 테이블이 있을 때 확인한 도구. 없으면 null.</summary>
        private ExternalToolInfo? _externalTool;

        /// <summary>
        /// 적재 방식을 외부 도구로 고른 테이블마다 실제 방식을 정한다(<see cref="CopyTablePlan.Load"/>). 도구 확인은 실행당 한 번이다.
        /// 쓸 수 있으면 확인 필요 항목(<see cref="CopyCheckCodes.ExternalLoad"/>), 못 쓰면 기본 방식으로 두고 통과 항목으로 이유(<see cref="CopyCheckCodes.ExternalLoadFallback"/>).
        /// </summary>
        private async Task<IReadOnlyList<CopyTablePlan>> CheckExternalLoadAsync(IReadOnlyList<CopyTablePlan> order, CancellationToken ct)
        {
            var wanted = order.Where(p => CopyLoad.Requested(job, p.Rule) == LoadMethod.ExternalTool).ToList();
            if (wanted.Count == 0)
            {
                return order;
            }

            var runBlocker = CopyLoad.RunBlocker(job, targetDbms, Target.Profile);
            var kind = CopyLoad.ToolFor(targetDbms);
            if (runBlocker is null && sessions.ExternalLoad is null)
            {
                runBlocker = "이 실행에는 외부 도구를 쓸 준비가 없습니다";
            }

            if (runBlocker is null && kind is { } supported && !ExternalLoadControl.Supports(supported))
            {
                runBlocker = $"이 빌드는 {CopyLoad.ToolName(supported)} 적재를 아직 지원하지 않습니다";
            }

            if (runBlocker is null && kind is { } k && sessions.ExternalLoad is { } external)
            {
                progress?.Report($"{CopyLoad.ToolName(k)} 확인");
                _externalTool = await external.Probe.CheckAsync(k, external.Paths.For(k), ct).ConfigureAwait(false);
                if (!_externalTool.Usable)
                {
                    runBlocker = $"{CopyLoad.ToolName(k)} 을(를) 쓸 수 없습니다 — {_externalTool.Problem ?? "도구를 찾지 못했습니다"}. 설정 > 드라이버의 「외부 적재 도구」에서 경로를 확인하세요";
                }
            }

            var result = new List<CopyTablePlan>(order.Count);
            foreach (var plan in order)
            {
                if (!wanted.Contains(plan))
                {
                    result.Add(plan);
                    continue;
                }

                var key = Key(plan.Rule);
                var columns = CopyLoad.Columns(plan, CopyLoad.CopyColumns(plan, sourceParts.Dbms, targetDbms), sourceParts.Dbms, targetDbms);
                var reason = runBlocker ?? CopyLoad.TableBlocker(plan, columns)
                             ?? (kind == ExternalLoaderKind.TbLoader && columns.FirstOrDefault(c => !TbLoaderControlFile.Supports(c)) is { } odd
                                 ? $"열 {odd.Name} 은(는) tbLoader 제어 파일에 식으로 적을 수 없는 이름입니다"
                                 : null);
                if (reason is not null)
                {
                    _items.Add(new CheckItem(key, CopyCheckCodes.ExternalLoadFallback, CheckLevel.Pass, $"{key}: 기본 방식으로 넣습니다 — {reason}."));
                    result.Add(plan);
                    continue;
                }

                _items.Add(new CheckItem(key, CopyCheckCodes.ExternalLoad, CheckLevel.Confirm, ExternalLoadMessage(key, plan)));
                result.Add(plan with { Load = LoadMethod.ExternalTool });
            }

            return result;
        }

        private string ExternalLoadMessage(string key, CopyTablePlan plan)
        {
            var tool = _externalTool!;
            var name = CopyLoad.ToolName(tool.Kind);
            var version = tool.Version is { } v ? " " + v.ToString(CultureInfo.InvariantCulture) : string.Empty;
            var path = CopyLoad.Fresh(plan)
                ? "이번에 만드는 테이블이라 direct path 로 넣습니다"
                : CopyLoad.Direct(plan)
                    ? "대상에 트리거가 없고 테이블 전체를 비운 뒤 넣어 direct path 로 넣습니다(넣는 동안 테이블 잠금, FK·CHECK 는 끄고 넣은 뒤 다시 켬)"
                    : plan.Rule.Action != TargetAction.DeleteAllInsert
                        ? "이미 있는 테이블에 남는 행이 있어 일반 경로(트리거·제약이 앱의 INSERT 와 같게 동작)로 넣습니다"
                        : !plan.NoTargetTriggers
                            ? "대상에 트리거가 있어(또는 확인할 수 없어) 일반 경로(트리거·제약이 앱의 INSERT 와 같게 동작)로 넣습니다"
                            : "대상에 FK·CHECK 제약이 있어 일반 경로(어긋난 행을 거절)로 넣습니다";
            var cleanup = DeletesBefore(plan)
                ? " 대상 정리(DELETE)는 도구가 잠금에 막히지 않게 적재 전에 확정합니다(되돌릴 수 없음)."
                : string.Empty;
            var retry = plan.Rule.Action == TargetAction.AppendOnly && !CopyLoad.Fresh(plan)
                ? " 도구가 실패하면 도구가 넣은 행이 남아 기본 방식으로 다시 넣지 않습니다."
                : " 도구가 실패하면 그 테이블을 정리한 뒤 기본 방식으로 다시 넣습니다.";
            return $"{key}: 외부 도구({name}{version})로 넣습니다 — {path}.{cleanup}{retry} 제어 파일은 미리 보기에 있습니다.";
        }

        private static bool DeletesBefore(CopyTablePlan plan) =>
            (plan.Rule.Action is TargetAction.DeleteAllInsert or TargetAction.DeleteRangeInsert) && !CopyLoad.Fresh(plan);

        /// <summary>외부 도구로 넣는 테이블의 제어 파일(미리 보기, 파일 위치는 실행 때 정한다). 비밀번호는 들어 있지 않다.</summary>
        private IEnumerable<PreviewStatement> ExternalLoadPreview(IReadOnlyList<CopyTablePlan> order)
        {
            if (_externalTool is not { } tool)
            {
                yield break;
            }

            foreach (var plan in order.Where(p => p.Load == LoadMethod.ExternalTool))
            {
                var columns = CopyLoad.Columns(plan, CopyLoad.CopyColumns(plan, sourceParts.Dbms, targetDbms), sourceParts.Dbms, targetDbms);
                var text = ExternalLoadControl.Preview(tool.Kind, TargetOwner(plan.Rule), plan.Rule.Table, columns, job.Options.MaxFailedRows, CopyLoad.Direct(plan));
                yield return new PreviewStatement(text, PreviewSide.Target, PolicyDecisionKind.Allow, null, null);
            }
        }
    }
}
