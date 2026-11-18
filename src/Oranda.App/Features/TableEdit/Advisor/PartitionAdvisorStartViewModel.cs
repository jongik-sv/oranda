using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Ai.Tuning;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Features;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Schema.TableEdit.Partitions.Advisor;
using Oranda.Core.Settings;

namespace Oranda.App.Features.TableEdit.Advisor;

/// <summary>진행 단계 한 줄의 상태.</summary>
public enum AdvisorStepState
{
    Pending,
    Running,
    Done,
}

/// <summary>진행 단계 한 줄(열·키 → 통계 → 조회 조건 → 분포 → AI 응답 대기).</summary>
public sealed partial class AdvisorStepViewModel(string text) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Mark))]
    private AdvisorStepState _state;

    [ObservableProperty]
    private string _text = text;

    public string Mark => State switch
    {
        AdvisorStepState.Done => "✔",
        AdvisorStepState.Running => "…",
        _ => "·",
    };
}

/// <summary>
/// AI 파티션 추천 확인 창(설계 3장 ②, 시안 ②). AI 튜닝 시작 창과 같은 규칙: 받는 곳을 먼저 보이고, 실제 값이 나가는 항목(PA-3)은 따로 켜며
/// 가림 대상 접속이면 잠근다. 「보낼 내용 보기」 = 근거를 모아 실제로 보낼 글을 펼친다(그 근거를 「모아서 보내기」가 그대로 쓴다).
/// 진행 중에는 같은 창이 단계와 「취소」를 보인다. 끝나면 <see cref="Outcome"/> 을 채우고 <see cref="CloseRequested"/> 를 낸다.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "취소 토큰은 한 번의 작업 동안만 쥐고 끝나면 finally 에서 정리한다(창이 닫히면 Stop/Abort 로 취소).")]
public sealed partial class PartitionAdvisorStartViewModel : ObservableObject
{
    private readonly PartitionAdvisorRun _run;
    private CancellationTokenSource? _cts;
    private PartitionEvidence? _evidence;
    private (bool Predicates, bool Distributions)? _evidenceKey;

    public PartitionAdvisorStartViewModel(PartitionAdvisorRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        _run = run;
        Steps =
        [
            new(PartitionAdvisorText.Step(EvidenceStep.ColumnsAndKeys)),
            new(PartitionAdvisorText.Step(EvidenceStep.Statistics)),
            new(PartitionAdvisorText.Step(EvidenceStep.Predicates)),
            new(PartitionAdvisorText.Step(EvidenceStep.Distributions)),
            new(PartitionAdvisorText.WaitingStep),
        ];
    }

    public string Title => TableEditText.T("AI 파티션 추천 — ", "AI partition advice — ") + _run.Setup.Table.Name;

    /// <summary>받는 곳(AI 튜닝과 같은 활성 대상).</summary>
    public string RecipientText => _run.Route.RecipientText;

    /// <summary>목적(0 조회 성능 + 오래된 데이터 정리 · 1 조회 성능 · 2 정리).</summary>
    [ObservableProperty]
    private int _goalIndex;

    /// <summary>조회 조건(V$SQL_PLAN, PA-2 기본 켬).</summary>
    [ObservableProperty]
    private bool _includePredicates = true;

    /// <summary>분포 조회(실제 SELECT, PA-1 기본 켬).</summary>
    [ObservableProperty]
    private bool _includeDistributions = true;

    /// <summary>실제 값 보내기(PA-3 기본 끔). 잠겨 있으면 켤 수 없다.</summary>
    [ObservableProperty]
    private bool _sendValues;

    public bool CanSendValues => _run.Route.ValuesAllowed && !IsBusy;

    /// <summary>값 보내기 줄 아래 설명(잠긴 까닭 또는 끄면 어떻게 되는지).</summary>
    public string SendValuesNote => _run.Route.ValuesLockedReason
        ?? TableEditText.T("끄면 값은 V1, V2… 자리표시와 건수만 보냅니다. LIST 값은 받은 뒤 앱이 실제 값으로 되돌립니다.",
            "When off, values go as V1, V2… placeholders with counts only. The app puts the real LIST values back after the answer.");

