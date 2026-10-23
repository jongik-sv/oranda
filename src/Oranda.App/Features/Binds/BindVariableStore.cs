using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Security;
using Oranda.Core.Workspaces;

namespace Oranda.App.Features.Binds;

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
        _name = Normalize(name);
    }

    /// <summary>콜론 없는 이름(<c>:ID</c> → <c>ID</c>). 대문자로 둔다(TSK-04-12, Oracle 바인드 이름은 대소문자를 가리지 않는다).</summary>
    [ObservableProperty]
    private string _name;

    /// <summary>바인드 이름을 대문자로. 큰따옴표로 감싼 이름(<c>"x"</c>)은 그대로 둔다.</summary>
    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.TrimStart().StartsWith('"') ? name : name.ToUpperInvariant();
    }

    partial void OnNameChanged(string value)
    {
        var upper = Normalize(value);
        if (!string.Equals(upper, value, StringComparison.Ordinal))
        {
            Name = upper;
        }
    }

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private BindTypeChoice _typeChoice = BindTypeChoice.All[0];

    [ObservableProperty]
    private BindDirectionChoice _directionChoice = BindDirectionChoice.All[0];

    /// <summary>값 칸이 자료형에 맞지 않을 때의 이유(툴팁·붉은 테두리). 맞으면 null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValueError))]
    private string? _valueError;

    public bool HasValueError => ValueError is not null;

    partial void OnTextChanged(string value) => ValueError = BindValueFormatter.Validate(Type, Direction, value);

    partial void OnTypeChoiceChanged(BindTypeChoice value)
    {
        OnPropertyChanged(nameof(Type));
        ValueError = BindValueFormatter.Validate(value.Type, Direction, Text);
    }

    partial void OnDirectionChoiceChanged(BindDirectionChoice value)
    {
        OnPropertyChanged(nameof(Direction));
        ValueError = BindValueFormatter.Validate(Type, value.Direction, Text);
    }

    /// <summary>값 칸을 떠날 때: 자료형에 맞게 글을 정리한다(NUMBER 쉼표·공백 제거, 날짜 서식 통일).</summary>
    public void Commit(string? dateFormat = null) => Text = BindValueFormatter.Normalize(Type, Text, dateFormat);

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
/// SQL 탭마다 하나(<see cref="ForTab"/>, 탭이 없는 도구 창용 기본 목록은 이벤트 버스(앱)마다 하나인 <see cref="For"/>). 목록 자체는 메모리에 있고, 탭이 참조하는 이름의 값만 작업 공간(<c>tabs[].binds</c>)과
/// .sql 파일 끝 바인드 블록에 함께 저장된다(<see cref="Snapshot"/>·<see cref="Restore"/>, data-model 2.6).
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

    /// <summary>
    /// 탭의 바인드 목록(TSK-04-11): SQL 탭마다 자기 목록을 가진다. 탭이 없으면(도구 창 등) <paramref name="bus"/> 의 기본 목록
    /// (<see cref="For"/>)을 돌려준다.
    /// </summary>
    public static BindVariableStore ForTab(IBindOwner? tab, IEventBus? bus) => tab?.Binds ?? For(bus);

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

    /// <summary>
    /// <paramref name="keep"/> 에 없는 줄을 지운다(TSK-04-12: 글에서 사라진 이름이 목록에 남지 않게). 지운 줄이 있으면 true.
    /// </summary>
    public bool Prune(IEnumerable<string> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        var names = new HashSet<string>(keep.Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        var stale = Rows.Where(r => !names.Contains(r.Name.Trim())).ToList();
        foreach (var row in stale)
        {
            Rows.Remove(row);
        }

        if (stale.Count > 0)
        {
            Renumber();
        }

        return stale.Count > 0;
    }

    /// <summary>
    /// 디스크에 남길 값(작업 공간 <c>tabs[].binds</c>·.sql 바인드 블록). <paramref name="names"/> 순서로, 목록에 있고 기본값
    /// (빈 값·문자열·IN — 실행이 만들어 둔 빈 줄)이 아닌 줄만 담는다. 비밀로 보이는 이름(<see cref="SensitiveBindNames"/>)은 뺀다.
    /// </summary>
    public IReadOnlyList<WorkspaceBind> Snapshot(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var binds = new List<WorkspaceBind>();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (SensitiveBindNames.IsSensitive(name) || Find(name) is not { } row
                || (row.Text.Length == 0 && row.Type == BindDataType.Varchar2 && row.Direction == BindDirection.In))
            {
                continue;
            }

            binds.Add(new WorkspaceBind
            {
                Name = row.Name.Trim(),
                Type = row.Type,
                Direction = row.Direction,
                Value = row.Text,
                IsNull = row.Text.Length == 0,
            });
        }

        return binds;
    }

    /// <summary>저장해 둔 값으로 줄을 만들거나 덮어쓴다(탭을 열 때). null 이면 아무것도 하지 않는다.</summary>
    public void Restore(IEnumerable<WorkspaceBind>? binds)
    {
        foreach (var b in binds ?? [])
        {
            var name = b.Name.Trim();
            if (name.Length == 0)
            {
                continue;
            }

            var row = Find(name) ?? Add(name);
            row.Text = b.IsNull ? string.Empty : b.Value;
            row.Type = b.Type;
            row.Direction = b.Direction;
        }
    }

    /// <summary>자동 저장 변경 감지용 표지: <see cref="Snapshot"/> 이 담을 이름·형식·방향·NULL·값.</summary>
    public string Fingerprint(IEnumerable<string> names)
    {
        var sb = new StringBuilder();
        foreach (var b in Snapshot(names))
        {
            sb.Append(b.Name).Append('\u0003').Append(b.Type).Append('\u0003').Append(b.Direction).Append('\u0003')
                .Append(b.IsNull).Append('\u0003').Append(b.Value.Length).Append(':').Append(b.Value).Append('\u0004');
        }

        return sb.ToString();
    }

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

/// <summary>자기 바인드 목록을 가진 것(SQL 탭). 바인드 저장소가 편집기 형식을 몰라도 되게 하는 경계.</summary>
public interface IBindOwner
{
    BindVariableStore Binds { get; }
}
