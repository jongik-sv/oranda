using System.Globalization;
using System.Text;

namespace Oranda.Core.Transfer.Copy.ExternalLoad;

/// <summary>
/// 데이터 파일 글 형식. <see cref="FieldSeparator"/> = 필드 구분, <see cref="RecordTerminator"/> = 레코드 끝, <see cref="Enclose"/> = NULL 아닌 필드를 감싸는 글자.
/// <see cref="Escape"/> 가 null 이면 값 안의 감싸는 글자를 두 번 적고(SQL*Loader), 있으면 감싸는 글자와 이스케이프 글자 앞에 이스케이프 글자를 둔다(tbLoader <c>ESCAPED BY '\\'</c>).
/// NULL 은 어느 형식이든 감싸지 않은 빈 필드다. 글자 값에 <see cref="RecordTerminator"/> 의 첫 글자가 있으면 그 행은 실패한다.
/// </summary>
public sealed record LoaderFileFormat(string FieldSeparator, string RecordTerminator, char Enclose, char? Escape)
{
    /// <summary>SQL*Loader: <c>,</c> · <c>"</c>(두 번 적기) · 레코드 끝 0x1E 0x0A(제어 파일 <c>"str X'1E0A'"</c>).</summary>
    public static LoaderFileFormat SqlLoader { get; } = new(",", LoaderDataWriter.RecordTerminator, '"', null);

    /// <summary>tbLoader: <c>,</c> · <c>"</c> · <c>\</c> 이스케이프 · 레코드 끝 0x1E 0x0A.</summary>
    public static LoaderFileFormat TbLoader { get; } = new(",", LoaderDataWriter.RecordTerminator, '"', '\\');

    /// <summary>필드 하나를 감싸 적는다.</summary>
    internal void AppendEnclosed(StringBuilder builder, string value)
    {
        builder.Append(Enclose);
        foreach (var c in value)
        {
            if (Escape is { } escape)
            {
                if (c == Enclose || c == escape)
                {
                    builder.Append(escape);
                }
            }
            else if (c == Enclose)
            {
                builder.Append(Enclose);
            }

            builder.Append(c);
        }

        builder.Append(Enclose);
    }
}

/// <summary>
/// 테이블 하나의 외부 도구 데이터 파일 쓰기(XL1). 폴더 하나에 데이터 파일 <see cref="DataFileName"/>(UTF-8, BOM 없음), LOB 파일(<see cref="LobFolderName"/>),
/// 키 곁 파일 <see cref="KeysFileName"/> 을 만든다.
/// <list type="bullet">
/// <item>필드는 <c>,</c> 로 나누고, NULL 이 아닌 필드는 모두 <c>"</c> 로 감싼다(값 안의 <c>"</c> 는 두 번). NULL = 감싸지 않은 빈 필드.</item>
/// <item>레코드 끝 = 두 바이트 0x1E 0x0A(제어 파일 <c>"str X'1E0A'"</c>). 값 안의 CR·LF 는 그대로 둔다. 글자 값에 U+001E 가 있으면 그 행은 실패한다.</item>
/// <item>LOB 열(CLOB·NCLOB·BLOB)의 NULL 아닌 값은 파일 하나씩 <c>lob/&lt;열 순번(1부터)&gt;/&lt;레코드 번호&gt;.bin</c>(CLOB = UTF-8 바이트, BLOB = 바이트 그대로)에 적고,
/// 필드에는 그 상대 경로를 적는다. 상대 경로라 도구는 이 폴더를 작업 폴더로 실행해야 한다(<see cref="SqlLoaderRunSpec.WorkingDirectory"/>).</item>
/// <item>키 곁 파일: 적은 레코드마다 한 줄, 키 글자를 탭으로 나눠 이스케이프해 적는다(<c>\\</c>·<c>\n</c>·<c>\r</c>·<c>\t</c>, NULL = <c>\N</c>, 빈 글자 = <c>\E</c>).
/// 도구가 거절한 레코드 번호로 <see cref="ReadKeys(IReadOnlySet{long})"/> 가 실패 행의 키를 되살린다.</item>
/// </list>
/// 값은 원본 코덱의 <c>ToPortable</c> 결과(이식 값)다. 같은 DBMS 복사·교차 복사 모두 같다(<see cref="LoaderValueText"/>).
/// 사용 순서: <see cref="TryWrite"/> 반복 → <see cref="Complete"/> → 도구 실행 → (거절이 있으면) <see cref="ReadKeys(IReadOnlySet{long})"/>. 디스크 오류는 예외로 낸다.
/// </summary>
public sealed class LoaderDataWriter : IDisposable
{
    public const string DataFileName = "data.dat";

    public const string KeysFileName = "keys.txt";

    public const string LobFolderName = "lob";

    /// <summary>SQL*Loader 형식의 레코드 끝 글자(U+001E 다음 LF). UTF-8 로 0x1E 0x0A.</summary>
    public const string RecordTerminator = "\u001E\n";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IReadOnlyList<LoaderColumn> _columns;
    private readonly LoaderFieldType[] _types;
    private readonly string?[] _fields;
    private readonly object?[] _lobs;
    private readonly bool[] _lobFolderMade;
    private readonly StringBuilder _record = new();
    private readonly StreamWriter _data;
    private readonly StreamWriter _keys;
    private bool _completed;

