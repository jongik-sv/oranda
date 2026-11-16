using System;
using Avalonia.Data.Converters;

namespace Oranda.App.Features.ChangeJournal;

/// <summary>
/// 변경 일지 좁은 배치(카드)의 원인 배지·원복 상태 칩 색 갈래. 줄이 이미 보이는 글(<see cref="IJournalRow.CauseText"/>·<see cref="IJournalRow.ScriptText"/>)만 보고
/// 정하므로 ViewModel 을 바꾸지 않는다. XAML: <c>Classes.run="{Binding CauseText, Converter={x:Static vm:JournalTone.Cause}, ConverterParameter=run}"</c>.
/// </summary>
public static class JournalTone
{
    /// <summary>원인 글이 이 갈래(<c>commit</c>·<c>run</c>·<c>ddl</c>·<c>unknown</c>)인지.</summary>
    public static IValueConverter Cause { get; } = new FuncValueConverter<string?, string?, bool>((text, tone) => CauseOf(text) == tone);

    /// <summary>원복 상태 글이 이 갈래(<c>ddl</c>·<c>fail</c>·<c>none</c>)인지. 경고(불완전·확인 필요)는 <see cref="IJournalRow.IsScriptWarning"/> 로 따로 칠한다.</summary>
    public static IValueConverter Script { get; } = new FuncValueConverter<string?, string?, bool>((text, tone) => ScriptOf(text) == tone);

    /// <summary>
    /// 줄이 확정(초록)인지. 묶음은 「확정 · N문장」, 묶음 안 문장 줄은 부모 묶음이 확정일 때만(문장 줄 글은 묶음이 열림·확인 필요여도 「N문장」이라 글로는 모른다).
    /// </summary>
    public static IValueConverter Confirmed { get; } = new FuncValueConverter<IJournalRow?, bool>(IsConfirmed);

    public static bool IsConfirmed(IJournalRow? row) => row switch
    {
        RollbackStatementItem s => s.Parent.Group.State == Oranda.Core.Rollback.RollbackScriptState.Confirmed && ScriptOf(s.ScriptText) == "ok",
        { } r => ScriptOf(r.ScriptText) == "ok",
        null => false,
    };

    /// <summary>원인 갈래: 원복 실행(#N) = run, DDL 실행·컴파일 = ddl, 커밋·DML 실행 = commit, 그 밖(롤백·커밋(추정)·모름·열림) = unknown(회색).</summary>
    public static string CauseOf(string? text) => text switch
    {
        null or "" => "unknown",
        _ when text.StartsWith("원복 실행", StringComparison.Ordinal) => "run",
        "DDL 실행" or "컴파일" => "ddl",
        "커밋" or "DML 실행" => "commit",
        _ => "unknown",
    };

    /// <summary>원복 상태 갈래: 확정 · N문장 / 문장 줄의 N문장 = ok, DDL 보관 = ddl, 보관 실패 = fail, 그 밖(무효·만들지 않음·열림·불완전·확인 필요) = none.</summary>
    public static string ScriptOf(string? text) => text switch
    {
        null or "" => "none",
        "DDL 보관" => "ddl",
        "보관 실패" => "fail",
        _ when text.StartsWith("확정", StringComparison.Ordinal) => "ok",
        _ when text.EndsWith("문장", StringComparison.Ordinal) && char.IsDigit(text[0]) => "ok",
        _ => "none",
    };
}
