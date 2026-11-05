using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;
using Oranda.Core.Settings;

namespace Oranda.App.Features.Ai.Tuning;

/// <summary>시작 확인 창의 모델 선택 줄(기본 모델·로컬 모델 등).</summary>
public sealed record TuningModelOption(string Id, string Title);

/// <summary>시작 확인 창의 effort 선택 줄(CLI 기본 · low~max).</summary>
public sealed record TuningEffortOption(string Id, string Title);

/// <summary>
/// 시작 확인 창에 보일 사실. 접속의 허용 수준과 환경은 <paramref name="Context"/> 가 들고 있고(<see cref="AiAccessPolicy.Effective"/> 로 이미 계산된 값),
/// 화면은 그 값을 다시 계산하지 않고 읽기만 한다.
/// </summary>
public sealed record TuningStartInfo(
    string Title,
    string StatementSummary,
    string? SharedPoolLine,
    string SendFirst,
    string SendOnRequest,
    string NotSent,
    AiContext Context,
    IReadOnlyList<TuningModelOption> Models,
    string InitialModelId = TuningStartInfo.DefaultModelId,
    bool MaskLiterals = false,
    IReadOnlyList<TuningEffortOption>? Efforts = null,
    string InitialEffortId = TuningStartInfo.DefaultModelId,
    bool ShowEffort = true,
    int BenchmarkTimeoutSeconds = AiSettings.DefaultBenchmarkTimeoutSeconds)
{
    /// <summary>
    /// 모델(또는 effort)을 지정하지 않고 보내는 줄의 id(설정 값이 비어 있을 때의 기본). <c>InitialModelId</c>·<c>InitialEffortId</c> 는 처음 골라 둘 줄의 id(설정 값).
    /// <c>Efforts</c> 가 null 이면 CLI 기본 줄 + low~max 다. <c>ShowEffort</c> 가 false 면 effort 줄을 숨기고 값을 넘기지 않는다.
    /// </summary>
    public const string DefaultModelId = "default";

    /// <summary>
    /// 고를 수 있는 effort 줄: low~max. 설정 값이 비어 있거나 모르는 값이면 「기본 — Claude Code」 줄(effort 를 넘기지 않음)을 맨 앞에 둔다.
    /// </summary>
    public static IReadOnlyList<TuningEffortOption> EffortOptions(string? configured)
    {
        List<TuningEffortOption> options = [];
        if (configured is null || !AiSettings.EffortLevels.Contains(configured))
        {
            options.Add(new TuningEffortOption(DefaultModelId, "기본 — Claude Code"));
        }

        options.AddRange(AiSettings.EffortLevels.Select(level => new TuningEffortOption(level, level)));
        return options;
    }

    /// <summary>접속의 AI 허용 수준이 「끔」이라 시작할 수 없다.</summary>
    public bool AccessOff => Context.AccessLevel == AiAccessLevel.Off;

    /// <summary>벤치마크(SELECT 실행)는 허용 수준이 「조회 실행」 이상일 때 쓴다(운영 접속도 같다: 상한이 「조회 실행」 이고 게이트가 SELECT 만 통과시킨다).</summary>
    public bool BenchmarkAllowed => Context.AccessLevel >= AiAccessLevel.Query;

    /// <summary>운영 접속이다(안내 문구를 보인다).</summary>
    public bool IsProduction => Context.Environment == ConnectionEnvironment.Production;
}

/// <summary>사용자가 「튜닝 시작」 을 눌러 확정한 선택.</summary>
/// <summary>
/// 시작 창의 선택. <paramref name="RunRiskyFunctions"/> = 「위험 함수도 실행」(대화마다, 기본 꺼짐): 벤치마크가 위험 함수가 든 후보도 읽기 전용으로 실행한다.
/// <paramref name="EffortId"/> = <c>claude --effort</c> 값(이어 말하기에도 같은 값). <see cref="TuningStartInfo.DefaultModelId"/> 이면 넘기지 않는다.
/// <paramref name="BenchmarkTimeoutSeconds"/> = 이번 실행에만 쓰는 운영 벤치마크 제한 시간(초, 0 = 제한 없음). 설정에는 저장하지 않는다.
/// </summary>
public sealed record TuningStartChoice(
    bool Benchmark,
    bool IndexDdl,
    string ModelId,
    bool RunRiskyFunctions = false,
    string EffortId = TuningStartInfo.DefaultModelId,
    int BenchmarkTimeoutSeconds = AiSettings.DefaultBenchmarkTimeoutSeconds);

