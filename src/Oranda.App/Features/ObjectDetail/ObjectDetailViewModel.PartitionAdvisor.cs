using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Oranda.App.Features.TableEdit;
using Oranda.App.Features.TableEdit.Advisor;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Features;
using Oranda.Core.Schema.TableEdit.Partitions.Advisor;
using Oranda.Core.Settings;

namespace Oranda.App.Features.ObjectDetail;

/// <summary>
/// AI 파티션 추천 배선(설계 2026-11-18 3장): 파티션 페이지의 「✨ AI 추천…」 → 확인 창(모달) → 결과 창(비모달, 탭 하나에 하나),
/// 「분포 보기」 → AI 없이 근거만 결과 창에, 띠의 「추천 다시 보기」 → 마지막 결과 창. 받는 곳·가림·감사는 AI 튜닝과 같은 규칙(<see cref="AdvisorRoute"/>).
/// AI 를 쓸 수 없으면(설정 없음 등) 까닭과 「설정 열기」를 묻는다.
/// </summary>
public sealed partial class ObjectDetailViewModel
{
    private PartitionAdvisorResultViewModel? _adviceResult;
    private Window? _adviceWindow;

    /// <summary>시험용: AI 경로(기본 = DI 의 <see cref="IAiDriver"/> 라우터).</summary>
    internal IAiDriver? AdvisorDriver { get; set; }

    /// <summary>시험용: 근거 모으기(기본 = DI 의 <see cref="IPartitionEvidenceCollector"/>).</summary>
    internal IPartitionEvidenceCollector? EvidenceCollector { get; set; }

    /// <summary>시험용: AI 설정(기본 = 설정 서비스의 <c>ai</c> 섹션).</summary>
    internal Func<AiSettings?>? AdvisorSettings { get; set; }

    /// <summary>시험용: Core 함수 묶음(기본 = Core).</summary>
    internal AdvisorEngine? AdvisorEngineOverride { get; set; }

    /// <summary>시험용: 확인 창 띄우기(기본 = 주 창 위 모달).</summary>
    internal Func<PartitionAdvisorStartViewModel, Task>? ShowAdvisorStart { get; set; }

    /// <summary>시험용: 결과 창 띄우기(기본 = 비모달 창 하나를 이 탭이 쥔다).</summary>
    internal Action<PartitionAdvisorResultViewModel>? ShowAdvisorResult { get; set; }

    /// <summary>시험용: AI 를 쓸 수 없을 때 안내(까닭 → 「설정 열기」를 골랐나).</summary>
    internal Func<string, Task<bool>>? AdvisorUnavailableNotice { get; set; }

    /// <summary>시험용: 날짜형 열 판정 공급자(기본 = DI 의 <see cref="IPartitionEvidenceProvider"/> — DBMS 별 공급자, 없으면 지원 안 함).</summary>
    internal IPartitionEvidenceProvider? DateLikeProvider { get; set; }

    /// <summary>시험용: 편집을 켤 때 시작한 날짜형 열 판정(끝나기를 기다린다).</summary>
    internal Task DateLikeDetection { get; private set; } = Task.CompletedTask;

    private CancellationTokenSource? _dateLikeCts;

    /// <summary>날짜형 판정을 할 열 수 상한(편집을 켤 때마다 표본 SELECT 를 열마다 하나 돌린다).</summary>
    internal const int MaxDateLikeColumns = 16;

    /// <summary>마지막 결과(띠의 「추천 다시 보기」가 다시 연다).</summary>
    internal PartitionAdvisorResultViewModel? AdviceResult => _adviceResult;

    // CommunityToolkit 이 만든 한 인자 훅(두 인자 훅은 TableEdit.cs 가 쓴다): 편집 세션이 생기면 단추 동작을 넣고, 끝나면 결과 창을 닫는다.
    partial void OnTableEditChanged(TableEditSession? value)
    {
        if (value is not null)
        {
            value.OpenAiAdvisor = () => OpenPartitionAdvisorAsync();
            value.OpenPartitionEvidence = () => OpenPartitionEvidenceAsync();
            value.ReopenAiAdvice = () => ReopenPartitionAdviceAsync();
            StartDateLikeDetection(value);
            return;
        }

        _dateLikeCts?.Cancel();
        _adviceResult = null;
        _adviceWindow?.Close();
        _adviceWindow = null;
    }

