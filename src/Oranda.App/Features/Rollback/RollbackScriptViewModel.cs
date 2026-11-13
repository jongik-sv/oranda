using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Shell;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Script;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Rollback;

/// <summary>
/// 원복 스크립트 탭(되돌리기 지점 설계 11.3, 화면 2). 묶음의 원복 스크립트를 읽기 전용으로 보이고, 자동으로 실행하지 않는다.
/// 도구 모음: 「원복 실행 (커밋 안 함)」, 「일반 SQL 탭으로 복사」, 「파일로 내보내기…」. 고치려면 일반 SQL 탭으로 복사한다.
/// </summary>
[OrandaToolPanel(PanelId, ToolPanelKind.Document, DockLocation.Document, AllowMultiple = true)]
public sealed partial class RollbackScriptViewModel : ToolPanelViewModelBase
{
    public const string PanelId = "oranda.panel.rollbackScript";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    private bool _isRunning;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private bool _messageIsWarning;

    [ObservableProperty]
    private string _progressText = string.Empty;

    private CancellationTokenSource? _runCts;

    /// <summary>디자이너·패널 등록용(빈 탭).</summary>
    public RollbackScriptViewModel()
    {
        Group = new RollbackGroup { ProfileId = Guid.Empty, ProfileName = string.Empty, DbKey = string.Empty };
        Document = new RollbackScriptDocument(RollbackScriptDocument.CurrentSchemaVersion, 0, []);
        Text = string.Empty;
    }

