using System.Globalization;
using Avalonia.Data.Converters;
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

    private sealed class TextConverter(Func<object?, string> map) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => map(value);

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