    /// <summary>「✨ AI 추천…」: AI 를 쓸 수 있는지 보고, 확인 창을 띄우고, 받았으면 결과 창을 연다.</summary>
    public async Task OpenPartitionAdvisorAsync(CancellationToken ct = default)
    {
        if (TableEdit is not { ShowsAiAdvisor: true } edit || _session is not { } basis || Target is not { } target)
        {
            return;
        }

        edit.FlushPendingInput?.Invoke();
        await ReloadAdvisorSettingsAsync(ct);
        var settings = (AdvisorSettings ?? CurrentAiSettings)();
        var driver = AdvisorDriver ?? _services?.GetService(typeof(IAiDriver)) as IAiDriver;
        var gate = _services?.GetService(typeof(IFeatureGate)) as IFeatureGate;
        if (PartitionAdvisorStartViewModel.UnavailableReason(gate, driver, settings) is { } reason)
        {
            StatusText = reason;
            if (await (AdvisorUnavailableNotice ?? AskOpenSettingsAsync)(reason))
            {
                (_services?.GetService(typeof(IShellUi)) as IShellUi)?.ShowSettings(vm => vm.SelectedTab = SettingsViewModel.UpdateTabIndex + 1); // AI 모델 탭 = 업데이트 다음
            }

            return;
        }

        var route = AdvisorRoute.For(basis.Profile, settings!);
        if (route.Context.AccessLevel == AiAccessLevel.Off)
        {
            StatusText = TableEditText.T("이 접속은 AI 허용 수준이 「끔」이라 AI 추천을 보낼 수 없습니다. 「분포 보기」는 쓸 수 있습니다.",
                "AI access is off for this connection, so no advice can be sent. Evidence view still works.");
            return;
        }

        if (EvidenceCollectorFor() is not { } collector)
        {
            StatusText = TableEditText.T("근거 모으기를 쓸 수 없습니다(이 빌드에 없습니다)", "Evidence collection is not available in this build");
            return;
        }

        var session = ConnectionManager.SessionRouter.ForTool(_services, basis) ?? basis;
        var run = new PartitionAdvisorRun(new AdvisorSetup(driver!, collector, session, target, edit.Dialect, edit.Context.Server, route)
        {
            Engine = AdvisorEngineOverride ?? AdvisorEngine.Default,
            Now = () => Today is { } today ? today().ToDateTime(TimeOnly.FromDateTime(DateTime.Now)) : DateTime.Now, // 시험이 「오늘」을 바꾼다
            Audit = _services?.GetService(typeof(IAgentAuditStore)) as IAgentAuditStore,
        });
        var start = new PartitionAdvisorStartViewModel(run);
        await (ShowAdvisorStart ?? ShowAdvisorStartWindowAsync)(start);
        if (start.Outcome is not { } outcome || !ReferenceEquals(TableEdit, edit))
        {
            return;
        }

        var result = new PartitionAdvisorResultViewModel(outcome, AdviceHost(edit, run, outcome.Evidence), route.Target.Label);
        Present(result);
    }

