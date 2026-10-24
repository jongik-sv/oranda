using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Localization;
using Oranda.Language.Tuning;

namespace Oranda.App.Features.Tuning.Assistant;

/// <summary>인덱스 후보 목록 한 줄.</summary>
public sealed class IndexCandidateItem(IndexAdvice advice)
{
    public IndexAdvice Advice { get; } = advice;

    /// <summary>표 (열) — <c>LOT_HIST (EQP_ID, EVT_TIME)</c>.</summary>
    public string TableText => $"{Advice.Candidate.TableName} ({string.Join(", ", Advice.Candidate.Columns)})";

    public string BasisText => Advice.BasisText;

    public string OverlapText
    {
        get
        {
            var ix = Advice.Candidate.OverlappingIndex ?? string.Empty;
            var en = LiteralTranslator.IsEnglish;
            return Advice.Overlap switch
            {
                IndexOverlapKind.Covered => en ? $"Exists ({ix})" : $"이미 있음 ({ix})",
                IndexOverlapKind.Extends => en ? $"{ix} leading cols" : $"{ix} 앞 열",
                IndexOverlapKind.SharesLeading => en ? $"{ix} first col" : $"{ix} 첫 열",
                IndexOverlapKind.Unknown => en ? "Not checked" : "확인 못함",
                _ => en ? "None" : "없음",
            };
        }
    }

    /// <summary>겹침 칸 경고색(넓히는 안·첫 열만 같음).</summary>
    public bool IsOverlapWarning => Advice.Overlap is IndexOverlapKind.Extends or IndexOverlapKind.SharesLeading;

    /// <summary>이미 있는 인덱스가 덮는다(성공색·표 칸 흐리게).</summary>
    public bool IsCovered => Advice.Overlap == IndexOverlapKind.Covered;

    public bool IsUnknown => Advice.Overlap == IndexOverlapKind.Unknown;

    public string Tip => Advice.Reason;
}

/// <summary>재작성 후보 목록 한 줄. 체크하면 벤치마크에 든다.</summary>
public sealed partial class RewriteCandidateItem : ObservableObject
{
    [ObservableProperty]
    private bool _isChecked;

    public RewriteCandidateItem(RewriteAdvice advice)
    {
        Advice = advice;
        // NULL 의미·행 수가 바뀔 수 있는 재작성은 꺼 둔 채(사용자가 켠다).
        _isChecked = advice.HasRewrite && !advice.SemanticsMayChange;
    }

    public RewriteAdvice Advice { get; }

    public string Title => Advice.Title;

    public string LocationText => Advice.Location(LiteralTranslator.IsEnglish);

    /// <summary>고친 SQL 이 있어야 벤치마크에 넣을 수 있다.</summary>
    public bool CanCheck => Advice.HasRewrite;

    /// <summary>경고 아이콘(형 변환 의심·결과가 바뀔 수 있음).</summary>
    public bool IsWarning => Advice.Rule == RewriteRule.ImplicitConversion || Advice.SemanticsMayChange;

    /// <summary>바꾼 SQL 없이 경고만(흐리게).</summary>
    public bool IsWarningOnly => !Advice.HasRewrite;

    public string CheckTip => CanCheck
        ? LiteralTranslator.IsEnglish ? "Include in the benchmark" : "벤치마크에 넣기"
        : LiteralTranslator.IsEnglish ? "Warning only — no rewritten SQL" : "경고만 있습니다 — 바꾼 SQL 이 없습니다";
}

/// <summary>힌트 대안 목록 한 줄. 체크하면 원문에 힌트를 넣은 변형이 벤치마크에 든다.</summary>
public sealed partial class HintCandidateItem : ObservableObject
{
    [ObservableProperty]
    private bool _isChecked;

    public HintCandidateItem(HintAdvice advice, bool check)
    {
        Advice = advice;
        _isChecked = check && advice.Enabled;
    }

    public HintAdvice Advice { get; }

    public string HintText => Advice.Comment;

    public string MeaningText => Advice.Enabled ? Advice.Meaning : Advice.DisabledReason ?? Advice.Meaning;

    public bool CanCheck => Advice.Enabled;

    public bool IsDisabled => !Advice.Enabled;
}

/// <summary>벤치마크 변형의 출처.</summary>
public enum BenchmarkSource
{
    Original,
    Rewrite,
    Hint,
}

/// <summary>벤치마크 줄의 진행 상태.</summary>
public enum BenchmarkRowState
{
    Pending,
    Running,
    Done,
    Failed,
    Rejected,
    Cancelled,
}

