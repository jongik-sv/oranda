using System.Data.Common;
using Oranda.Core.Execution;
using Oranda.Tibero.Driver;

namespace Oranda.Tibero.Results;

/// <summary>실행 뒤 값을 읽을 매개변수(OUT·INOUT·반환값).</summary>
/// <param name="Name">바인드 이름(<c>:</c> 제외)</param>
/// <param name="Parameter">드라이버 매개변수</param>
/// <param name="Bind">요청한 바인드(형식에 맞게 값을 되돌릴 때 필요)</param>
internal readonly record struct TiberoOutBind(string Name, DbParameter Parameter, BindValue Bind);

/// <summary>
/// <see cref="BindValue"/> → 드라이버 매개변수(EXEC-03, R1b). 이름으로 묶는다(<c>BindByName</c>). 변환 계획은 드라이버 무관한
/// <see cref="TiberoBindPlan"/> 이 정하고, 이 클래스는 어댑터로 그 계획대로 매개변수를 만든다.
/// 규칙 근거 = docs/design/tibero-driver-values.md 6장(TIMESTAMP 나노초·TZ 오프셋 감싸기, OUT 불가 형식은 VARCHAR2 로 받기).
/// </summary>
internal static class TiberoBindBuilder
{
    /// <summary>
    /// 문장의 바인드 이름(<see cref="SqlStatement.BindNames"/>, 비었으면 <paramref name="binds"/> 의 키)마다 매개변수를 더한다.
    /// 값이 없는 이름이 있으면 <see cref="InvalidOperationException"/>.
    /// </summary>
    /// <returns>OUT·INOUT·반환값 매개변수(실행 뒤 <see cref="TiberoValues.ReadOut"/> 로 값을 읽을 것)</returns>
    public static IReadOnlyList<TiberoOutBind> Build(TiberoDriver driver, DbCommand cmd, SqlStatement stmt,
                                                     IReadOnlyDictionary<string, BindValue> binds)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(cmd);
        ArgumentNullException.ThrowIfNull(stmt);
        ArgumentNullException.ThrowIfNull(binds);
        var lookup = new Dictionary<string, BindValue>(binds, StringComparer.OrdinalIgnoreCase);
        var names = stmt.BindNames.Count > 0 ? stmt.BindNames : [.. binds.Keys];
        var outs = new List<TiberoOutBind>();
        driver.SetBindByName(cmd);
        foreach (var name in names)
        {
            if (!lookup.TryGetValue(name, out var bind))
            {
                throw new InvalidOperationException($"바인드 변수 :{name} 의 값이 없습니다.");
            }

            var p = Create(driver, cmd, name, bind);
            cmd.Parameters.Add(p);
            if (bind.Direction != BindDirection.In)
            {
                outs.Add(new TiberoOutBind(name, p, bind));
            }
        }

        return outs;
    }

    internal static DbParameter Create(TiberoDriver driver, DbCommand cmd, string name, BindValue bind)
    {
        var plan = TiberoBindPlan.For(bind);
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        driver.SetParameterKind(p, driver.KindOfProviderType(plan.ProviderType)
            ?? throw new NotSupportedException($"지원하지 않는 매개변수 형식입니다: {plan.ProviderType}"));
        p.Direction = plan.Direction;
        p.Value = Wrap(driver, plan);
        if (plan.Size is { } size)
        {
            p.Size = size;
        }

        return p;
    }

    private static object Wrap(TiberoDriver driver, TiberoBindPlan plan) => plan.Wrapper switch
    {
        TiberoBindWrapper.TimeStamp when plan.Value is DateTime dt =>
            driver.CreateTimeStamp(dt, (int)(dt.Ticks % TimeSpan.TicksPerSecond * 100)),
        TiberoBindWrapper.TimeStampTz when plan.Value is DateTimeOffset o && plan.TzOffset is { } tz =>
            driver.CreateTimeStampTz(o.DateTime, tz),
        _ => plan.Value,
    };
}
