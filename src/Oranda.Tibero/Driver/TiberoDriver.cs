using System.Data.Common;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using Oranda.Core.Connection;

namespace Oranda.Tibero.Driver;

/// <summary>
/// Tibero 드라이버(Tibero.DataAccess)를 리플렉션으로 묶은 어댑터. 드라이버 형식을 아는 코드는 이 폴더뿐이다.
/// <see cref="Bind"/> 가 모든 멤버를 컴파일된 식으로 묶고(하나라도 없으면 <see cref="TiberoDriverBindException"/>),
/// 그 뒤로는 바뀌지 않아 여러 스레드에서 써도 된다. 설계 = docs/design/tibero-driver-adapter.md 2장.
/// </summary>
internal sealed partial class TiberoDriver
{
    internal const string NotSpecifiedMessage =
        "Tibero 드라이버가 지정되지 않았습니다. 설정 > 드라이버에서 TDP.NET Core(Tibero.DataAccess.dll) 위치를 지정하세요.";

    private const string ClientNs = "Tibero.DataAccess.Client.";

    private const string TypesNs = "Tibero.DataAccess.Types.";

    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    private readonly Assembly _assembly;

    private readonly Type _commandType;

    private readonly Type _parameterType;

    private readonly Type _readerType;

    private readonly Type _exceptionType;

    private readonly Type _odpExceptionType;

    private readonly Type _nullableType;

    private readonly Action<DbCommand, bool> _setBindByName;

    private readonly Action<DbCommand, int> _setInitialLobFetchSize;

    private readonly Action<DbCommand, int> _setArrayBindCount;

    private readonly Action<DbParameter, object> _setDbType;

    private readonly Action<DbParameter, object> _setCollection;

    private readonly Dictionary<TiberoParamKind, object> _paramValues = [];

    private readonly Dictionary<TiberoCollectionKind, object> _collectionValues = [];

    private readonly Dictionary<int, TiberoParamKind> _kindByProviderType = [];

    private readonly Func<int, int, int, int, int, int, int, object> _newTimeStamp;

    private readonly Func<DateTime, string, object> _newTimeStampTz;

    private readonly Func<DbDataReader, int, byte[]?> _readRaw;

    private readonly Func<DbDataReader, int, Stream?> _readClob;

    private readonly Func<DbDataReader, int, Stream?> _readBlob;

    private readonly Func<DbDataReader, int, DateTime?> _readLtz;

    private readonly Func<Stream, bool> _isNClob;

    private readonly Func<Stream, char[], int, int, int> _readChars;

    private readonly Func<Stream, Stream> _cloneClob;

    private readonly Func<Stream, Stream> _cloneBlob;

    private readonly Func<object, bool> _isNullable;

    private readonly Func<Exception, int> _serverNumber;

    private readonly Func<Exception, int> _clientCode;

    private readonly List<ValueEntry> _entries = [];