/// <summary>벤치마크 표 한 줄(원문 또는 변형). 실행하며 값이 바뀐다.</summary>
public sealed partial class BenchmarkRowItem : ObservableObject
{
    /// <summary>「상대」 막대 최대 폭(DIP).</summary>
    public const double BarMax = 120;

    [ObservableProperty]
    private BenchmarkRowState _state;

    [ObservableProperty]
    private string _elapsedText = "-";

    [ObservableProperty]
    private string _minMaxText = string.Empty;

    [ObservableProperty]
    private string _logicalText = string.Empty;

    [ObservableProperty]
    private string _physicalText = string.Empty;

    [ObservableProperty]
    private string _rowsText = string.Empty;

    [ObservableProperty]
    private string _planHashText = string.Empty;

    [ObservableProperty]
    private string _verdictText = string.Empty;

    [ObservableProperty]
    private double _barWidth;

    [ObservableProperty]
    private BenchmarkVerdictKind? _verdict;

    [ObservableProperty]
    private bool _isRowsMismatch;

    public BenchmarkRowItem(int number, string label, string sql, BenchmarkSource source)
    {
        Number = number;
        Label = label;
        Sql = sql;
        Source = source;
    }

    public int Number { get; }

    public string Label { get; }

    public string Sql { get; }

    public BenchmarkSource Source { get; }

    /// <summary>반복마다 잰 경과(밀리초).</summary>
    public System.Collections.Generic.List<double> ElapsedSamples { get; } = [];

    public long LogicalReads { get; set; }

    public long PhysicalReads { get; set; }

    public long? Rows { get; set; }

    public long? PlanHash { get; set; }

    /// <summary>반복 평균 경과. 다 재지 못했으면 null.</summary>
    public TimeSpan? Elapsed { get; set; }

    public bool IsBaseline => Source == BenchmarkSource.Original;

    public bool IsBest => Verdict == BenchmarkVerdictKind.Best;

    public bool IsFailed => State is BenchmarkRowState.Failed or BenchmarkRowState.Rejected or BenchmarkRowState.Cancelled;

    public bool IsRunning => State == BenchmarkRowState.Running;

    /// <summary>막대 색: 원문 = 중립, 가장 나음 = 초록, 행 수 다름 = 호박, 나머지 = 파랑.</summary>
    public bool IsBarNeutral => IsBaseline;

    public bool IsBarGood => IsBest;

    public bool IsBarWarn => IsRowsMismatch;

    public string SqlTip => Sql;

    partial void OnStateChanged(BenchmarkRowState value)
    {
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsRunning));
    }

    partial void OnVerdictChanged(BenchmarkVerdictKind? value)
    {
        OnPropertyChanged(nameof(IsBest));
        OnPropertyChanged(nameof(IsBarGood));
    }

    partial void OnIsRowsMismatchChanged(bool value) => OnPropertyChanged(nameof(IsBarWarn));

    /// <summary>재기 전으로 되돌린다.</summary>
    public void Reset()
    {
        State = BenchmarkRowState.Pending;
        ElapsedSamples.Clear();
        LogicalReads = PhysicalReads = 0;
        Rows = PlanHash = null;
        Elapsed = null;
        ElapsedText = "-";
        MinMaxText = LogicalText = PhysicalText = RowsText = PlanHashText = VerdictText = string.Empty;
        BarWidth = 0;
        Verdict = null;
        IsRowsMismatch = false;
    }

    /// <summary>잰 값을 칸 글로.</summary>
    public void ShowMeasures()
    {
        static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);
        if (ElapsedSamples.Count > 0)
        {
            ElapsedText = Ms(Elapsed?.TotalMilliseconds ?? ElapsedSamples[^1]);
            MinMaxText = ElapsedSamples.Count > 1 ? $"{Ms(Min(), unit: false)} / {Ms(Max(), unit: false)}" : string.Empty;
        }

        LogicalText = N(LogicalReads);
        PhysicalText = N(PhysicalReads);
        RowsText = Rows is { } r ? N(r) : string.Empty;
        PlanHashText = PlanHash?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private double Min()
    {
        var m = double.MaxValue;
        foreach (var v in ElapsedSamples)
        {
            m = Math.Min(m, v);
        }

        return m;
    }

    private double Max()
    {
        var m = double.MinValue;
        foreach (var v in ElapsedSamples)
        {
            m = Math.Max(m, v);
        }

        return m;
    }

    /// <summary>밀리초 글(1 초 미만은 소수 한 자리, 그 위는 정수 + 천 단위 쉼표).</summary>
    public static string Ms(double ms, bool unit = true)
    {
        var text = ms < 10 ? ms.ToString("0.0", CultureInfo.InvariantCulture) : Math.Round(ms).ToString("N0", CultureInfo.InvariantCulture);
        return unit ? text + " ms" : text;
    }
}
