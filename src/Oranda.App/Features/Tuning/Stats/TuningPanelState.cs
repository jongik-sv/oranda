using System;
using Oranda.App.Features.Localization;

namespace Oranda.App.Features.Tuning.Stats;

/// <summary>통계 보기·자식 커서 화면의 상태(빈 안내·내용 중 무엇을 보일지).</summary>
public enum TuningPanelState
{
    /// <summary>접속 없음.</summary>
    NoSession,

    /// <summary>대상(표·sql_id)을 아직 고르지 않음.</summary>
    NoTarget,

    /// <summary>읽는 중.</summary>
    Loading,

    /// <summary>내용 있음.</summary>
    Ready,

    /// <summary>빈 결과(통계 없음·공유 풀에 없음).</summary>
    Empty,

    /// <summary>권한 부족(ORA-00942·ORA-01031·V$ 권한 없음).</summary>
    Denied,

    /// <summary>오류.</summary>
    Error,
}

/// <summary>화면 글자(ko/en). XAML 밖(ViewModel·코드)에서 만드는 글은 이것으로 고른다.</summary>
internal static class TuningText
{
    public static string T(string ko, string en) => LiteralTranslator.IsEnglish ? en : ko;

    /// <summary>예외 메시지 첫 줄.</summary>
    public static string FirstLine(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var i = message.IndexOf('\n', StringComparison.Ordinal);
        return (i < 0 ? message : message[..i]).Trim();
    }
}
