using System;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Connection;
using Oranda.Core.Rollback;

namespace Oranda.App.Features.ChangeJournal;

/// <summary>
/// 변경 일지 표의 한 줄(되돌리기 지점 설계 11.1, 화면 1). DDL 보관본(<see cref="JournalItem"/>), 커밋 묶음(<see cref="RollbackGroupItem"/>),
/// 펼친 묶음 안의 문장(<see cref="RollbackStatementItem"/>) 이 같은 열(시간 | 객체 | 원인 | 요약 | 영향 행 | 시점 | 원복 스크립트)을 쓴다.
/// </summary>
public interface IJournalRow
{
    /// <summary>시간 역순으로 합칠 때 쓰는 시각.</summary>
    DateTimeOffset At { get; }

    string TimeText { get; }

    string ObjectText { get; }

    string CauseText { get; }

    string SummaryText { get; }

    string RowsText { get; }

    string PointText { get; }

    string ScriptText { get; }

    /// <summary>앞 아이콘 키.</summary>
    string IconKey { get; }

    /// <summary>줄을 위험색으로(보관 실패).</summary>
    bool IsFailure { get; }

    /// <summary>원복 스크립트 칸을 경고색으로(불완전·확인 필요).</summary>
    bool IsScriptWarning { get; }

    /// <summary>묶음 안 문장 줄(들여 쓴다).</summary>
    bool IsChild { get; }

    /// <summary>앞 칸의 접기·펼치기 표시(묶음만, 문장이 있을 때 ▸·▾).</summary>
    string ExpandGlyph { get; }
}

/// <summary>커밋 묶음 한 줄(접고 펼친다).</summary>
public sealed partial class RollbackGroupItem(RollbackGroup group) : ObservableObject, IJournalRow
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandGlyph))]
    private bool _isExpanded;

    public RollbackGroup Group { get; } = group;

    public long Id => Group.Id;

    public DateTimeOffset At => Group.EndedAt ?? Group.StartedAt;

    public string TimeText => At.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>첫 표, 둘 이상이면 「SAMPLE.PRODUCTS 외 2」.</summary>
    public string ObjectText => Group.Tables.Count switch
    {
        0 => string.Empty,
        1 => Name(Group.Tables[0]),
        _ => $"{Name(Group.Tables[0])} 외 {Group.Tables.Count - 1}",
    };

    /// <summary>「커밋」·「롤백」·「모름」. 원복 스크립트가 없는 묶음의 모름 끝은 「커밋(추정)」(RD-16).</summary>
    public string CauseText => Group.EndKind switch
    {
        TransactionEndKind.Commit => "커밋",
        TransactionEndKind.Rollback => "롤백",
        TransactionEndKind.RollbackToSavepoint => "롤백",
        _ when Group.State == RollbackScriptState.Open => "열림",
        _ when Group.State == RollbackScriptState.Unverified => "모름",
        _ when !Group.HasScriptFile || Group.State == RollbackScriptState.NotCreated => "커밋(추정)",
        _ => "모름",
    };

    /// <summary>「문장 3개 · prod-example」.</summary>
    public string SummaryText => $"문장 {StatementCount.ToString(CultureInfo.InvariantCulture)}개 · {Group.ProfileName}";

    public int StatementCount => Group.Statements.Count;

    public string RowsText => Group.RowsTotal.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>「SCN 48213907 → 48214022」(기준 → 커밋). 롤백 묶음은 기준만.</summary>
    public string PointText
    {
        get
        {
            if (Group.BasePoint is not { } basePoint)
            {
                return Group.CommitPoint is { } only ? "→ " + only.Text : string.Empty;
            }

            return Group.CommitPoint is { } commit && Group.EndKind is not (TransactionEndKind.Rollback or TransactionEndKind.RollbackToSavepoint)
                ? $"{basePoint.Text} → {commit.Number.ToString(CultureInfo.InvariantCulture)}"
                : basePoint.Text;
        }
    }

    /// <summary>상태 표시(설계 11.2): 「확정 · 16문장」, 「불완전 · 이유」, 「무효 · 롤백함」, 「만들지 않음 · 이유」, 「확인 필요 · 이유」.</summary>
    public string ScriptText => StateText(Group.State, Group.StepCount, Group.Reason, Group.StateReason, Group.Environment);

    public string IconKey => "fileSql";

    public bool IsFailure => false;

    public bool IsScriptWarning => Group.State is RollbackScriptState.Incomplete or RollbackScriptState.Unverified;

    public bool IsChild => false;

    /// <summary>원복 스크립트를 열 수 있다(파일이 있고 무효·만들지 않음이 아님).</summary>
    public bool CanOpenScript => Group.HasScriptFile && Group.State is RollbackScriptState.Confirmed or RollbackScriptState.Incomplete or RollbackScriptState.Unverified;

    public string ExpandGlyph => StatementCount == 0 ? string.Empty : IsExpanded ? "▾" : "▸";

    internal static string Name(Oranda.Core.Metadata.DbObjectRef table) =>
        string.IsNullOrEmpty(table.Owner) ? table.Name : table.Owner + "." + table.Name;

    /// <summary>상태 값 → 표시 글(설계 11.2).</summary>
    internal static string StateText(RollbackScriptState state, int steps, NotCreatedReason reason, string? stateReason, ConnectionEnvironment env)
    {
        var why = string.IsNullOrWhiteSpace(stateReason) ? null : stateReason.Trim();
        return state switch
        {
            RollbackScriptState.Confirmed => $"확정 · {steps.ToString("N0", CultureInfo.CurrentCulture)}문장",
            RollbackScriptState.Incomplete => "불완전 · " + (why ?? "일부 행의 원복 문장을 만들지 못함"),
            RollbackScriptState.Invalid => "무효 · " + (why ?? "롤백함"),
            RollbackScriptState.Unverified => "확인 필요 · " + (why ?? "트랜잭션 끝을 모름"),
            RollbackScriptState.Open => "열림 · 커밋 전",
            _ => "만들지 않음" + (NotCreatedText(reason, env, why) is { } r ? " · " + r : string.Empty),
        };
    }

    /// <summary>「만들지 않음」 이유(설계 11.2: 개발·테스트 접속, 설정 끔, 확인 창에서 끔, 상한 넘음, 대상 아님, 안전한 저장소 없음).</summary>
    internal static string? NotCreatedText(NotCreatedReason reason, ConnectionEnvironment env, string? stateReason) => reason switch
    {
        NotCreatedReason.NotProduction => env == ConnectionEnvironment.Test ? "테스트 접속" : "개발 접속",
        NotCreatedReason.SettingOff => "설정 끔",
        NotCreatedReason.Declined => "확인 창에서 끔",
        NotCreatedReason.OverLimit => "상한 넘음",
        NotCreatedReason.NotTarget => stateReason ?? "대상 아님",
        NotCreatedReason.NoSecureStore => "안전한 저장소 없음",
        NotCreatedReason.Failed => stateReason ?? "만드는 중 오류",
        NotCreatedReason.Suppressed => "기록 안 함",
        _ => stateReason,
    };
}

