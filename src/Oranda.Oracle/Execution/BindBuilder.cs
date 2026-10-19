using System.Data;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Execution;

namespace Oranda.Oracle.Execution;

/// <summary><see cref="BindValue"/> → <see cref="OracleParameter"/>(EXEC-03). 이름으로 묶는다(BindByName).</summary>
internal static class BindBuilder
{
    /// <summary>OUT 문자열·RAW 의 기본 크기(PL/SQL 최대).</summary>
    public const int DefaultOutSize = 32767;

    /// <summary>
    /// 문장의 바인드 이름(<see cref="SqlStatement.BindNames"/>, 비었으면 <paramref name="binds"/> 의 키)마다 매개변수를 더한다.
    /// 값이 없는 이름이 있으면 <see cref="InvalidOperationException"/>.
    /// </summary>
    /// <returns>OUT·INOUT·반환값 매개변수(실행 뒤 값을 읽을 것)</returns>
    public static IReadOnlyList<(string Name, OracleParameter Parameter)> Build(OracleCommand cmd, SqlStatement stmt,
                                                                               IReadOnlyDictionary<string, BindValue> binds)
    {
        var lookup = new Dictionary<string, BindValue>(binds, StringComparer.OrdinalIgnoreCase);
        var names = stmt.BindNames.Count > 0 ? stmt.BindNames : [.. binds.Keys];
        var outs = new List<(string, OracleParameter)>();
        foreach (var name in names)
        {
            if (!lookup.TryGetValue(name, out var bind))
            {
                throw new InvalidOperationException($"바인드 변수 :{name} 의 값이 없습니다.");
            }

            var p = Create(name, bind);
            cmd.Parameters.Add(p);
            if (bind.Direction != BindDirection.In)
            {
                outs.Add((name, p));
            }
        }

        return outs;
    }

    internal static OracleParameter Create(string name, BindValue bind)
    {
        var p = new OracleParameter(name, ToDbType(bind.Type))
        {
            Direction = bind.Direction switch
            {
                BindDirection.Out => ParameterDirection.Output,
                BindDirection.InOut => ParameterDirection.InputOutput,
                BindDirection.ReturnValue => ParameterDirection.ReturnValue,
                _ => ParameterDirection.Input,
            },
            Value = bind.Direction == BindDirection.Out || bind.Value is null ? DBNull.Value : bind.Value,
        };

        if (bind.Size is { } size)
        {
            p.Size = size;
        }
        else if (bind.Direction != BindDirection.In
                 && bind.Type is BindDataType.Varchar2 or BindDataType.NVarchar2 or BindDataType.FixedChar or BindDataType.Raw)
        {
            p.Size = DefaultOutSize;
        }

        return p;
    }

    internal static OracleDbType ToDbType(BindDataType type) => type switch
    {
        BindDataType.Varchar2 => OracleDbType.Varchar2,
        BindDataType.NVarchar2 => OracleDbType.NVarchar2,
        BindDataType.FixedChar => OracleDbType.Char,
        BindDataType.Number => OracleDbType.Decimal,
        BindDataType.BinaryDouble => OracleDbType.BinaryDouble,
        BindDataType.Date => OracleDbType.Date,
        BindDataType.Timestamp => OracleDbType.TimeStamp,
        BindDataType.TimestampTz => OracleDbType.TimeStampTZ,
        BindDataType.Clob => OracleDbType.Clob,
        BindDataType.Blob => OracleDbType.Blob,
        BindDataType.Raw => OracleDbType.Raw,
        BindDataType.Boolean => OracleDbType.Boolean,
        // JSON 문자열은 19c(JSON 형식 없음)에서도 통하도록 CLOB 으로 보낸다.
        BindDataType.Json => OracleDbType.Clob,
        BindDataType.RefCursor => OracleDbType.RefCursor,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}
