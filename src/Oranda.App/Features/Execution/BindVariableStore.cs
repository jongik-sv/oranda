using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Events;
using Oranda.Core.Execution;

namespace Oranda.App.Features.Execution;

/// <summary>형식 콤보 한 칸. 오렌지 Bind Variable List 의 네 형식(문자열·숫자·날짜·고정 길이 문자)을 맨 위에 둔다.</summary>
public sealed record BindTypeChoice(BindDataType Type, string Label)
{
    public static IReadOnlyList<BindTypeChoice> All { get; } =
    [
        new(BindDataType.Varchar2, "문자열"),
        new(BindDataType.Number, "숫자"),
        new(BindDataType.Date, "날짜"),
        new(BindDataType.FixedChar, "고정 길이 문자"),
        new(BindDataType.NVarchar2, "NVARCHAR2"),
        new(BindDataType.Timestamp, "TIMESTAMP"),
        new(BindDataType.TimestampTz, "TIMESTAMP WITH TIME ZONE"),
        new(BindDataType.BinaryDouble, "BINARY_DOUBLE"),
        new(BindDataType.Clob, "CLOB"),
        new(BindDataType.Blob, "BLOB"),
        new(BindDataType.Raw, "RAW"),
        new(BindDataType.Boolean, "BOOLEAN"),
        new(BindDataType.Json, "JSON"),
        new(BindDataType.RefCursor, "REF CURSOR"),
    ];

    public static BindTypeChoice Of(BindDataType type) => All.FirstOrDefault(c => c.Type == type) ?? All[0];
}

/// <summary>방향 콤보 한 칸(오렌지 8.0 의 IN·OUT·IN OUT).</summary>
public sealed record BindDirectionChoice(BindDirection Direction, string Label)
{
    public static IReadOnlyList<BindDirectionChoice> All { get; } =
    [
        new(BindDirection.In, "IN"),
        new(BindDirection.Out, "OUT"),
        new(BindDirection.InOut, "IN OUT"),
    ];

    public static BindDirectionChoice Of(BindDirection direction) => All.FirstOrDefault(c => c.Direction == direction) ?? All[0];
}

/// <summary>바인드 변수 목록 한 줄: 이름·값·형식·방향. 값이 비면 NULL 이다.</summary>
public sealed partial class BindVariableRow : ObservableObject
{
    public BindVariableRow(string name)
    {
        _name = name;
    }

    /// <summary>콜론 없는 이름(<c>:ID</c> → <c>ID</c>).</summary>
    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private BindTypeChoice _typeChoice = BindTypeChoice.All[0];

    [ObservableProperty]
    private BindDirectionChoice _directionChoice = BindDirectionChoice.All[0];

    /// <summary>목록 번호(1부터).</summary>
    [ObservableProperty]
    private int _number;

    public BindDataType Type
    {
        get => TypeChoice.Type;
        set => TypeChoice = BindTypeChoice.Of(value);
    }

    public BindDirection Direction
    {
        get => DirectionChoice.Direction;
        set => DirectionChoice = BindDirectionChoice.Of(value);
    }

    /// <summary>실행할 값. 형식에 맞지 않으면 <paramref name="error"/> 에 이유를 담고 null.</summary>
    public BindValue? ToBindValue(out string? error)
    {
        var input = new BindInput(Name.Trim()) { Type = Type, Direction = Direction, Text = Text, IsNull = Text.Length == 0 };
        return input.ToBindValue(out error);
    }
}

/// <summary>
/// 바인드 변수 목록(EXEC-03, 오렌지 Bind Variable List 방식). 「바인드 변수」 창이 보이고 고치는 목록이며, 실행은 묻지 않고 이 값을 쓴다.
/// 문장에 처음 보는 이름이 있으면 목록에 줄을 더하고 실행하지 않는다(값 없이 INSERT 등이 NULL 로 실행되지 않게).
/// 값은 세션 메모리에만 둔다(data-model: 디스크에 쓰지 않는다). 이벤트 버스(앱)마다 하나.
/// </summary>
public sealed class BindVariableStore
{
    private static readonly ConditionalWeakTable<IEventBus, BindVariableStore> PerBus = [];
    private static readonly object Gate = new();
    private static BindVariableStore? s_fallback;

