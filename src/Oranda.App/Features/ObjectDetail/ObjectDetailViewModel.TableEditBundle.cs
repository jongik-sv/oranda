using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.TableEdit;
using Oranda.Core.ChangeBundle;
using Oranda.Core.Schema.TableEdit;

namespace Oranda.App.Features.ObjectDetail;

/// <summary>
/// 「묶음에 넣기 ▾」(테이블 편집기 설계 7.7·9.3): 실행하지 않고 지금 편집의 문장을 「지금 묶음」(또는 새 묶음)에 넣는다. 넣기 전에 실행 전 검사를 돌려
/// ✖ 가 있으면 막는다(설계 10장 — 「실행」·「묶음에 넣기」 앞). 넣기 판정(DBMS·대상 소유자·해시 중복)은 <see cref="IChangeBundleService.AddAsync"/> 가 한다.
/// 「다른 묶음」 고르기는 묶음 목록을 주는 서비스 멤버가 없어 묶음 창(te-bundle T9)에서 「지금 묶음」을 바꿔 쓰게 둔다.
/// </summary>
public sealed partial class ObjectDetailViewModel
{
    /// <summary>시험용: 반영 묶음 서비스(기본 = 서비스의 <see cref="IChangeBundleService"/>).</summary>
    internal IChangeBundleService? ChangeBundles { get; set; }

    /// <summary>시험용: 새 묶음 이름 묻기(기본 = 편집기 대화상자의 입력 창). null = 취소.</summary>
    internal Func<string, CancellationToken, Task<string?>>? PromptBundleName { get; set; }

    private IChangeBundleService? ResolveBundles() => ChangeBundles ?? _services?.GetService(typeof(IChangeBundleService)) as IChangeBundleService;

    /// <summary>「묶음에 넣기 ▾」가 보인다(편집 중이고 반영 묶음 기능이 있다).</summary>
    public bool CanAddToBundle => TableEdit is not null && ResolveBundles() is not null;

    /// <summary>「묶음에 넣기 ▾」를 누를 수 있다(문장이 있고 실행 중이 아님). 파티션 바꾸기를 다시 만들기 스크립트로만 하는 상태면 막는다(검토 뒤 직접 실행할 글이라).</summary>
    public bool CanAddToBundleNow => CanAddToBundle && HasTableEditStatements && !NeedsTableEditRebuild && !IsTableEditRunning;

    /// <summary>
    /// 지금 편집의 문장을 묶음에 넣는다. <paramref name="newBundle"/> 이면 이름을 물어 새 묶음을 만들고 「지금 묶음」으로 둔 뒤 넣는다.
    /// 검사에 ✖ 가 있으면 넣지 않고 아래 줄에 보인다. 결과 한 줄(상태 줄에도 보임)을 돌려준다.
    /// </summary>
    public async Task<ChangeBundleAddResult?> AddTableEditToBundleAsync(bool newBundle, CancellationToken ct = default)
    {
        TableEdit?.FlushPendingInput?.Invoke();
        if (TableEdit is not { } edit || Target is not { } target || _session is not { } session || ResolveBundles() is not { } bundles || !CanAddToBundleNow)
        {
            return null;
        }

        IsTableEditRunning = true;
        try
        {
            var script = edit.Script;
            var runner = new TableEditRunner(null)
            {
                Checks = TableEditChecks ?? DbmsServices.For<ITableEditChecks>(_services, session),
                CheckTimeout = TableEditCheckTimeout,
            };
            var findings = await runner.CheckAsync(session, target, edit, ct);
            TableEditFindings = findings;
            if (findings.Any(f => f.Severity == CheckSeverity.Block))
            {
                StatusText = TableEditText.T("실행 전 검사에서 ✖ 가 있어 묶음에 넣지 않았다", "Not added: the pre-run check found blocking problems");
                return null;
            }

            if (!ReferenceEquals(script, edit.Script))
            {
                StatusText = TableEditText.T("검사하는 사이 편집이 바뀌어 넣지 않았다. 다시 넣으세요", "The edit changed while checking; not added. Try again");
                return null;
            }

            var origin = bundles.OriginOf(session.Id) ?? ChangeBundleOrigin.From(session);
            if (newBundle)
            {
                var prompt = PromptBundleName ?? ((initial, token) => SqlEditor.SqlEditorHost.Current.Dialogs.PromptAsync(
                    TableEditText.T("새 반영 묶음 이름", "New change bundle name"), initial, token));
                var name = await prompt($"{target.Name} {DateTime.Now:MM/dd}", ct);
                if (string.IsNullOrWhiteSpace(name))
                {
                    StatusText = TableEditText.T("묶음에 넣지 않았다", "Not added");
                    return null;
                }

                if (await bundles.CreateAsync(name.Trim(), origin, makeCurrent: true, ct) is null)
                {
                    StatusText = TableEditText.T("새 묶음을 만들지 못했다", "Could not create the bundle");
                    return null;
                }
            }

            var drafts = script.Statements
                .Select(s => new ChangeBundleDraft(
                    s.Statement.Text,
                    s.Marks.HasFlag(TableEditMarks.ServerName) ? TableEditText.T("서버가 지은 이름 — 다른 DB 에서 실패", "server-generated name — fails elsewhere") : null,
                    edit.Draft.Owner))
                .ToList();
            var result = await bundles.AddAsync(origin, drafts, ChangeBundleItemSource.TableEditor, ct);
            StatusText = BundleResultText(result, drafts.Count);
            return result;
        }
        catch (OperationCanceledException)
        {
            StatusText = TableEditText.T("취소했다", "Cancelled");
            return null;
        }
        finally
        {
            IsTableEditRunning = false;
        }
    }

    /// <summary>넣기 결과 한 줄.</summary>
    internal static string BundleResultText(ChangeBundleAddResult result, int count) => result.Outcome switch
    {
        ChangeBundleAddOutcome.Done => TableEditText.T(
            $"묶음에 {result.Added}문장을 넣었다" + (result.AlreadyPresent > 0 ? $"(이미 있던 {result.AlreadyPresent}문장은 뺌)" : string.Empty) + (result.Skipped > 0 ? $" · 대상 소유자가 아닌 {result.Skipped}문장은 뺌" : string.Empty),
            $"Added {result.Added} statement(s) to the bundle" + (result.AlreadyPresent > 0 ? $" ({result.AlreadyPresent} already there)" : string.Empty) + (result.Skipped > 0 ? $" · {result.Skipped} skipped (owner not a target)" : string.Empty)),
        ChangeBundleAddOutcome.AlreadyPresent => TableEditText.T($"{count}문장 모두 이미 묶음에 있다", $"All {count} statement(s) are already in the bundle"),
        ChangeBundleAddOutcome.NoCurrentBundle => TableEditText.T("「지금 묶음」이 없다. 「새 묶음에 넣기…」를 쓰세요", "There is no current bundle. Use “Add to a new bundle…”"),
        ChangeBundleAddOutcome.DbmsMismatch => TableEditText.T("이 접속의 DBMS 가 지금 묶음의 DBMS 와 달라 넣지 않았다", "Not added: this connection's DBMS differs from the bundle's"),
        ChangeBundleAddOutcome.OwnerNotTarget => TableEditText.T("이 테이블 소유자가 지금 묶음의 대상 소유자가 아니라 넣지 않았다", "Not added: this table's owner is not a target owner of the bundle"),
        _ => TableEditText.T("묶음에 쓸 수 없다", "The bundle store is not writable"),
    };
}