    public RollbackScriptViewModel(RollbackGroup group, RollbackScriptDocument document, int retentionDays = RollbackSettings.DefaultRetentionDays)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(document);
        Group = group;
        Document = document;
        RetentionDays = retentionDays;
        Text = TextBuilder(group, document);
    }

    /// <summary>스크립트 글 만들기(설계 6.7, Core 작성기). 열 때마다 원장 문서에서 만든다.</summary>
    private static string TextBuilder(RollbackGroup group, RollbackScriptDocument document) => RollbackScriptWriter.Write(group, document);

    public RollbackGroup Group { get; }

    public RollbackScriptDocument Document { get; }

    /// <summary>탭에 보이는 원복 스크립트(읽기 전용).</summary>
    public string Text { get; }

    public int RetentionDays { get; } = RollbackSettings.DefaultRetentionDays;

    /// <summary>탭 제목: 「원복 스크립트 #318 · prod-example」.</summary>
    public string Title => $"원복 스크립트 #{Group.Id.ToString(CultureInfo.InvariantCulture)} · {Group.ProfileName}";

    /// <summary>실행할 원복 문장 수(무효 문장 빼고).</summary>
    public int StepCount => RollbackRunner.Order(Document, Group).Count;

    /// <summary>상태 줄: 「원복 문장 16개 · 암호화 보관 · 2026-12-12 에 지움 · 읽기 전용」.</summary>
    public string StatusLine
    {
        get
        {
            var expires = Group.ExpiresAt ?? (Group.EndedAt ?? Group.StartedAt).AddDays(RetentionDays);
            var state = Group.State == RollbackScriptState.Unverified ? " · 확인 필요" : string.Empty;
            return $"원복 문장 {StepCount.ToString("N0", CultureInfo.CurrentCulture)}개 · 암호화 보관 · {expires.ToLocalTime():yyyy-MM-dd} 에 지움 · 읽기 전용{state}";
        }
    }

    public bool CanRun => !IsRunning && StepCount > 0;

    /// <summary>원복 실행 결과 가운데 살펴볼 줄(0행·충돌·오류). 되돌린 줄은 요약 숫자로만 보인다.</summary>
    public ObservableCollection<RollbackStepResult> Results { get; } = [];

    /// <summary>실행 중인 원복을 멈춘다(지금 문장이 끝난 뒤). 이미 실행한 문장은 커밋하지 않은 채 남는다.</summary>
    public void Cancel() => _runCts?.Cancel();

    /// <summary>세션의 실행 상태(실행 중 표시·커밋 단추). 셸은 이벤트 버스의 상태를 쓴다.</summary>
    internal Func<Oranda.App.Features.Execution.ExecutionState?> RunState { get; set; } = () => null;

    /// <summary>묶음의 접속에 열린 작업 세션을 찾는다. 셸은 세션 관리자에서 찾는다. null = 열려 있지 않음.</summary>
    internal Func<Guid, ISession?> SessionFinder { get; set; } = _ => null;

    internal Func<IExecutionService?> Execution { get; set; } = () => null;

    internal Func<IRollbackLedger?> Ledger { get; set; } = () => null;

    internal IRollbackScriptDialogs Dialogs { get; set; } = RollbackScriptDialogs.Current;

    /// <summary>새 SQL 편집기 탭을 연다(글, 접속 프로필). 셸은 SQL 편집기 호스트를 쓴다.</summary>
    internal Func<string, Guid?, bool> EditorOpener { get; set; } = OpenInNewEditor;

    /// <summary>1행을 바꾼 원복 문장들 → 반대 문장 묶음(RD-8, Core <see cref="RollbackReverse.ReverseAll"/>).</summary>
    private static IReadOnlyList<RollbackStep> ReverseAll(IReadOnlyList<RollbackStep> steps) => RollbackReverse.ReverseAll(steps, out _);

    /// <summary>셸 서비스로 실행 수단을 잇는다.</summary>
    internal void UseServices(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // 그 접속의 주 작업 세션(사용자가 커밋·롤백할 그 세션). 같은 DB 에 더 연 다른 작업 공간의 세션으로 보내지 않는다.
        SessionFinder = profileId => Oranda.App.Features.ConnectionManager.SessionRouter.ForProfile(services, profileId) is { State: not (SessionState.Broken or SessionState.Closed) } s ? s : null;
        RunState = () => Oranda.App.Features.Execution.ExecutionState.For(services.GetService(typeof(Oranda.Core.Events.IEventBus)) as Oranda.Core.Events.IEventBus);
        Execution = () => services.GetService(typeof(IExecutionService)) as IExecutionService;
        Ledger = () => services.GetService(typeof(IRollbackLedger)) as IRollbackLedger;
    }

    /// <summary>
    /// 「원복 실행 (커밋 안 함)」(설계 11.3): 운영 접속이면 확인 창 하나를 띄운 뒤 모든 문장을 미리 확인한 문장으로 보낸다.
    /// 문장마다 영향 행 수가 1인지 보고, 끝나면 요약(되돌림·0행·충돌·오류)을 보인다. 커밋·롤백은 사용자가 한다.
    /// </summary>
    public async Task<RollbackRunSummary?> RunAsync(CancellationToken ct = default)
    {
        if (!CanRun)
        {
            return null;
        }

        if (SessionFinder(Group.ProfileId) is not { } session)
        {
            Tell($"접속 「{Group.ProfileName}」 이(가) 열려 있지 않습니다. 먼저 접속한 뒤 다시 실행하세요.", warning: true);
            return null;
        }

        if (Execution() is not { } execution)
        {
            Tell("실행 엔진을 찾지 못했습니다.", warning: true);
            return null;
        }

        if (Group.State == RollbackScriptState.Open)
        {
            Tell("이 묶음은 아직 커밋하거나 롤백하지 않았습니다. 트랜잭션이 끝난 뒤 실행하세요.", warning: true);
            return null;
        }

        if (!string.IsNullOrEmpty(Group.DbKey) && !string.Equals(session.DbKey, Group.DbKey, StringComparison.OrdinalIgnoreCase))
        {
            Tell($"접속 「{Group.ProfileName}」 이(가) 지금 다른 DB({session.DbKey})를 가리킵니다. 원복 스크립트를 만든 DB({Group.DbKey})가 아니라 실행하지 않습니다.", warning: true);
            return null;
        }

        var state = RunState();
        if (state?.IsBusy(session.Id) == true)
        {
            Tell("그 세션은 지금 실행 중입니다. 실행이 끝난 뒤 다시 실행하세요.", warning: true);
            return null;
        }

        if (ConfirmText(session) is { } message && !await Dialogs.ConfirmAsync("원복 실행", message, "실행", ct).ConfigureAwait(true))
        {
            return null;
        }

        IsRunning = true;
        Results.Clear();
        ProgressText = string.Empty;
        Tell("원복 문장을 실행하는 중…", warning: false);
        using var cts = _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        state?.BeginRun(session.Id, null); // 원복을 실행하는 동안 그 세션은 실행 중(커밋 단추·다른 실행이 끼어들지 않게)
        try
        {
            var total = StepCount;
            var done = 0;
            var progress = new Progress<RollbackStepResult>(r =>
            {
                done++;
                ProgressText = $"{done.ToString("N0", CultureInfo.CurrentCulture)} / {total.ToString("N0", CultureInfo.CurrentCulture)}";
                if (r.Outcome != RollbackStepOutcome.Reverted)
                {
                    Results.Add(r);
                }
            });
            var runner = new RollbackRunner(execution, Ledger()) { ReverseAll = ReverseAll };
            var summary = await Task.Run(() => runner.RunAsync(session, Group, Document, progress, cts.Token), CancellationToken.None).ConfigureAwait(true);
            Results.Clear();
            foreach (var r in summary.Steps.Where(r => r.Outcome != RollbackStepOutcome.Reverted))
            {
                Results.Add(r);
            }

            Tell(summary.Text, warning: summary.ZeroRows + summary.Conflicts + summary.Errors > 0 || summary.Cancelled);
            return summary;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Tell("원복 실행 중 오류: " + ex.Message, warning: true);
            return null;
        }
        finally
        {
            _runCts = null;
            state?.ApplyTransaction(session.Id, session.HasPendingTransaction);
            state?.EndRun(session.Id);
            IsRunning = false;
        }
    }

    /// <summary>
    /// 실행 전 확인 글. 운영 접속, 「불완전」·「확인 필요」 묶음, 세션에 커밋하지 않은 변경이 이미 있으면 묻는다. 묻지 않아도 되면 null.
    /// </summary>
    internal string? ConfirmText(ISession session)
    {
        var production = session.Profile.Environment == ConnectionEnvironment.Production;
        var notes = new List<string>();
        if (Group.State == RollbackScriptState.Unverified)
        {
            notes.Add("이 묶음은 트랜잭션이 어떻게 끝났는지 모릅니다(확인 필요). 데이터를 먼저 확인하세요.");
        }

        if (Group.State == RollbackScriptState.Incomplete)
        {
            notes.Add("이 원복 스크립트는 불완전합니다" + (string.IsNullOrWhiteSpace(Group.StateReason) ? "." : $"({RollbackScriptWriter.OneLine(Group.StateReason)}).") + " 일부 행은 되돌리지 않습니다.");
        }

        if (session.HasPendingTransaction)
        {
            notes.Add("이 세션에는 커밋하지 않은 변경이 이미 있습니다. 원복 문장이 같은 트랜잭션에 섞입니다.");
        }

        if (!production && notes.Count == 0)
        {
            return null;
        }

        var env = production ? "운영" : session.Profile.Environment == ConnectionEnvironment.Test ? "테스트" : "개발";
        var head = $"{session.Profile.Name}({env})에서 원복 문장 {StepCount.ToString("N0", CultureInfo.CurrentCulture)}개를 실행합니다. 커밋하지 않습니다.";
        return notes.Count == 0 ? head : head + "\n" + string.Join("\n", notes);
    }

    /// <summary>「일반 SQL 탭으로 복사」: 고칠 수 있는 새 SQL 탭에 같은 글을 넣는다(묶음의 접속으로). 실행하지 않는다.</summary>
    public bool CopyToEditor()
    {
        var ok = EditorOpener(Text, Group.ProfileId == Guid.Empty ? null : Group.ProfileId);
        Tell(ok ? "일반 SQL 탭에 복사했습니다(자동으로 실행하지 않습니다)." : "편집기를 열지 못했습니다.", warning: !ok);
        return ok;
    }

    /// <summary>「파일로 내보내기…」: 평문 .sql 이라 먼저 묻는다.</summary>
    public async Task<bool> ExportAsync(CancellationToken ct = default)
    {
        if (!await Dialogs.ConfirmAsync("파일로 내보내기", "이 파일에는 실제 데이터가 평문으로 들어갑니다. 내보낼까요?", "내보내기", ct).ConfigureAwait(true))
        {
            return false;
        }

        if (await Dialogs.PickExportPathAsync($"rollback-{Group.Id.ToString(CultureInfo.InvariantCulture)}.sql", ct).ConfigureAwait(true) is not { } path)
        {
            return false;
        }

        try
        {
            // SQL*Plus·SQL Developer 에서 실행해도 값 안의 & 가 치환 변수로 바뀌지 않게 첫 줄에 SET DEFINE OFF 를 둔다(앱 안의 탭에는 넣지 않는다).
            await File.WriteAllTextAsync(path, "SET DEFINE OFF\n" + Text, new UTF8Encoding(false), ct).ConfigureAwait(true);
            Tell("내보냈습니다: " + path, warning: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Tell("내보내지 못했습니다: " + ex.Message, warning: true);
            return false;
        }
    }

    private void Tell(string text, bool warning)
    {
        Message = text;
        MessageIsWarning = warning;
    }

    private static bool OpenInNewEditor(string text, Guid? profileId)
    {
        var tab = Oranda.App.Features.SqlEditor.SqlEditorHost.Current.NewDocument(text);
        tab.ConnectionProfileId = profileId;
        return true;
    }
}