    public static BindVariableStore For(IEventBus? bus)
    {
        lock (Gate)
        {
            if (bus is null)
            {
                return s_fallback ??= new BindVariableStore();
            }

            return PerBus.GetValue(bus, _ => new BindVariableStore());
        }
    }

    public ObservableCollection<BindVariableRow> Rows { get; } = [];

    /// <summary>창이 이 이름의 값 칸으로 포커스를 옮기기를 바란다(실행이 새 이름을 만났을 때).</summary>
    public event EventHandler<string>? FocusRequested;

    /// <summary>창이 아직 만들어지지 않았을 때 받은 포커스 요청(창이 만들어지면 처리한다).</summary>
    public string? PendingFocus { get; set; }

    public BindVariableRow? Find(string name) =>
        Rows.FirstOrDefault(r => string.Equals(r.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>목록에 없는 이름을 더하고, 새로 더한 이름을 돌려준다.</summary>
    public IReadOnlyList<string> Ensure(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var added = new List<string>();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Find(name) is null)
            {
                Add(name);
                added.Add(name);
            }
        }

        return added;
    }

    public BindVariableRow Add(string name)
    {
        var row = new BindVariableRow(name);
        Rows.Add(row);
        Renumber();
        return row;
    }

    public void Remove(BindVariableRow row)
    {
        Rows.Remove(row);
        Renumber();
    }

    public void Clear() => Rows.Clear();

    public void RequestFocus(string name)
    {
        PendingFocus = name;
        FocusRequested?.Invoke(this, name);
    }

    /// <summary>
    /// 이름마다 값을 만든다. 목록에 없는 이름이나 형식에 맞지 않는 값이 있으면 false 와 이유.
    /// </summary>
    public bool TryBuild(IEnumerable<string> names, out Dictionary<string, BindValue> values, out string? error)
    {
        ArgumentNullException.ThrowIfNull(names);
        values = new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase);
        error = null;
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Find(name) is not { } row)
            {
                error = $"바인드 변수 :{name} 이(가) 목록에 없습니다.";
                return false;
            }

            if (row.ToBindValue(out error) is not { } value)
            {
                return false;
            }

            values[name] = value;
        }

        return true;
    }

    /// <summary>
    /// 편집기에 붙여 넣을 SQL 리터럴(오렌지 「Paste Bind Variables Into Editor」). 문자는 따옴표로 감싸고, 숫자는 그대로,
    /// 날짜는 <c>TO_DATE</c>·<c>TO_TIMESTAMP</c>, 빈 값은 <c>NULL</c>. 못 바꾸면 null.
    /// </summary>
    public static string? ToLiteral(BindVariableRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Text.Length == 0)
        {
            return "NULL";
        }

        if (row.ToBindValue(out _) is not { Value: { } value })
        {
            return null;
        }

        return row.Type switch
        {
            BindDataType.Number or BindDataType.BinaryDouble => Convert.ToString(value, CultureInfo.InvariantCulture),
            BindDataType.Date => "TO_DATE('" + ((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "', 'YYYY-MM-DD HH24:MI:SS')",
            BindDataType.Timestamp => "TO_TIMESTAMP('" + ((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "', 'YYYY-MM-DD HH24:MI:SS.FF')",
            BindDataType.TimestampTz => "TO_TIMESTAMP_TZ('" + ((DateTimeOffset)value).ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture) + "', 'YYYY-MM-DD HH24:MI:SS.FF TZH:TZM')",
            BindDataType.Raw => "HEXTORAW('" + Convert.ToHexString((byte[])value) + "')",
            BindDataType.Boolean => (bool)value ? "TRUE" : "FALSE",
            BindDataType.NVarchar2 => "N" + Quote(row.Text),
            _ => Quote(row.Text),
        };
    }

    private static string Quote(string text)
    {
        var sb = new StringBuilder(text.Length + 2).Append('\'');
        sb.Append(text.Replace("'", "''", StringComparison.Ordinal));
        return sb.Append('\'').ToString();
    }

    private void Renumber()
    {
        for (int i = 0; i < Rows.Count; i++)
        {
            Rows[i].Number = i + 1;
        }
    }
}