/// <summary>AI 튜닝 시작 확인 창(시안 9): 먼저 보낼 것·요청 시 줄 것·보내지 않음, 벤치마크·인덱스 문안·모델 선택.</summary>
public sealed partial class TuningStartViewModel : ObservableObject
{
    public const string BlockedText = "이 접속은 AI 허용 수준이 「끔」 입니다. 접속 편집에서 허용 수준을 바꾼 뒤 다시 시도하세요.";

    public const string ProductionNoticeText = "운영 접속: 조회(SELECT)만 실행하고 벤치마크 반복은 5회 이하이며, 리터럴 값은 항상 가려서 보냅니다.";

    public const string ProductionNoticeLocalAiText = "운영 접속: 조회(SELECT)만 실행하고 벤치마크 반복은 5회 이하입니다. 「로컬 AI」 프로필이라 값을 가리지 않고 보냅니다(비밀번호·주민번호·카드·계좌·주소·인증값 열의 히스토그램 끝값 제외).";

    public const string MaskLineText = "리터럴 :vN 가림";

    public const string BenchmarkLevelText = "벤치마크는 접속의 AI 허용 수준이 「조회 실행」 이상일 때만 쓸 수 있습니다.";

    [ObservableProperty]
    private bool _benchmark;

    [ObservableProperty]
    private bool _indexDdl = true;

    /// <summary>입력 칸의 글(초). 처음 값은 설정의 기본값이고, 이번 실행에만 적용한다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BenchmarkTimeoutValid), nameof(ShowBenchmarkTimeoutError), nameof(ShowBenchmarkTimeoutWarning), nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _benchmarkTimeoutText;

    /// <summary>「위험 함수도 실행」: 자치 트랜잭션·외부 접근·확인 못 한 함수가 든 후보도 실측한다(끄면 운영 커서 실측으로 대체). 대화마다 기본 꺼짐.</summary>
    [ObservableProperty]
    private bool _runRiskyFunctions;

    [ObservableProperty]
    private TuningModelOption? _model;

    [ObservableProperty]
    private TuningEffortOption? _effort;

    public TuningStartViewModel(TuningStartInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        Info = info;
        Benchmark = info.BenchmarkAllowed && !info.IsProduction; // 운영은 허용만 하고 기본은 끈다(문장당 제한 시간·변형 5개·반복 5회 부하)
        _benchmarkTimeoutText = AiSettings.NormalizeBenchmarkTimeout(info.BenchmarkTimeoutSeconds).ToString(CultureInfo.InvariantCulture);
        Model = info.Models.FirstOrDefault(m => string.Equals(m.Id, info.InitialModelId, StringComparison.OrdinalIgnoreCase))
            ?? (info.Models.Count > 0 ? info.Models[0] : null);
        Efforts = info.Efforts ?? TuningStartInfo.EffortOptions(null);
        Effort = Efforts.FirstOrDefault(e => string.Equals(e.Id, info.InitialEffortId, StringComparison.OrdinalIgnoreCase)) ?? Efforts[0];
    }

    public TuningStartInfo Info { get; }

    public IReadOnlyList<TuningModelOption> Models => Info.Models;

    public IReadOnlyList<TuningEffortOption> Efforts { get; }

    /// <summary>effort 줄을 보인다. API 경로(OpenAI 형식 프로필)는 effort 를 쓰지 않아 끈다.</summary>
    public bool ShowEffort => Info.ShowEffort;

    public bool IsBlocked => Info.AccessOff;

    public string? BlockedMessage => IsBlocked ? BlockedText : null;

    public bool BenchmarkEnabled => Info.BenchmarkAllowed && !IsBlocked;

    public string? BenchmarkNote => Info.BenchmarkAllowed ? null : BenchmarkLevelText;

    /// <summary>값을 :vN 으로 가려 보낸다(운영 접속이거나 설정이 켜짐).</summary>
    public bool MasksLiterals => Info.MaskLiterals;

    public bool IsProduction => Info.IsProduction;