    /// <summary>
    /// 폴더(없으면 만든다)에 새 데이터 파일·키 파일을 연다(있던 파일은 덮어쓴다). 열 형식이 외부 도구가 다루지 않는 형식이면 <see cref="ArgumentException"/>
    /// (사전 점검이 <see cref="LoaderValueText.Supports"/> 로 걸러야 한다).
    /// </summary>
    public LoaderDataWriter(string directory, IReadOnlyList<LoaderColumn> columns)
        : this(directory, columns, LoaderFileFormat.SqlLoader)
    {
    }

    /// <summary><paramref name="format"/> 형식으로 쓴다(tbLoader = <see cref="LoaderFileFormat.TbLoader"/>).</summary>
    public LoaderDataWriter(string directory, IReadOnlyList<LoaderColumn> columns, LoaderFileFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(format);
        if (string.IsNullOrEmpty(format.RecordTerminator) || string.IsNullOrEmpty(format.FieldSeparator))
        {
            throw new ArgumentException("필드 구분·레코드 끝 글자가 비었습니다.", nameof(format));
        }

        Format = format;
        if (columns.Count == 0)
        {
            throw new ArgumentException("적재할 열이 없습니다.", nameof(columns));
        }

        _columns = columns;
        _types = new LoaderFieldType[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            if (!LoaderFieldType.TryParse(columns[i].TargetType, out _types[i]))
            {
                throw new ArgumentException(LoaderValueText.UnsupportedType(columns[i].Name, columns[i].TargetType), nameof(columns));
            }
        }

        _fields = new string?[columns.Count];
        _lobs = new object?[columns.Count];
        _lobFolderMade = new bool[columns.Count];
        Directory = Path.GetFullPath(directory);
        System.IO.Directory.CreateDirectory(Directory);
        DataFile = Path.Combine(Directory, DataFileName);
        KeysFile = Path.Combine(Directory, KeysFileName);
        _data = Open(DataFile);
        try
        {
            _keys = Open(KeysFile);
        }
        catch
        {
            _data.Dispose();
            throw;
        }
    }

    /// <summary>데이터 파일 글 형식.</summary>
    public LoaderFileFormat Format { get; }

    /// <summary>쓰는 폴더(전체 경로).</summary>
    public string Directory { get; }

    /// <summary>데이터 파일 전체 경로.</summary>
    public string DataFile { get; }

    /// <summary>키 곁 파일 전체 경로.</summary>
    public string KeysFile { get; }

    /// <summary>적은 레코드 수(= 마지막 레코드 번호, 1부터).</summary>
    public long Records { get; private set; }

    /// <summary>적은 LOB 파일 수.</summary>
    public long LobFiles { get; private set; }

    /// <summary>
    /// 한 행을 적는다. <paramref name="portable"/> = 열 순서대로 이식 값(null = NULL), <paramref name="keyTexts"/> = 실패 행을 알릴 키 글자(열 수와 무관).
    /// 값을 적을 수 없으면 아무것도 적지 않고 false 와 고정된 오류 글(열·형식만, 값 없음). 디스크 오류는 예외.
    /// </summary>
    public bool TryWrite(IReadOnlyList<object?> portable, IReadOnlyList<string?> keyTexts, out string? error)
    {
        ArgumentNullException.ThrowIfNull(portable);
        ArgumentNullException.ThrowIfNull(keyTexts);
        ObjectDisposedException.ThrowIf(_completed, this);
        if (portable.Count != _columns.Count)
        {
            throw new ArgumentException($"값 수({portable.Count})가 열 수({_columns.Count})와 다릅니다.", nameof(portable));
        }

        // 1) 모든 값을 먼저 바꾸고 검사한다 — 실패하면 파일에 아무것도 남기지 않는다.
        for (var i = 0; i < _columns.Count; i++)
        {
            _fields[i] = null;
            _lobs[i] = null;
            var column = _columns[i];
            var value = portable[i];
            if (value is null or DBNull)
            {
                continue;
            }

            if (_types[i].Kind == LoaderFieldKind.Blob)
            {
                if (value is not byte[])
                {
                    error = LoaderValueText.Mismatch(column.Name, column.TargetType, value);
                    return false;
                }

                _lobs[i] = value;
                continue;
            }

            if (!LoaderValueText.TryFormat(value, column.Name, column.TargetType, _types[i], out var text, out error))
            {
                return false;
            }

            if (_types[i].Kind == LoaderFieldKind.Clob)
            {
                _lobs[i] = text;
            }
            else if (text is not null && text.Contains(Format.RecordTerminator[0], StringComparison.Ordinal))
            {
                error = string.Create(
                    CultureInfo.InvariantCulture,
                    $"열 {column.Name}({column.TargetType}): 값에 레코드 구분 문자(U+{(int)Format.RecordTerminator[0]:X4})가 있어 외부 도구로 적재할 수 없습니다.");
                return false;
            }
            else
            {
                _fields[i] = text;
            }
        }

        // 2) LOB 파일을 적고 필드에 상대 경로를 둔다.
        var record = Records + 1;
        for (var i = 0; i < _columns.Count; i++)
        {
            if (_lobs[i] is { } lob)
            {
                _fields[i] = WriteLob(i, record, lob);
            }
        }

        // 3) 레코드 한 줄.
        _record.Clear();
        for (var i = 0; i < _columns.Count; i++)
        {
            if (i > 0)
            {
                _record.Append(Format.FieldSeparator);
            }

            if (_fields[i] is { } field)
            {
                Format.AppendEnclosed(_record, field);
            }
        }

        _record.Append(Format.RecordTerminator);
        _data.Write(_record);

        _record.Clear();
        for (var k = 0; k < keyTexts.Count; k++)
        {
            if (k > 0)
            {
                _record.Append('\t');
            }

            AppendEscapedKey(_record, keyTexts[k]);
        }

        _record.Append('\n');
        _keys.Write(_record);

        Records = record;
        error = null;
        return true;
    }