    /// <summary>분포 조회 비용(표본 규칙 PA-1). 근거를 모은 뒤에는 실제로 읽은 행 수.</summary>
    public string DistributionCostText
    {
        get
        {
            if (_evidence is { Distributions.Count: > 0 } e)
            {
                var read = e.Distributions.Max(d => d.SampledRows);
                var percent = e.Distributions.Select(d => d.SamplePercent).FirstOrDefault(p => p is not null);
                return percent is { } p
                    ? TableEditText.T($"SAMPLE({p.ToString("0.##", CultureInfo.InvariantCulture)}) 로 약 {read:N0}행 읽었습니다", $"Read about {read:N0} rows with SAMPLE({p.ToString("0.##", CultureInfo.InvariantCulture)})")
                    : TableEditText.T($"표본 없이 {read:N0}행 읽었습니다", $"Read {read:N0} rows without sampling");
            }

            return TableEditText.T("실제 SELECT 를 돌립니다: 20만 행 이하면 전체, 넘으면 SAMPLE 로 약 5만 행을 읽습니다",
                "Runs real SELECTs: the whole table up to 200,000 rows, otherwise about 50,000 sampled rows");
        }
    }

    /// <summary>모은 근거에서 건너뛴 항목(모으기 전에는 빈 목록).</summary>
    public IReadOnlyList<string> SkippedLines => _evidence is { } e ? [.. e.Skipped.Select(PartitionAdvisorText.Skip)] : [];

    public bool HasSkipped => SkippedLines.Count > 0;

    /// <summary>「보낼 내용 보기」로 펼친 실제 프롬프트.</summary>
    [ObservableProperty]
    private string _previewText = string.Empty;

    [ObservableProperty]
    private bool _isPreviewOpen;

    /// <summary>모으기·AI 대기 중.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendValues), nameof(CanStart), nameof(CancelText))]
    private bool _isBusy;

    [ObservableProperty]
    private string _errorText = string.Empty;

    public ObservableCollection<AdvisorStepViewModel> Steps { get; }

    public bool CanStart => !IsBusy;

    public string CancelText => IsBusy ? TableEditText.T("멈추기", "Stop") : TableEditText.T("취소", "Cancel");

    /// <summary>받은 결과(창이 닫힌 뒤 부르는 쪽이 결과 창을 연다). 취소·닫기면 null.</summary>
    public AdvisorOutcome? Outcome { get; private set; }

    /// <summary>창을 닫아 달라.</summary>
    public event EventHandler? CloseRequested;

    public AdvicePromptOptions Options => new(
        GoalIndex switch { 1 => AdviceGoal.Query, 2 => AdviceGoal.Purge, _ => AdviceGoal.QueryAndPurge },
        SendValues && _run.Route.ValuesAllowed,
        PartitionAdvisorRun.Language);

    partial void OnSendValuesChanged(bool value)
    {
        if (value && !_run.Route.ValuesAllowed)
        {
            SendValues = false; // 잠긴 줄은 켜지지 않는다(창 밖에서 켜도)
            return;
        }

        RefreshPreview();
    }

    partial void OnGoalIndexChanged(int value) => RefreshPreview();

    partial void OnIncludePredicatesChanged(bool value) => ClosePreviewIfStale();

    partial void OnIncludeDistributionsChanged(bool value) => ClosePreviewIfStale();

    /// <summary>「보낼 내용 보기」: 근거를 모으고(이미 같은 선택으로 모았으면 그대로) 보낼 글을 펼친다. 다시 누르면 접는다.</summary>
    public async Task TogglePreviewAsync()
    {
        if (IsPreviewOpen)
        {
            IsPreviewOpen = false;
            return;
        }

        if (await EnsureEvidenceAsync() is not { } evidence)
        {
            return;
        }

        PreviewText = _run.BuildPrompt(evidence, Options).Prompt;
        IsPreviewOpen = true;
    }

    /// <summary>「모아서 보내기」: 모으기 → AI. 받았으면 true(창을 닫는다). 실패·취소면 창에 남는다.</summary>
    public async Task<bool> SendAsync()
    {
        if (IsBusy || await EnsureEvidenceAsync() is not { } evidence)
        {
            return false;
        }

        IsBusy = true;
        ErrorText = string.Empty;
        _cts = new CancellationTokenSource();
        Steps[^1].State = AdvisorStepState.Running;
        try
        {
            var outcome = await _run.AskAsync(evidence, Options, _cts.Token);
            if (outcome.Error is { } error && outcome.Advice is null && outcome.RawText.Length == 0)
            {
                Steps[^1].State = AdvisorStepState.Pending;
                ErrorText = TableEditText.T("AI 가 답하지 못했습니다: ", "The AI did not answer: ") + error;
                return false;
            }

            Steps[^1].State = AdvisorStepState.Done;
            Outcome = outcome;
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (OperationCanceledException)
        {
            Steps[^1].State = AdvisorStepState.Pending;
            ErrorText = TableEditText.T("멈췄습니다. 다시 보낼 수 있습니다.", "Stopped. You can send again.");
            return false;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    /// <summary>「취소」: 진행 중이면 멈추고(창은 남음), 아니면 창을 닫는다.</summary>
    public void Cancel()
    {
        if (_cts is { } cts)
        {
            cts.Cancel();
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>창이 닫힐 때: 진행 중인 일을 멈춘다.</summary>
    public void Abort() => _cts?.Cancel();

    private async Task<PartitionEvidence?> EnsureEvidenceAsync()
    {
        var key = (IncludePredicates, IncludeDistributions);
        if (_evidence is { } cached && _evidenceKey == key)
        {
            return cached;
        }

        IsBusy = true;
        ErrorText = string.Empty;
        foreach (var step in Steps)
        {
            step.State = AdvisorStepState.Pending;
        }

        _cts = new CancellationTokenSource();
        try
        {
            var options = new EvidenceOptions(IncludePredicates, IncludeDistributions);
            var evidence = await _run.CollectAsync(options, new UiProgress<EvidenceProgress>(OnProgress), _cts.Token);
            for (var i = 0; i < Steps.Count - 1; i++)
            {
                Steps[i].State = AdvisorStepState.Done;
            }

            _evidence = evidence;
            _evidenceKey = key;
            OnPropertyChanged(nameof(SkippedLines));
            OnPropertyChanged(nameof(HasSkipped));
            OnPropertyChanged(nameof(DistributionCostText));
            return evidence;
        }
        catch (OperationCanceledException)
        {
            ErrorText = TableEditText.T("모으기를 멈췄습니다.", "Collecting stopped.");
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ErrorText = TableEditText.T("근거를 모으지 못했습니다: ", "Could not collect the evidence: ") + ex.Message;
            return null;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    private void OnProgress(EvidenceProgress p)
    {
        var at = (int)p.Step;
        for (var i = 0; i < Steps.Count - 1; i++)
        {
            Steps[i].State = i < at ? AdvisorStepState.Done : i == at ? AdvisorStepState.Running : AdvisorStepState.Pending;
        }

        if (p.Step == EvidenceStep.Distributions && p.Column is { } column)
        {
            Steps[at].Text = PartitionAdvisorText.Step(EvidenceStep.Distributions) + string.Create(CultureInfo.InvariantCulture, $" — {column} ({p.Index}/{p.Total})");
        }
    }

    private void RefreshPreview()
    {
        if (IsPreviewOpen && _evidence is { } evidence)
        {
            PreviewText = _run.BuildPrompt(evidence, Options).Prompt;
        }
    }

    private void ClosePreviewIfStale()
    {
        if (_evidenceKey != (IncludePredicates, IncludeDistributions))
        {
            IsPreviewOpen = false; // 보낼 정보를 바꿨다: 펼친 글은 옛 근거다
        }
    }

    /// <summary>
    /// AI 를 쓸 수 없는 까닭(없으면 null). AI 튜닝 대화 창의 사용 불가 원인과 같은 판정(기능 꺼짐·드라이버 없음·「선택 안 함」·찾을 수 없는 프로필·쓸 수 없는 CLI).
    /// </summary>
    public static string? UnavailableReason(IFeatureGate? gate, IAiDriver? driver, AiSettings? settings)
    {
        if (gate is not null && !gate.IsEnabled(FeatureIds.AiTuning))
        {
            return TableEditText.T("AI 기능이 꺼져 있습니다. 기능 설정에서 AI 튜닝을 켜면 쓸 수 있습니다.", "AI features are off. Turn on AI tuning in the feature settings.");
        }

        if (driver is null)
        {
            return TableEditText.T("AI 드라이버를 쓸 수 없습니다(이 빌드에 없습니다).", "No AI driver in this build.");
        }

        if (settings is null)
        {
            return TableEditText.T("AI 설정을 읽지 못했습니다.", "Could not read the AI settings.");
        }

        return AiActiveProfile.Find(settings).Kind switch
        {
            AiActiveKind.None => TableEditText.T("AI 모델이 「선택 안 함」 입니다. 설정 › AI 모델에서 고르면 쓸 수 있습니다.", "No AI model is selected. Choose one in Settings › AI model."),
            AiActiveKind.ProfileMissing => TableEditText.T("선택한 AI 프로필을 찾을 수 없습니다. 설정 › AI 모델에서 다른 프로필을 고르십시오.", "The selected AI profile is missing. Choose another in Settings › AI model."),
            AiActiveKind.ProfileInvalid => TableEditText.T("선택한 AI 프로필의 형식을 이 버전이 알지 못합니다. 설정 › AI 모델에서 다른 프로필을 고르십시오.", "This version does not know the selected AI profile. Choose another in Settings › AI model."),
            AiActiveKind.SystemCliUnavailable or AiActiveKind.CustomCliUnavailable => TableEditText.T("선택한 AI CLI 를 쓸 수 없습니다. 설정 › AI 모델에서 다른 대상을 고르십시오.", "The selected AI CLI is not available. Choose another in Settings › AI model."),
            _ => null,
        };
    }
}

/// <summary>진행 알림을 UI 스레드에서(이미 UI 스레드면 바로) 부른다 — 시험에서도 순서가 그대로다.</summary>
internal sealed class UiProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            report(value);
        }
        else
        {
            Dispatcher.UIThread.Post(() => report(value));
        }
    }
}