    /// <summary>「로컬 AI」 프로필로 보낸다(값을 가리지 않는다는 줄을 보인다).</summary>
    public bool IsLocalAi => Info.Context.LocalAi;

    public const string TimeoutUnlimitedWarningText = "사용자가 중지할 때까지 운영 DB 에서 실행합니다";

    public const string TimeoutRangeText = "제한 시간은 5~3600초 또는 0(제한 없음)으로 입력하세요.";

    /// <summary>제한 시간 입력 칸을 보인다: 운영 접속에서 벤치마크를 켰을 때만(개발·테스트 접속은 제한이 없다).</summary>
    public bool ShowBenchmarkTimeout => IsProduction && Benchmark && BenchmarkEnabled;

    /// <summary>입력한 글에서 읽은 제한 시간(초). 정수가 아니거나 범위(0 또는 5~3600) 밖이면 null.</summary>
    public int? ParsedBenchmarkTimeout =>
        int.TryParse(BenchmarkTimeoutText?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && AiSettings.IsValidBenchmarkTimeout(seconds)
            ? seconds
            : null;

    /// <summary>입력 칸이 안 보이거나 값이 올바르다.</summary>
    public bool BenchmarkTimeoutValid => !ShowBenchmarkTimeout || ParsedBenchmarkTimeout is not null;

    public bool ShowBenchmarkTimeoutError => ShowBenchmarkTimeout && ParsedBenchmarkTimeout is null;

    /// <summary>0(제한 없음)을 입력했다: 사용자가 중지할 때까지 운영 DB 에서 도는 경고를 보인다.</summary>
    public bool ShowBenchmarkTimeoutWarning => ShowBenchmarkTimeout && ParsedBenchmarkTimeout == 0;

    public string? ProductionNotice => IsProduction ? (Info.Context.LocalAi ? ProductionNoticeLocalAiText : ProductionNoticeText) : null;

    public bool HasSharedPoolLine => !string.IsNullOrEmpty(Info.SharedPoolLine);

    public bool CanStart => !IsBlocked && Model is not null && BenchmarkTimeoutValid;

    /// <summary>
    /// 입력 칸이 숨었을 때(벤치마크를 껐다) 쓰는 값. 설정 값이다. 다만 운영 접속에서 설정이 0(제한 없음)이면 경고 없이 제한이 풀리지 않게 30초로 닫는다:
    /// 벤치마크 확인란은 문구만 바꾸고 이어 말하기의 도구 호출은 막지 않기 때문이다. 제한 없음은 칸에서 0 을 보고 정했을 때만 적용한다.
    /// </summary>
    private int HiddenTimeout()
    {
        var configured = AiSettings.NormalizeBenchmarkTimeout(Info.BenchmarkTimeoutSeconds);
        return IsProduction && configured == 0 ? AiSettings.DefaultBenchmarkTimeoutSeconds : configured;
    }

    partial void OnBenchmarkChanged(bool value) => NotifyTimeoutVisibility();

    private void NotifyTimeoutVisibility()
    {
        OnPropertyChanged(nameof(ShowBenchmarkTimeout));
        OnPropertyChanged(nameof(BenchmarkTimeoutValid));
        OnPropertyChanged(nameof(ShowBenchmarkTimeoutError));
        OnPropertyChanged(nameof(ShowBenchmarkTimeoutWarning));
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    /// <summary>확정한 선택. 시작할 수 없으면 null.</summary>
    public TuningStartChoice? Choice => CanStart
        ? new TuningStartChoice(Benchmark && BenchmarkEnabled, IndexDdl, Model!.Id, Benchmark && BenchmarkEnabled && RunRiskyFunctions, ShowEffort ? Effort?.Id ?? TuningStartInfo.DefaultModelId : TuningStartInfo.DefaultModelId,
            ShowBenchmarkTimeout ? ParsedBenchmarkTimeout ?? AiSettings.DefaultBenchmarkTimeoutSeconds : HiddenTimeout())
        : null;

    partial void OnModelChanged(TuningModelOption? value) => OnPropertyChanged(nameof(CanStart));

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start() => StartRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>「튜닝 시작」 이 눌렸다(창이 닫고 <see cref="Choice"/> 를 돌려준다).</summary>
    public event EventHandler? StartRequested;
}