    /// <summary>「분포 보기」(PA-9): AI 없이 근거만 모아 결과 창(근거 패널)에.</summary>
    public async Task OpenPartitionEvidenceAsync(CancellationToken ct = default)
    {
        if (TableEdit is not { ShowsAiAdvisor: true } edit || _session is not { } basis || Target is not { } target)
        {
            return;
        }

        if (EvidenceCollectorFor() is not { } collector)
        {
            StatusText = TableEditText.T("근거 모으기를 쓸 수 없습니다(이 빌드에 없습니다)", "Evidence collection is not available in this build");
            return;
        }

        var session = ConnectionManager.SessionRouter.ForTool(_services, basis) ?? basis;
        IsBusy = true;
        StatusText = TableEditText.T("근거를 모으는 중…(통계·조회 조건·분포)", "Collecting evidence… (statistics, predicates, distributions)");
        try
        {
            var evidence = await collector.CollectAsync(session, target, EvidenceOptions.Default, null, ct);
            if (!ReferenceEquals(TableEdit, edit))
            {
                return;
            }

            StatusText = string.Empty;
            Present(new PartitionAdvisorResultViewModel(evidence, new AdvisorResultHost { PickReportFile = ReportPicker() }));
        }
        catch (OperationCanceledException)
        {
            StatusText = TableEditText.T("근거 모으기를 멈췄습니다", "Collecting stopped");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusText = TableEditText.T("근거를 모으지 못했습니다: ", "Could not collect the evidence: ") + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>띠의 「추천 다시 보기」: 마지막 결과 창을 다시 연다.</summary>
    public Task ReopenPartitionAdviceAsync()
    {
        if (_adviceResult is { } result)
        {
            Present(result);
        }

        return Task.CompletedTask;
    }

    private void StartDateLikeDetection(TableEditSession edit)
    {
        _dateLikeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _dateLikeCts = cts;
        DateLikeDetection = RunDateLikeDetectionAsync(edit, cts);
    }

    private async Task RunDateLikeDetectionAsync(TableEditSession edit, CancellationTokenSource cts)
    {
        try
        {
            await DetectDateLikeColumnsAsync(edit, cts.Token);
        }
        finally
        {
            if (ReferenceEquals(_dateLikeCts, cts))
            {
                _dateLikeCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// 날짜형 문자열·숫자 열 판정(PA-10, PU4): 일반 테이블 편집을 켤 때, 날짜형일 수 있는 글·수 열(길이 6~10 글, 정밀도 6~8 정수)을 공급자의
    /// <see cref="IPartitionEvidenceProvider.DetectDateLikeAsync"/>(표본 500행)로 보고, 형식 맞는 비율이 95% 이상인 열을 쉬운 설정에 넣는다
    /// (<see cref="TableEditSession.SetDateLikeColumns"/>). 지원 안 함(null)·권한 오류·취소면 조용히 그만둔다 — 쉬운 설정이 DATE 열만 보일 뿐이다.
    /// </summary>
    internal async Task DetectDateLikeColumnsAsync(TableEditSession edit, CancellationToken ct)
    {
        if (!edit.ShowsPartitionMethodChoice || !edit.CanEditPartitions || _session is not { } basis || Target is not { } target
            || (DateLikeProvider ?? _services?.GetService(typeof(IPartitionEvidenceProvider)) as IPartitionEvidenceProvider) is not { } provider)
        {
            return;
        }

        var session = ConnectionManager.SessionRouter.ForTool(_services, basis) ?? basis;
        var found = new Dictionary<string, DateLikeFormat>(StringComparer.Ordinal);
        foreach (var column in DateLikeCandidates(edit))
        {
            try
            {
                if (await provider.DetectDateLikeAsync(session, target.Owner, target.Name, column, ct) is { } dateLike
                    && dateLike.MatchRatio >= PartitionAdviceChecker.DateLikeStopRatio)
                {
                    found[column] = dateLike.Format;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 권한·연결 오류는 다음 열도 같다: 판정을 그만두고 찾은 것만 넣는다.
                System.Diagnostics.Trace.TraceWarning("날짜형 열 판정 실패({0}): {1}", column, ex.GetType().Name);
                break;
            }
        }

        if (found.Count > 0 && ReferenceEquals(TableEdit, edit) && !ct.IsCancellationRequested)
        {
            edit.SetDateLikeColumns(found);
        }
    }

    /// <summary>날짜형일 수 있는 열: 글(CHAR·VARCHAR2 류, 길이 모름 또는 6~10)·수(NUMBER, 정밀도 모름 또는 6~8, 소수 없음). 앞에서부터 <see cref="MaxDateLikeColumns"/> 개.</summary>
    internal static IReadOnlyList<string> DateLikeCandidates(TableEditSession edit) =>
    [
        .. edit.Draft.Columns
            .Where(c => !c.IsDropped && !c.IsVirtual)
            .Where(c => Oranda.Core.Schema.TableEdit.Partitions.PartitionBounds.KindOf(c.DataType) switch
            {
                Oranda.Core.Schema.TableEdit.Partitions.PartitionKeyKind.Text => c.Length is null or (>= 6 and <= 10),
                Oranda.Core.Schema.TableEdit.Partitions.PartitionKeyKind.Number => c.Precision is null or (>= 6 and <= 8) && c.Scale is null or 0,
                _ => false,
            })
            .Select(c => c.Name)
            .Take(MaxDateLikeColumns),
    ];

    private AdvisorResultHost AdviceHost(TableEditSession edit, PartitionAdvisorRun run, PartitionEvidence evidence) => new()
    {
        // 결과 창은 비모달이라 그사이 편집을 껐다 켰을 수 있다: 이 결과를 받은 편집 세션에만 채운다.
        Fill = c => ReferenceEquals(TableEdit, edit) && edit.ApplyAdvice(c.Candidate, c.Draft, c.Checks),
        Ask = (options, token) => run.AskAsync(evidence, options, token), // 같은 근거로 다시 묻는다(모으기를 다시 돌리지 않음)
        PickReportFile = ReportPicker(),
    };

    private Func<string, CancellationToken, Task<string?>> ReportPicker() =>
        PickScriptFile ?? ((suggested, token) => SqlEditor.SqlEditorHost.Current.Dialogs.PickReportSaveFileAsync(suggested, token));

    private void Present(PartitionAdvisorResultViewModel result)
    {
        if (result.HasAi)
        {
            _adviceResult = result; // 근거만 창은 「추천 다시 보기」 대상이 아니다
        }

        if (ShowAdvisorResult is { } show)
        {
            show(result);
            return;
        }

        if (_adviceWindow is { } open)
        {
            open.DataContext = result; // 탭 하나에 창 하나
            open.Activate();
            return;
        }

        var window = new PartitionAdvisorResultWindow { DataContext = result };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_adviceWindow, window))
            {
                _adviceWindow = null;
            }
        };
        _adviceWindow = window;
        if (MainWindow() is { } owner)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }

    private IPartitionEvidenceCollector? EvidenceCollectorFor() =>
        EvidenceCollector ?? _services?.GetService(typeof(IPartitionEvidenceCollector)) as IPartitionEvidenceCollector;

    private AiSettings? CurrentAiSettings() => (_services?.GetService(typeof(SettingsService)) as SettingsService)?.Current.Ai;

    /// <summary>보내기 전에 파일의 AI 설정을 다시 읽는다(AI 튜닝과 같게 — 다른 창이 저장했어도 확인 창과 라우터가 같은 설정을 본다). 못 읽으면 앞 값.</summary>
    private async Task ReloadAdvisorSettingsAsync(CancellationToken ct)
    {
        if (AdvisorSettings is not null || _services?.GetService(typeof(SettingsService)) is not SettingsService settings)
        {
            return;
        }

        try
        {
            await settings.ReloadAiAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("AI 설정을 다시 읽지 못했습니다: {0}", ex.GetType().Name);
        }
    }

    private static async Task<bool> AskOpenSettingsAsync(string reason)
    {
        var choice = await SqlEditor.SqlEditorHost.Current.Dialogs.AskButtonsAsync(
            TableEditText.T("AI 파티션 추천", "AI partition advice"),
            reason + Environment.NewLine + Environment.NewLine
            + TableEditText.T("AI 없이 숫자만 보려면 「분포 보기」를 쓰세요.", "To see the numbers without AI, use Evidence view."),
            [TableEditText.T("설정 열기", "Open settings"), TableEditText.T("닫기", "Close")],
            0,
            CancellationToken.None);
        return choice == 0;
    }

    private static async Task ShowAdvisorStartWindowAsync(PartitionAdvisorStartViewModel vm)
    {
        var window = new PartitionAdvisorStartWindow { DataContext = vm };
        if (MainWindow() is { } owner)
        {
            await window.ShowDialog(owner).ConfigureAwait(true);
        }
        else
        {
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            await closed.Task.ConfigureAwait(true);
        }
    }

    private static Window? MainWindow() => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