    /// <summary>파일을 비우고 닫는다(도구를 띄우기 전에 부른다). 여러 번 불러도 된다.</summary>
    public void Complete()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        try
        {
            _data.Flush();
            _keys.Flush();
        }
        finally
        {
            _data.Dispose();
            _keys.Dispose();
        }
    }

    /// <summary>레코드 번호(1부터) → 그 레코드의 키 글자. 키 파일에 없는 번호는 빠진다. 닫지 않았으면 먼저 비운다.</summary>
    public IReadOnlyDictionary<long, IReadOnlyList<string?>> ReadKeys(IReadOnlySet<long> records)
    {
        if (!_completed)
        {
            _keys.Flush();
        }

        return ReadKeys(Directory, records);
    }

    /// <summary><see cref="ReadKeys(IReadOnlySet{long})"/> 의 정적 판(쓰기 객체를 닫은 뒤에도 쓸 수 있다).</summary>
    public static IReadOnlyDictionary<long, IReadOnlyList<string?>> ReadKeys(string directory, IReadOnlySet<long> records)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(records);
        var result = new Dictionary<long, IReadOnlyList<string?>>();
        if (records.Count == 0)
        {
            return result;
        }

        var last = records.Max();
        using var stream = new FileStream(Path.Combine(directory, KeysFileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Utf8);
        long number = 0;
        while (number < last && reader.ReadLine() is { } line)
        {
            number++;
            if (records.Contains(number))
            {
                result[number] = ParseKeyLine(line);
            }
        }

        return result;
    }

    public void Dispose() => Complete();

    /// <summary>LOB 파일의 상대 경로(이 폴더 기준).</summary>
    public static string LobRelativePath(int columnIndex, long record) =>
        Path.Combine(LobFolderName, (columnIndex + 1).ToString(CultureInfo.InvariantCulture), record.ToString(CultureInfo.InvariantCulture) + ".bin");

    internal static void AppendEscapedKey(StringBuilder builder, string? key)
    {
        if (key is null)
        {
            builder.Append("\\N");
            return;
        }

        if (key.Length == 0)
        {
            // 빈 글자 키를 키 없는 줄(빈 줄)과 구분한다.
            builder.Append("\\E");
            return;
        }

        foreach (var c in key)
        {
            switch (c)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }
    }

    internal static IReadOnlyList<string?> ParseKeyLine(string line)
    {
        if (line.Length == 0)
        {
            // 키 글자가 없는 줄(키 없이 적은 행).
            return [];
        }

        var keys = new List<string?>();
        var current = new StringBuilder();
        var isNull = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\t')
            {
                keys.Add(isNull ? null : current.ToString());
                current.Clear();
                isNull = false;
            }
            else if (c == '\\' && i + 1 < line.Length)
            {
                var next = line[++i];
                switch (next)
                {
                    case 'n':
                        current.Append('\n');
                        break;
                    case 'r':
                        current.Append('\r');
                        break;
                    case 't':
                        current.Append('\t');
                        break;
                    case 'N':
                        isNull = true;
                        break;
                    case 'E':
                        break;
                    default:
                        current.Append(next);
                        break;
                }
            }
            else
            {
                current.Append(c);
            }
        }

        keys.Add(isNull ? null : current.ToString());
        return keys;
    }

    private static StreamWriter Open(string path)
    {
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete, 1 << 16);
        return new StreamWriter(stream, Utf8, 1 << 16) { NewLine = "\n" };
    }

    private string WriteLob(int columnIndex, long record, object lob)
    {
        if (!_lobFolderMade[columnIndex])
        {
            System.IO.Directory.CreateDirectory(Path.Combine(Directory, LobFolderName, (columnIndex + 1).ToString(CultureInfo.InvariantCulture)));
            _lobFolderMade[columnIndex] = true;
        }

        var relative = LobRelativePath(columnIndex, record);
        var bytes = lob as byte[] ?? Utf8.GetBytes((string)lob);
        File.WriteAllBytes(Path.Combine(Directory, relative), bytes);
        LobFiles++;
        return relative;
    }
}