/// <summary>펼친 묶음 안의 문장 한 줄.</summary>
public sealed class RollbackStatementItem(RollbackGroupItem parent, RollbackStatementInfo statement) : IJournalRow
{
    public RollbackGroupItem Parent { get; } = parent;

    public RollbackStatementInfo Statement { get; } = statement;

    public DateTimeOffset At => Statement.ExecutedAt;

    public string TimeText => Statement.ExecutedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string ObjectText => Statement.Table is { } t ? RollbackGroupItem.Name(t) : string.Empty;

    public string CauseText => "DML 실행";

    /// <summary>가린 문장 요약 한 줄.</summary>
    public string SummaryText => string.Join(' ', (Statement.Summary ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public string RowsText => Statement.RowsAffected is { } n ? n.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;

    /// <summary>직전 시점 번호.</summary>
    public string PointText => Statement.BeforePoint?.Number.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>원복 문장 수(「12문장」, 「만들지 않음」, 「무효」, 「불완전 · 이유」).</summary>
    public string ScriptText => Statement.Invalidated || Statement.State == RollbackScriptState.Invalid || Parent.Group.State == RollbackScriptState.Invalid
        ? "무효"
        : Statement.State switch
        {
            RollbackScriptState.Incomplete => "불완전" + (string.IsNullOrWhiteSpace(Statement.Reason) ? string.Empty : " · " + Statement.Reason),
            RollbackScriptState.NotCreated => "만들지 않음",
            _ => $"{Statement.StepCount.ToString("N0", CultureInfo.CurrentCulture)}문장",
        };

    public string IconKey => "fileSql";

    public bool IsFailure => false;

    public bool IsScriptWarning => Statement.State == RollbackScriptState.Incomplete;

    public bool IsChild => true;

    public string ExpandGlyph => string.Empty;
}

/// <summary>「원인」 거르기.</summary>
public enum JournalCauseFilter
{
    All,
    Ddl,
    Compile,
    Dml,
}

/// <summary>「원인」 콤보 한 줄.</summary>
public sealed record JournalCauseChoice(JournalCauseFilter Filter, string Label)
{
    public override string ToString() => Label;

    internal static JournalCauseChoice[] All { get; } =
    [
        new(JournalCauseFilter.All, "원인: 전체"),
        new(JournalCauseFilter.Ddl, "DDL 실행"),
        new(JournalCauseFilter.Compile, "컴파일"),
        new(JournalCauseFilter.Dml, "DML"),
    ];

    internal static JournalCauseChoice Of(JournalCauseFilter filter) => All.First(c => c.Filter == filter);
}
