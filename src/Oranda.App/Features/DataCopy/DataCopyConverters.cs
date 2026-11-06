using System.Globalization;
using Avalonia.Data.Converters;
using Oranda.App.Features.Localization;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Features.DataCopy;

/// <summary>열거 값을 화면 글자로 바꾸는 변환기 모음.</summary>
public static class DataCopyConverters
{
    public static IValueConverter RuleKindText { get; } = new TextConverter(v => v switch
    {
        RuleKind.All => "전체",
        RuleKind.Period => "기간",
        RuleKind.ParentKey => "부모 키",
        RuleKind.Custom => "직접 조건",
        RuleKind.Exclude => "제외",
        _ => string.Empty,
    });

    /// <summary>규칙 종류 콤보에서 이름 옆에 붙는 짧은 설명(「직접 조건」이 WHERE 직접 입력임을 바로 알 수 있게).</summary>
    public static IValueConverter RuleKindHint { get; } = new TextConverter(v => v switch
    {
        RuleKind.All => "모든 행",
        RuleKind.Period => "기간 열 + 추가 조건(AND)",
        RuleKind.ParentKey => "부모 표와 FK 로 이은 행",
        RuleKind.Custom => "WHERE 절 직접 입력",
        RuleKind.Exclude => "복사하지 않음",
        _ => string.Empty,
    });

    public static IValueConverter ActionText { get; } = new TextConverter(v => v switch
    {
        TargetAction.DeleteAllInsert => "비우고 넣기",
        TargetAction.DeleteRangeInsert => "같은 범위 지우고 넣기",
        TargetAction.AppendOnly => "추가만",
        TargetAction.MergeByPk => "PK 기준 MERGE",
        TargetAction.Recreate => "테이블 다시 만들기",
        _ => string.Empty,
    });

    public static IValueConverter CommitText { get; } = new TextConverter(v => v switch
    {
        CommitMode.PerTable => "테이블마다",
        CommitMode.PerBatch => "배치마다",
        CommitMode.AtEnd => "모두 끝난 뒤 한 번",
        _ => string.Empty,
    });

    public static IValueConverter MaskKindText { get; } = new TextConverter(v => v switch
    {
        MaskKind.Fixed => "고정값",
        MaskKind.KeepFirst => "앞 n 글자만 남기기",
        MaskKind.KeepLast => "뒤 n 글자만 남기기",
        MaskKind.Hash => "해시",
        MaskKind.Null => "NULL",
        _ => string.Empty,
    });

    public static IValueConverter OutcomeText { get; } = new TextConverter(v => v switch
    {
        CopyOutcome.Succeeded => "성공",
        CopyOutcome.PartiallyFailed => "일부 실패",
        CopyOutcome.Failed => "실패",
        CopyOutcome.Stopped => "중지",
        _ => string.Empty,
    });

    /// <summary>부모 키 후보 콤보: 부모 계정을 알면 <c>계정.표</c>(같은 이름 표가 두 계정에 있어도 구별되게), 모르면 표 이름.</summary>
    public static IValueConverter ParentLinkText { get; } = new TextConverter(v => v is ParentLink link
        ? (string.IsNullOrEmpty(link.ParentOwner) ? link.ParentTable : link.ParentOwner + "." + link.ParentTable)
        : string.Empty);

    // 바인딩으로 들어가는 글은 화면 번역(LiteralTranslator)이 지나간 뒤에 바뀌므로, 영어 화면이면 여기서 문구 표의 영어로 바꾼다.
    private sealed class TextConverter(Func<object?, string> map) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var text = map(value);
            return LiteralTranslator.IsEnglish && LiteralTranslator.Table.TryGetValue(text, out var english) ? english : text;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