    private TiberoDriver(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        _assembly = assembly;
        var name = assembly.GetName();
        Version = name.Version ?? new Version();
        Location = assembly.Location;

        var b = new Binder(assembly);

        // 1) 형식·멤버를 찾는다. 없는 것은 모두 모은다.
        var tFactory = b.Find(ClientNs + "TiberoClientFactory", true);
        var fInstance = b.StaticField(tFactory, "Instance");
        b.Find(ClientNs + "TiberoConnection", true);
        b.Find(ClientNs + "TiberoConnectionStringBuilder", true);
        var tCommand = b.Find(ClientNs + "TiberoCommand", true);
        var pBindByName = b.Prop(tCommand, "BindByName", typeof(bool), write: true);
        var pLobSize = b.Prop(tCommand, "InitialLOBFetchSize", typeof(int), write: true);
        var pArrayCount = b.Prop(tCommand, "ArrayBindCount", typeof(int), write: true);
        var tParameter = b.Find(ClientNs + "TiberoParameter", true);
        var tDbType = b.Find(ClientNs + "TiberoDbType", true);
        var tCollType = b.Find(ClientNs + "TiberoCollectionType", true);
        var pDbType = b.Prop(tParameter, "TiberoDbType", null, write: true);
        var pCollType = b.Prop(tParameter, "CollectionType", null, write: true);
        var tReader = b.Find(ClientNs + "TiberoDataReader", true);
        var mBinary = b.Method(tReader, "GetTiberoBinary", typeof(int));
        var mClob = b.Method(tReader, "GetTiberoClob", typeof(int));
        var mBlob = b.Method(tReader, "GetTiberoBlob", typeof(int));
        var mLtz = b.Method(tReader, "GetTiberoTimeStampLTZ", typeof(int));
        var tException = b.Find(ClientNs + "TiberoException", true);
        var pNumber = b.Prop(tException, "Number", typeof(int));
        var tOdp = b.Find("tbcomm.err.TbOdpException", true);
        var pErrorCode = b.Prop(tOdp, "ErrorCode", typeof(int));

        var tNullable = b.Find(TypesNs + "INullable", true);
        var pNullableIsNull = b.Prop(tNullable, "IsNull", typeof(bool));
        var tString = b.Find(TypesNs + "TiberoString", true);
        var pStringValue = b.Prop(tString, "Value", typeof(string));
        var pStringNull = b.Prop(tString, "IsNull", typeof(bool));
        var tDecimal = b.Find(TypesNs + "TiberoDecimal", true);
        var pDecimalNull = b.Prop(tDecimal, "IsNull", typeof(bool));
        var tDate = b.Find(TypesNs + "TiberoDate", true);
        var pDateValue = b.Prop(tDate, "Value", typeof(DateTime));
        var pDateNull = b.Prop(tDate, "IsNull", typeof(bool));
        var tStamp = b.Find(TypesNs + "TiberoTimeStamp", true);
        var pStampValue = b.Prop(tStamp, "Value", typeof(DateTime));
        var pStampNano = b.Prop(tStamp, "Nanosecond", typeof(int));
        var pStampNull = b.Prop(tStamp, "IsNull", typeof(bool));
        var cStamp = b.Ctor(tStamp, typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int));
        var tLtz = b.Find(TypesNs + "TiberoTimeStampLTZ", true);
        var pLtzValue = b.Prop(tLtz, "Value", typeof(DateTime));
        var pLtzNano = b.Prop(tLtz, "Nanosecond", typeof(int));
        var pLtzNull = b.Prop(tLtz, "IsNull", typeof(bool));
        var tTz = b.Find(TypesNs + "TiberoTimeStampTZ", true);
        var cTz = b.Ctor(tTz, typeof(DateTime), typeof(string));
        var tBinary = b.Find(TypesNs + "TiberoBinary", true);
        var pBinaryNull = b.Prop(tBinary, "IsNull", typeof(bool));
        var pBinaryValue = b.Prop(tBinary, "Value", typeof(byte[]));
        var tClob = b.Find(TypesNs + "TiberoClob", true);
        var pClobNClob = b.Prop(tClob, "IsNClob", typeof(bool));
        var pClobNull = b.Prop(tClob, "IsNull", typeof(bool));
        var mClobRead = b.Method(tClob, "Read", typeof(char[]), typeof(int), typeof(int));
        var mClobClone = b.Method(tClob, "Clone");
        var tBlob = b.Find(TypesNs + "TiberoBlob", true);
        var pBlobNull = b.Prop(tBlob, "IsNull", typeof(bool));
        var mBlobClone = b.Method(tBlob, "Clone");
        var tCursor = b.Find(TypesNs + "TiberoRefCursor", true);
        var pCursorNull = b.Prop(tCursor, "IsNull", typeof(bool));
        var mCursorReader = b.Method(tCursor, "GetDataReader");

        var paramValues = new Dictionary<TiberoParamKind, object>();
        foreach (var kind in Enum.GetValues<TiberoParamKind>())
        {
            if (b.EnumValue(tDbType, kind.ToString()) is { } v)
            {
                paramValues[kind] = v;
            }
        }

        var collectionValues = new Dictionary<TiberoCollectionKind, object>();
        if (b.EnumValue(tCollType, "None") is { } none)
        {
            collectionValues[TiberoCollectionKind.None] = none;
        }

        if (b.EnumValue(tCollType, "PLSQLAssociativeArray") is { } assoc)
        {
            collectionValues[TiberoCollectionKind.AssociativeArray] = assoc;
        }

        var factory = b.FactoryInstance(fInstance);
        b.ThrowIfAny(Version.ToString(), Location);

