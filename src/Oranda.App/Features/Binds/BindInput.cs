using System.Globalization;
using Oranda.Core.Execution;

namespace Oranda.App.Features.Binds;

/// <summary>바인드 값 한 개의 입력: 이름·형식·방향·글자(EXEC-03). 「바인드 변수」 목록 줄이 실행 값으로 바꿀 때 쓴다.</summary>
public sealed class BindInput
{
    public BindInput(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public BindDataType Type { get; set; } = BindDataType.Varchar2;

    public BindDirection Direction { get; set; } = BindDirection.In;

    public string Text { get; set; } = string.Empty;

    public bool IsNull { get; set; }

    /// <summary>값을 형식에 맞게 바꾼다. 못 바꾸면 <paramref name="error"/> 에 이유를 담고 null.</summary>
    public BindValue? ToBindValue(out string? error)
    {
        error = null;
        if (Direction == BindDirection.Out || Type == BindDataType.RefCursor || IsNull || (Text.Length == 0 && Type is not (BindDataType.Varchar2 or BindDataType.NVarchar2 or BindDataType.FixedChar or BindDataType.Clob)))
        {
            return new BindValue(Type, null, Direction) { Size = Direction == BindDirection.In ? null : 4000 };
        }

        try
        {
            object? value = Type switch
            {
                BindDataType.Number => decimal.Parse(Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture),
                BindDataType.BinaryDouble => double.Parse(Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture),
                BindDataType.Date or BindDataType.Timestamp => DateTime.Parse(Text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None),
                BindDataType.TimestampTz => DateTimeOffset.Parse(Text.Trim(), CultureInfo.InvariantCulture),
                BindDataType.Raw => ParseHex(Text),
                BindDataType.Boolean => ParseBool(Text),
                _ => Text,
            };
            return new BindValue(Type, value, Direction) { Size = Direction == BindDirection.In ? null : 4000 };
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
        {
            error = $":{Name} 값 '{Text}' 을(를) {Type} 로 읽을 수 없습니다.";
            return null;
        }
    }

    private static byte[] ParseHex(string text)
    {
        var hex = text.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
        return Convert.FromHexString(hex);
    }

    private static bool ParseBool(string text) => text.Trim().ToUpperInvariant() switch
    {
        "TRUE" or "1" or "Y" or "T" => true,
        "FALSE" or "0" or "N" or "F" => false,
        _ => throw new FormatException("불리언 값이 아닙니다."),
    };
}
