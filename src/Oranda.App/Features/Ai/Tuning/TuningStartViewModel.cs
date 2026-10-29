using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;

namespace Oranda.App.Features.Ai.Tuning;

/// <summary>시작 확인 창의 모델 선택 줄(기본 모델·로컬 모델 등).</summary>
public sealed record TuningModelOption(string Id, string Title);

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
    bool MaskLiterals = false)
{
    /// <summary>모델을 지정하지 않고 보내는 줄의 id(설정 값이 비어 있을 때의 기본). <c>InitialModelId</c> 는 처음 골라 둘 줄의 id(설정 값).</summary>
    public const string DefaultModelId = "default";

    /// <summary>접속의 AI 허용 수준이 「끔」이라 시작할 수 없다.</summary>
    public bool AccessOff => Context.AccessLevel == AiAccessLevel.Off;

    /// <summary>벤치마크(SELECT 실행)는 허용 수준이 「조회 실행」 이상일 때 쓴다(운영 접속도 같다: 상한이 「조회 실행」 이고 게이트가 SELECT 만 통과시킨다).</summary>
    public bool BenchmarkAllowed => Context.AccessLevel >= AiAccessLevel.Query;

    /// <summary>운영 접속이다(안내 문구를 보인다).</summary>
    public bool IsProduction => Context.Environment == ConnectionEnvironment.Production;
}

/// <summary>사용자가 「튜닝 시작」 을 눌러 확정한 선택.</summary>
public sealed record TuningStartChoice(bool Benchmark, bool IndexDdl, string ModelId);

/// <summary>AI 튜닝 시작 확인 창(시안 9): 먼저 보낼 것·요청 시 줄 것·보내지 않음, 벤치마크·인덱스 문안·모델 선택.</summary>
public sealed partial class TuningStartViewModel : ObservableObject
{
    public const string BlockedText = "이 접속은 AI 허용 수준이 「끔」 입니다. 접속 편집에서 허용 수준을 바꾼 뒤 다시 시도하세요.";

    public const string ProductionNoticeText = "운영 접속: 조회(SELECT)만 실행하고 벤치마크 반복은 5회 이하이며, 리터럴 값은 항상 가려서 보냅니다.";

    public const string MaskLineText = "리터럴 :vN 가림";

    public const string BenchmarkLevelText = "벤치마크는 접속의 AI 허용 수준이 「조회 실행」 이상일 때만 쓸 수 있습니다.";

    [ObservableProperty]
    private bool _benchmark;

    [ObservableProperty]
    private bool _indexDdl = true;

    [ObservableProperty]
    private TuningModelOption? _model;

    public TuningStartViewModel(TuningStartInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        Info = info;
        Benchmark = info.BenchmarkAllowed && !info.IsProduction; // 운영은 허용만 하고 기본은 끈다(문장당 30초·변형 5개·반복 5회 부하)
        Model = info.Models.FirstOrDefault(m => string.Equals(m.Id, info.InitialModelId, StringComparison.OrdinalIgnoreCase))
            ?? (info.Models.Count > 0 ? info.Models[0] : null);
    }

    public TuningStartInfo Info { get; }

    public IReadOnlyList<TuningModelOption> Models => Info.Models;

    public bool IsBlocked => Info.AccessOff;

    public string? BlockedMessage => IsBlocked ? BlockedText : null;

    public bool BenchmarkEnabled => Info.BenchmarkAllowed && !IsBlocked;

    public string? BenchmarkNote => Info.BenchmarkAllowed ? null : BenchmarkLevelText;

    /// <summary>값을 :vN 으로 가려 보낸다(운영 접속이거나 설정이 켜짐).</summary>
    public bool MasksLiterals => Info.MaskLiterals;

    public bool IsProduction => Info.IsProduction;

    public string? ProductionNotice => IsProduction ? ProductionNoticeText : null;

    public bool HasSharedPoolLine => !string.IsNullOrEmpty(Info.SharedPoolLine);

    public bool CanStart => !IsBlocked && Model is not null;

    /// <summary>확정한 선택. 시작할 수 없으면 null.</summary>
    public TuningStartChoice? Choice => CanStart ? new TuningStartChoice(Benchmark && BenchmarkEnabled, IndexDdl, Model!.Id) : null;

    partial void OnModelChanged(TuningModelOption? value) => OnPropertyChanged(nameof(CanStart));

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start() => StartRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>「튜닝 시작」 이 눌렸다(창이 닫고 <see cref="Choice"/> 를 돌려준다).</summary>
    public event EventHandler? StartRequested;
}