        // 2) 모두 있으니 컴파일된 식으로 묶는다.
        try
        {
            _commandType = tCommand.Type;
            _parameterType = tParameter.Type;
            _readerType = tReader.Type;
            _exceptionType = tException.Type;
            _odpExceptionType = tOdp.Type;
            _nullableType = tNullable.Type;
            Factory = factory!;

            _setBindByName = CommandSetter<bool>(tCommand.Type, pBindByName);
            _setInitialLobFetchSize = CommandSetter<int>(tCommand.Type, pLobSize);
            _setArrayBindCount = CommandSetter<int>(tCommand.Type, pArrayCount);
            _setDbType = ParameterSetter(tParameter.Type, pDbType, tDbType.Type);
            _setCollection = ParameterSetter(tParameter.Type, pCollType, tCollType.Type);
            _paramValues = paramValues;
            _collectionValues = collectionValues;
            foreach (var (kind, boxed) in paramValues)
            {
                _kindByProviderType[Convert.ToInt32(boxed, CultureInfo.InvariantCulture)] = kind;
            }

            _newTimeStamp = BuildNewTimeStamp(cStamp);
            _newTimeStampTz = BuildNewTimeStampTz(cTz);
            _readRaw = BuildReadRaw(tReader.Type, mBinary, pBinaryNull, pBinaryValue);
            _readClob = BuildLobGetter(tReader.Type, mClob, tClob.Type, pClobNull);
            _readBlob = BuildLobGetter(tReader.Type, mBlob, tBlob.Type, pBlobNull);
            _readLtz = BuildReadLtz(tReader.Type, mLtz, tLtz.Type, pLtzNull, pLtzValue);
            _isNClob = BuildIsNClob(tClob.Type, pClobNClob);
            _readChars = BuildReadChars(tClob.Type, mClobRead);
            _cloneClob = BuildClone(tClob.Type, mClobClone);
            _cloneBlob = BuildClone(tBlob.Type, mBlobClone);
            _isNullable = BuildIsNullable(tNullable.Type, pNullableIsNull);
            _serverNumber = BuildExceptionProp(tException.Type, pNumber);
            _clientCode = BuildExceptionProp(tOdp.Type, pErrorCode);

            _entries.Add(new ValueEntry(tString.Type, TiberoValueKind.String, BoxedProp(tString.Type, pStringValue), null, BoolProp(tString.Type, pStringNull)));
            _entries.Add(new ValueEntry(tDecimal.Type, TiberoValueKind.Decimal, static o => InvariantText(o), null, BoolProp(tDecimal.Type, pDecimalNull)));
            _entries.Add(new ValueEntry(tDate.Type, TiberoValueKind.Date, BoxedProp(tDate.Type, pDateValue), null, BoolProp(tDate.Type, pDateNull)));

            var stampValue = BoxedProp(tStamp.Type, pStampValue);
            _entries.Add(new ValueEntry(tStamp.Type, TiberoValueKind.TimeStamp, o => ToSecond((DateTime)stampValue(o)!), IntProp(tStamp.Type, pStampNano), BoolProp(tStamp.Type, pStampNull)));
            var ltzValue = BoxedProp(tLtz.Type, pLtzValue);
            _entries.Add(new ValueEntry(tLtz.Type, TiberoValueKind.TimeStampLtz, o => ToSecond((DateTime)ltzValue(o)!), IntProp(tLtz.Type, pLtzNano), BoolProp(tLtz.Type, pLtzNull)));
            _entries.Add(new ValueEntry(tBinary.Type, TiberoValueKind.Binary, BoxedProp(tBinary.Type, pBinaryValue), null, BoolProp(tBinary.Type, pBinaryNull)));

            var cloneClob = _cloneClob;
            var cloneBlob = _cloneBlob;
            _entries.Add(new ValueEntry(tClob.Type, TiberoValueKind.Lob, o => MakeClob(cloneClob((Stream)o)), null, BoolProp(tClob.Type, pClobNull)));
            _entries.Add(new ValueEntry(tBlob.Type, TiberoValueKind.Lob, o => MakeBlob(cloneBlob((Stream)o)), null, BoolProp(tBlob.Type, pBlobNull)));
            _entries.Add(new ValueEntry(tCursor.Type, TiberoValueKind.RefCursor, BuildCursorReader(tCursor.Type, mCursorReader), null, BoolProp(tCursor.Type, pCursorNull)));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new TiberoDriverBindException(["(식 구성 실패: " + ex.Message + ")"], Version.ToString(), Location);
        }
    }

    /// <summary>드라이버 어셈블리의 버전.</summary>
    internal Version Version { get; }

    /// <summary>드라이버 어셈블리의 위치.</summary>
    internal string Location { get; }

    /// <summary>드라이버의 기본 형식 팩터리(<c>TiberoClientFactory.Instance</c>).</summary>
    internal DbProviderFactory Factory { get; }

    /// <summary>
    /// 지정한 위치의 드라이버를 (처음 한 번) 읽어 묶은 어댑터. 그 뒤에는 같은 인스턴스. 못 쓰면 <see cref="TiberoDriverUnavailableException"/>.
    /// </summary>
    internal static TiberoDriver Require(IDriverPathSource? paths) => TiberoDriverHost.Shared.Require(paths);

    /// <summary>
    /// 넘긴 어셈블리에서 모든 멤버를 묶는다. 하나라도 없거나, 드라이버를 읽다가 다른 예외(의존 어셈블리 없음·형식 읽기 실패·
    /// 드라이버 정적 초기화 실패 등)가 나도 <see cref="TiberoDriverBindException"/> 하나로 알린다.
    /// </summary>
    internal static TiberoDriver Bind(Assembly driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        try
        {
            var bound = new TiberoDriver(driver);
            lock (BoundGate)
            {
                Volatile.Write(ref s_bound, [.. s_bound, bound]);
            }

            return bound;
        }
        catch (TiberoDriverBindException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            string? version = null;
            string? location = null;
            try
            {
                version = driver.GetName().Version?.ToString();
                location = driver.Location;
            }
            catch (Exception inner) when (inner is not OutOfMemoryException)
            {
                // 이름도 못 읽는 어셈블리 — 버전·위치 없이 알린다.
            }

            throw new TiberoDriverBindException(["(드라이버 읽기 실패: " + ex.GetType().Name + ": " + ex.Message + ")"], version, location);
        }
    }

    // ---- 연결·명령 ----

    internal DbConnection CreateConnection(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        var connection = Factory.CreateConnection()
            ?? throw new InvalidOperationException("드라이버가 연결 객체를 만들지 못했습니다.");
        connection.ConnectionString = connectionString;
        return connection;
    }

    internal DbConnectionStringBuilder CreateConnectionStringBuilder() =>
        Factory.CreateConnectionStringBuilder()
            ?? throw new InvalidOperationException("드라이버가 연결 문자열 빌더를 만들지 못했습니다.");

    internal void SetBindByName(DbCommand cmd, bool value = true)
    {
        Check(_commandType, cmd, nameof(cmd));
        _setBindByName(cmd, value);
    }

    internal void SetInitialLobFetchSize(DbCommand cmd, int size)
    {
        Check(_commandType, cmd, nameof(cmd));
        _setInitialLobFetchSize(cmd, size);
    }

    internal void SetArrayBindCount(DbCommand cmd, int count)
    {
        Check(_commandType, cmd, nameof(cmd));
        _setArrayBindCount(cmd, count);
    }

    // ---- 매개변수 ----

    internal void SetParameterKind(DbParameter p, TiberoParamKind kind)
    {
        Check(_parameterType, p, nameof(p));
        if (!_paramValues.TryGetValue(kind, out var boxed))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "알 수 없는 매개변수 형식입니다.");
        }

        _setDbType(p, boxed);
    }

    /// <summary>스키마 표 <c>ProviderType</c>(= 드라이버 TiberoDbType 정수) 에 대응하는 형식. 모르는 값 = null.</summary>
    internal TiberoParamKind? KindOfProviderType(int providerType) =>
        _kindByProviderType.TryGetValue(providerType, out var kind) ? kind : null;

    internal void SetCollectionType(DbParameter p, TiberoCollectionKind kind)
    {
        Check(_parameterType, p, nameof(p));
        if (!_collectionValues.TryGetValue(kind, out var boxed))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "알 수 없는 컬렉션 종류입니다.");
        }

        _setCollection(p, boxed);
    }

    /// <summary><c>DbParameter.Value</c> 에 넣을 TIMESTAMP 값. <paramref name="value"/> 의 초 아래는 버리고 <paramref name="nanosecond"/> 를 쓴다.</summary>
    internal object CreateTimeStamp(DateTime value, int nanosecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nanosecond);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(nanosecond, 999_999_999);
        return _newTimeStamp(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, nanosecond);
    }

    /// <summary><c>DbParameter.Value</c> 에 넣을 TIMESTAMP WITH TIME ZONE 값. <paramref name="value"/> = 그 오프셋의 지역 시각, <paramref name="offset"/> = <c>±hh:mm</c>.</summary>
    internal object CreateTimeStampTz(DateTime value, string offset)
    {
        ArgumentNullException.ThrowIfNull(offset);
        if (!OffsetPattern().IsMatch(offset))
        {
            throw new ArgumentException("오프셋은 ±hh:mm 꼴이어야 합니다.", nameof(offset));
        }

        return _newTimeStampTz(value, offset);
    }

    // ---- 리더 ----

    /// <summary>서버 원시 바이트. NULL 이면 null.</summary>
    internal byte[]? ReadRawBytes(DbDataReader r, int i)
    {
        Check(_readerType, r, nameof(r));
        return _readRaw(r, i);
    }

    /// <summary>CLOB·NCLOB 로케이터(새 사본). NULL 이면 null. 받은 쪽이 Dispose 한다.</summary>
    internal TiberoLob? ReadClob(DbDataReader r, int i)
    {
        Check(_readerType, r, nameof(r));
        return _readClob(r, i) is { } s ? MakeClob(s) : null;
    }

    /// <summary>BLOB 로케이터(새 사본). NULL 이면 null. 받은 쪽이 Dispose 한다.</summary>
    internal TiberoLob? ReadBlob(DbDataReader r, int i)
    {
        Check(_readerType, r, nameof(r));
        return _readBlob(r, i) is { } s ? MakeBlob(s) : null;
    }

    /// <summary>TIMESTAMP WITH LOCAL TIME ZONE(클라이언트 PC 시간대). NULL 이면 null.</summary>
    internal DateTime? ReadTimeStampLtz(DbDataReader r, int i)
    {
        Check(_readerType, r, nameof(r));
        return _readLtz(r, i);
    }

    // ---- 공급자 값 ----

    /// <summary>null·<see cref="DBNull"/> 이거나 드라이버 값의 <c>IsNull</c>.</summary>
    internal bool IsProviderNull(object? value) =>
        value is null || value is DBNull || (_nullableType.IsInstanceOfType(value) && _isNullable(value));

    /// <summary>공급자 값을 어댑터 값으로 바꾼다. <c>Value</c> 에 드라이버 형식 객체를 넣지 않는다.</summary>
    internal TiberoOutValue ConvertProviderValue(object? value)
    {
        if (value is null || value is DBNull)
        {
            return new TiberoOutValue(TiberoValueKind.Null, null, true, null);
        }

        var type = value.GetType();
        if (type.Assembly != _assembly)
        {
            return value is sbyte[] signed
                ? new TiberoOutValue(TiberoValueKind.Clr, null, false, ToUnsigned(signed))
                : new TiberoOutValue(TiberoValueKind.Clr, null, false, value);
        }

        var name = type.Name;
        foreach (var entry in _entries)
        {
            if (!entry.Type.IsAssignableFrom(type))
            {
                continue;
            }

            if (entry.IsNull(value))
            {
                return new TiberoOutValue(TiberoValueKind.Null, name, true, null);
            }

            return new TiberoOutValue(entry.Kind, name, false, entry.Value(value), entry.Nanosecond?.Invoke(value) ?? 0);
        }

        if (_nullableType.IsInstanceOfType(value) && _isNullable(value))
        {
            return new TiberoOutValue(TiberoValueKind.Null, name, true, null);
        }

        return new TiberoOutValue(TiberoValueKind.Other, name, false, InvariantText(value));
    }

    internal TiberoOutValue ReadOutValue(DbParameter p)
    {
        Check(_parameterType, p, nameof(p));
        return ConvertProviderValue(p.Value);
    }

    // ---- 예외 ----

    /// <summary>이 예외 자체만 본다(안쪽은 호출자가 훑는다).</summary>
    internal bool TryReadError(Exception ex, out TiberoErrorInfo error)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (_exceptionType.IsInstanceOfType(ex))
        {
            error = new TiberoErrorInfo(_serverNumber(ex), ex.Message, TiberoErrorOrigin.Server);
            return true;
        }

        if (_odpExceptionType.IsInstanceOfType(ex))
        {
            error = new TiberoErrorInfo(_clientCode(ex), ex.Message, TiberoErrorOrigin.Client);
            return true;
        }

        error = default;
        return false;
    }

    /// <summary>
    /// 이 프로세스에서 묶인 모든 어댑터(앱 = <see cref="TiberoDriverHost.Shared"/> 하나, 시험 = 가짜도)에 <see cref="TryReadError"/> 를 묻는다.
    /// 어댑터를 받지 않는 정적 오류 분류(<c>TiberoErrorClassifier</c>)가 쓴다. 아무것도 묶이지 않았으면 드라이버 예외도 있을 수 없어 false.
    /// </summary>
    internal static bool TryReadAnyError(Exception ex, out TiberoErrorInfo error)
    {
        ArgumentNullException.ThrowIfNull(ex);
        foreach (var driver in Volatile.Read(ref s_bound))
        {
            if (driver.TryReadError(ex, out error))
            {
                return true;
            }
        }

        error = default;
        return false;
    }

    // ---- 내부 ----

    private static readonly Lock BoundGate = new();

    // 묶을 때만 바뀐다(드물다). 읽기는 잠금 없이 복사본을 본다.
    private static TiberoDriver[] s_bound = [];

    private TiberoLob MakeClob(Stream s) =>
        new(_isNClob(s) ? TiberoLobKind.NClob : TiberoLobKind.Clob, s, _cloneClob, _readChars);

    private TiberoLob MakeBlob(Stream s) => new(TiberoLobKind.Blob, s, _cloneBlob, null);

    private static void Check(Type expected, object? arg, string paramName)
    {
        ArgumentNullException.ThrowIfNull(arg, paramName);
        if (!expected.IsInstanceOfType(arg))
        {
            throw new ArgumentException($"{paramName} 은(는) 이 어댑터의 드라이버 {expected.Name} 이 아닙니다.", paramName);
        }
    }

    private static byte[] ToUnsigned(sbyte[] source)
    {
        var copy = new byte[source.Length];
        Buffer.BlockCopy(source, 0, copy, 0, source.Length);
        return copy;
    }

    private static DateTime ToSecond(DateTime value) => value.AddTicks(-(value.Ticks % TimeSpan.TicksPerSecond));

    private static string InvariantText(object value)
    {
        if (value is IFormattable formattable)
        {
            return formattable.ToString(null, CultureInfo.InvariantCulture);
        }

        // ToString() 이 현재 문화권을 쓰는 형식(드라이버 값 형식 대부분)은 잠깐 invariant 로 바꿔 부른다.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            return value.ToString() ?? string.Empty;
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [GeneratedRegex(@"^[+-]([01]\d|2[0-3]):[0-5]\d$", RegexOptions.CultureInvariant)]
    private static partial Regex OffsetPattern();

    // ---- 식 만들기 ----

    private static Action<DbCommand, T> CommandSetter<T>(Type commandType, PropertyInfo property)
    {
        var c = Expression.Parameter(typeof(DbCommand), "c");
        var v = Expression.Parameter(typeof(T), "v");
        var body = Expression.Assign(Expression.Property(Expression.Convert(c, commandType), property), v);
        return Expression.Lambda<Action<DbCommand, T>>(body, c, v).Compile();
    }

    private static Action<DbParameter, object> ParameterSetter(Type parameterType, PropertyInfo property, Type enumType)
    {
        var p = Expression.Parameter(typeof(DbParameter), "p");
        var v = Expression.Parameter(typeof(object), "v");
        var body = Expression.Assign(
            Expression.Property(Expression.Convert(p, parameterType), property),
            Expression.Convert(v, enumType));
        return Expression.Lambda<Action<DbParameter, object>>(body, p, v).Compile();
    }

    private static Func<int, int, int, int, int, int, int, object> BuildNewTimeStamp(ConstructorInfo ctor)
    {
        var ps = Enumerable.Range(0, 7).Select(n => Expression.Parameter(typeof(int), "a" + n)).ToArray();
        var body = Expression.Convert(Expression.New(ctor, ps), typeof(object));
        return Expression.Lambda<Func<int, int, int, int, int, int, int, object>>(body, ps).Compile();
    }

    private static Func<DateTime, string, object> BuildNewTimeStampTz(ConstructorInfo ctor)
    {
        var d = Expression.Parameter(typeof(DateTime), "d");
        var s = Expression.Parameter(typeof(string), "s");
        var body = Expression.Convert(Expression.New(ctor, d, s), typeof(object));
        return Expression.Lambda<Func<DateTime, string, object>>(body, d, s).Compile();
    }

    private static Func<DbDataReader, int, byte[]?> BuildReadRaw(Type readerType, MethodInfo getter, PropertyInfo isNull, PropertyInfo value)
    {
        var r = Expression.Parameter(typeof(DbDataReader), "r");
        var i = Expression.Parameter(typeof(int), "i");
        var tmp = Expression.Variable(getter.ReturnType, "b");
        var body = Expression.Block(
            [tmp],
            Expression.Assign(tmp, Expression.Call(Expression.Convert(r, readerType), getter, i)),
            Expression.Condition(
                Expression.Property(tmp, isNull),
                Expression.Constant(null, typeof(byte[])),
                Expression.Property(tmp, value)));
        return Expression.Lambda<Func<DbDataReader, int, byte[]?>>(body, r, i).Compile();
    }

    private static Func<DbDataReader, int, Stream?> BuildLobGetter(Type readerType, MethodInfo getter, Type lobType, PropertyInfo isNull)
    {
        var r = Expression.Parameter(typeof(DbDataReader), "r");
        var i = Expression.Parameter(typeof(int), "i");
        var tmp = Expression.Variable(lobType, "l");
        var body = Expression.Block(
            [tmp],
            Expression.Assign(tmp, Expression.Convert(Expression.Call(Expression.Convert(r, readerType), getter, i), lobType)),
            Expression.Condition(
                Expression.OrElse(Expression.Equal(tmp, Expression.Constant(null, lobType)), Expression.Property(tmp, isNull)),
                Expression.Constant(null, typeof(Stream)),
                Expression.Convert(tmp, typeof(Stream))));
        return Expression.Lambda<Func<DbDataReader, int, Stream?>>(body, r, i).Compile();
    }

    private static Func<DbDataReader, int, DateTime?> BuildReadLtz(Type readerType, MethodInfo getter, Type ltzType, PropertyInfo isNull, PropertyInfo value)
    {
        var r = Expression.Parameter(typeof(DbDataReader), "r");
        var i = Expression.Parameter(typeof(int), "i");
        var tmp = Expression.Variable(ltzType, "t");
        var body = Expression.Block(
            [tmp],
            Expression.Assign(tmp, Expression.Convert(Expression.Call(Expression.Convert(r, readerType), getter, i), ltzType)),
            Expression.Condition(
                Expression.Property(tmp, isNull),
                Expression.Constant(null, typeof(DateTime?)),
                Expression.Convert(Expression.Property(tmp, value), typeof(DateTime?))));
        return Expression.Lambda<Func<DbDataReader, int, DateTime?>>(body, r, i).Compile();
    }

    private static Func<Stream, bool> BuildIsNClob(Type clobType, PropertyInfo isNClob)
    {
        var s = Expression.Parameter(typeof(Stream), "s");
        var body = Expression.Property(Expression.Convert(s, clobType), isNClob);
        return Expression.Lambda<Func<Stream, bool>>(body, s).Compile();
    }

    private static Func<Stream, char[], int, int, int> BuildReadChars(Type clobType, MethodInfo read)
    {
        var s = Expression.Parameter(typeof(Stream), "s");
        var buffer = Expression.Parameter(typeof(char[]), "buffer");
        var offset = Expression.Parameter(typeof(int), "offset");
        var count = Expression.Parameter(typeof(int), "count");
        var body = Expression.Call(Expression.Convert(s, clobType), read, buffer, offset, count);
        return Expression.Lambda<Func<Stream, char[], int, int, int>>(body, s, buffer, offset, count).Compile();
    }

    private static Func<Stream, Stream> BuildClone(Type lobType, MethodInfo clone)
    {
        var s = Expression.Parameter(typeof(Stream), "s");
        var body = Expression.Convert(Expression.Call(Expression.Convert(s, lobType), clone), typeof(Stream));
        return Expression.Lambda<Func<Stream, Stream>>(body, s).Compile();
    }

    private static Func<object, bool> BuildIsNullable(Type nullableType, PropertyInfo isNull)
    {
        var o = Expression.Parameter(typeof(object), "o");
        var body = Expression.Property(Expression.Convert(o, nullableType), isNull);
        return Expression.Lambda<Func<object, bool>>(body, o).Compile();
    }

    private static Func<Exception, int> BuildExceptionProp(Type exceptionType, PropertyInfo property)
    {
        var e = Expression.Parameter(typeof(Exception), "e");
        var body = Expression.Property(Expression.Convert(e, exceptionType), property);
        return Expression.Lambda<Func<Exception, int>>(body, e).Compile();
    }

    private static Func<object, object?> BoxedProp(Type owner, PropertyInfo property)
    {
        var o = Expression.Parameter(typeof(object), "o");
        var body = Expression.Convert(Expression.Property(Expression.Convert(o, owner), property), typeof(object));
        return Expression.Lambda<Func<object, object?>>(body, o).Compile();
    }

    private static Func<object, bool> BoolProp(Type owner, PropertyInfo property)
    {
        var o = Expression.Parameter(typeof(object), "o");
        var body = Expression.Property(Expression.Convert(o, owner), property);
        return Expression.Lambda<Func<object, bool>>(body, o).Compile();
    }

    private static Func<object, int> IntProp(Type owner, PropertyInfo property)
    {
        var o = Expression.Parameter(typeof(object), "o");
        var body = Expression.Property(Expression.Convert(o, owner), property);
        return Expression.Lambda<Func<object, int>>(body, o).Compile();
    }

    private static Func<object, object?> BuildCursorReader(Type cursorType, MethodInfo getReader)
    {
        var o = Expression.Parameter(typeof(object), "o");
        var body = Expression.Convert(Expression.Call(Expression.Convert(o, cursorType), getReader), typeof(DbDataReader));
        return Expression.Lambda<Func<object, object?>>(body, o).Compile();
    }

    /// <summary>드라이버 값 한 종류의 변환 방법.</summary>
    private sealed record ValueEntry(
        Type Type,
        TiberoValueKind Kind,
        Func<object, object?> Value,
        Func<object, int>? Nanosecond,
        Func<object, bool> IsNull);

    /// <summary>형식과 이름(없는 형식이어도 이름은 남긴다).</summary>
    private readonly record struct Found(Type Type, string Name, bool Exists);

    /// <summary>1단계 탐색 도우미. 없는 멤버는 모두 모으고 자리 채움 값을 돌려준다(2단계는 모두 있을 때만 돈다).</summary>
    private sealed class Binder(Assembly assembly)
    {
        private static readonly PropertyInfo DummyProp = typeof(string).GetProperty(nameof(string.Length))!;

        private static readonly MethodInfo DummyMethod = typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes)!;

        private static readonly ConstructorInfo DummyCtor = typeof(object).GetConstructor(Type.EmptyTypes)!;

        private static readonly FieldInfo DummyField = typeof(string).GetField(nameof(string.Empty))!;

        private readonly List<string> _missing = [];

        internal Found Find(string fullName, bool reportType)
        {
            var shortName = fullName[(fullName.LastIndexOf('.') + 1)..];
            var type = assembly.GetType(fullName, false);
            if (type is null)
            {
                if (reportType)
                {
                    Add(shortName);
                }

                return new Found(typeof(object), shortName, false);
            }

            return new Found(type, shortName, true);
        }

        internal PropertyInfo Prop(Found owner, string name, Type? valueType, bool write = false)
        {
            var property = owner.Exists
                ? owner.Type.GetProperties(PublicInstance).FirstOrDefault(p =>
                    p.Name == name && (valueType is null || p.PropertyType == valueType)
                    && (write ? p.GetSetMethod() is not null : p.GetGetMethod() is not null))
                : null;
            if (property is null)
            {
                Add(owner.Name + "." + name);
                return DummyProp;
            }

            return property;
        }

        internal MethodInfo Method(Found owner, string name, params Type[] parameters)
        {
            // 정확한 인자 형식만(넓히기 변환으로 다른 겹치기를 고르지 않게).
            var method = owner.Exists ? owner.Type.GetMethod(name, PublicInstance | BindingFlags.ExactBinding, null, parameters, null) : null;
            if (method is null)
            {
                Add(owner.Name + "." + name);
                return DummyMethod;
            }

            return method;
        }

        internal ConstructorInfo Ctor(Found owner, params Type[] parameters)
        {
            // TiberoTimeStamp 는 7인자 생성자가 (…, int nanosecond)·(…, double millisecond) 둘이다 — 정확히 맞는 것만.
            var ctor = owner.Exists ? owner.Type.GetConstructor(PublicInstance | BindingFlags.ExactBinding, null, parameters, null) : null;
            if (ctor is null)
            {
                Add(owner.Name + ".ctor");
                return DummyCtor;
            }

            return ctor;
        }

        internal FieldInfo StaticField(Found owner, string name)
        {
            var field = owner.Exists ? owner.Type.GetField(name, BindingFlags.Public | BindingFlags.Static) : null;
            if (field is null || !typeof(DbProviderFactory).IsAssignableFrom(field.FieldType))
            {
                Add(owner.Name + "." + name);
                return DummyField;
            }

            return field;
        }

        internal object? EnumValue(Found enumType, string name)
        {
            if (enumType.Exists && enumType.Type.IsEnum && Enum.TryParse(enumType.Type, name, false, out var value))
            {
                return value;
            }

            Add(enumType.Name + "." + name);
            return null;
        }

        internal DbProviderFactory? FactoryInstance(FieldInfo field)
        {
            if (_missing.Count > 0)
            {
                return null;
            }

            var factory = field.GetValue(null) as DbProviderFactory;
            if (factory is null)
            {
                Add(field.DeclaringType!.Name + "." + field.Name);
            }

            return factory;
        }

        internal void ThrowIfAny(string version, string location)
        {
            if (_missing.Count > 0)
            {
                throw new TiberoDriverBindException(_missing, version, location);
            }
        }

        private void Add(string item)
        {
            if (!_missing.Contains(item))
            {
                _missing.Add(item);
            }
        }
    }
}
