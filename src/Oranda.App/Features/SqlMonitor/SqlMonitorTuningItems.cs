using System;
using System.Globalization;
using Oranda.Core.Tuning;

namespace Oranda.App.Features.SqlMonitor;

/// <summary>대기 분류 색(아이콘 브러시) 표시용 묶음. 화면은 <c>Classes.cpu</c> 처럼 불 값으로 색 클래스를 단다.</summary>
public abstract class WaitToneItem(WaitTone tone)
{
    public WaitTone Tone { get; } = tone;

    public bool IsCpu => Tone == WaitTone.Cpu;

    public bool IsUserIo => Tone == WaitTone.UserIo;

    public bool IsApplication => Tone == WaitTone.Application;

    public bool IsCluster => Tone == WaitTone.Cluster;

    public bool IsConcurrency => Tone == WaitTone.Concurrency;

    /// <summary>「12 %」 꼴(1 % 미만은 「&lt;1 %」).</summary>
    public static string Percent(double share) => share switch
    {
        <= 0 => "0 %",
        < 0.01 => "<1 %",
        _ => Math.Round(share * 100).ToString("0", CultureInfo.InvariantCulture) + " %",
    };
}

/// <summary>대기 분해 표의 한 줄(이벤트).</summary>
public sealed class WaitEventItem(WaitEventShare share) : WaitToneItem(share.Tone)
{
    /// <summary>비율 막대의 최대 폭(DIP).</summary>
    public const double BarWidth = 120;

    public WaitEventShare Share { get; } = share;

    public string Event => Share.Event;

    public string WaitClass => Share.WaitClass;

    public string SamplesText => Share.Samples.ToString("N0", CultureInfo.InvariantCulture);

    public string ShareText => Percent(Share.Share);

    public double ShareBarWidth => Math.Round(Math.Clamp(Share.Share, 0, 1) * BarWidth, 1);

    /// <summary>세션 수(ASH 줄은 모름 = 「—」).</summary>
    public string SessionsText => Share.Sessions?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";

    /// <summary>행 잠금 대기(enq: TX·TM)면 Lock 모니터로 바로 가기를 보인다.</summary>
    public bool IsLockWait => Share.Tone == WaitTone.Application && Share.Event.StartsWith("enq: T", StringComparison.OrdinalIgnoreCase);

    /// <summary>바로 가기 단추를 실제로 보일지(Lock 모니터 명령이 등록돼 있을 때만). 화면이 채운다.</summary>
    public bool ShowLockLink { get; set; }

    public string Tip => $"{Share.Event} · {Share.WaitClass} · 표본 {SamplesText} · {ShareText}";
}

/// <summary>대기 분류 색 막대의 한 칸과 범례.</summary>
public sealed class WaitClassItem(WaitClassShare share) : WaitToneItem(share.Tone)
{
    public WaitClassShare Share { get; } = share;

    public string LegendText => $"{Share.Label} {Percent(Share.Share)}";
}

/// <summary>AWR 계획 이력 한 줄(snap × plan_hash).</summary>
public sealed class AwrRowItem(AwrPlanRow row, int planIndex)
{
    public AwrPlanRow Row { get; } = row;

    /// <summary>plan_hash 색 번호(처음 나온 순서, 그래프 점 색).</summary>
    public int PlanIndex { get; } = planIndex;

    /// <summary>실행당 경과(ms). 실행 0 이면 null.</summary>
    public double? MsPerExec => Row.Executions > 0 ? Row.ElapsedMicros / 1000d / Row.Executions : null;

    public string SnapText => Row.SnapTime.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

    public string PlanHashText => Row.PlanHashValue.ToString(CultureInfo.InvariantCulture);

    public string ExecutionsText => Row.Executions.ToString("N0", CultureInfo.InvariantCulture);

    public string MsPerExecText => Ms(MsPerExec);

    public string GetsPerExecText => Row.Executions > 0
        ? ((double)Row.BufferGets / Row.Executions).ToString("N0", CultureInfo.InvariantCulture)
        : "—";

    /// <summary>「1,842 ms」·「3.4 ms」·「0.25 ms」. 없으면 「—」.</summary>
    public static string Ms(double? ms) => ms switch
    {
        null => "—",
        >= 100 => ms.Value.ToString("N0", CultureInfo.InvariantCulture) + " ms",
        >= 1 => ms.Value.ToString("0.0", CultureInfo.InvariantCulture) + " ms",
        _ => ms.Value.ToString("0.00", CultureInfo.InvariantCulture) + " ms",
    };
}

/// <summary>AWR 범례 한 칸(plan_hash 하나).</summary>
public sealed class AwrPlanLegendItem(long planHash, int planIndex, double? averageMs, long executions)
{
    public long PlanHash { get; } = planHash;

    public int PlanIndex { get; } = planIndex;

    public double? AverageMs { get; } = averageMs;

    public long Executions { get; } = executions;

    public string Text => $"{PlanHash.ToString(CultureInfo.InvariantCulture)} · 평균 {AwrRowItem.Ms(AverageMs)}";

    // 점 색 클래스(그래프 색 순서 AwrPlanChart.PaletteKeys 와 같다).
    public bool IsP0 => PlanIndex % 6 == 0;

    public bool IsP1 => PlanIndex % 6 == 1;

    public bool IsP2 => PlanIndex % 6 == 2;

    public bool IsP3 => PlanIndex % 6 == 3;

    public bool IsP4 => PlanIndex % 6 == 4;

    public bool IsP5 => PlanIndex % 6 == 5;
}

/// <summary>계획 관리(Profile·Baseline·Patch) 한 줄.</summary>
public sealed class PlanControlItem(SqlPlanControlRow row)
{
    public SqlPlanControlRow Row { get; } = row;

    public string KindText => Row.Kind switch
    {
        SqlPlanControlKind.Profile => "SQL Profile",
        SqlPlanControlKind.Baseline => "SQL Plan Baseline",
        _ => "SQL Patch",
    };

    /// <summary>「ENABLED · ACCEPTED」·「ENABLED · 미승인」·「DISABLED」.</summary>
    public string StatusText => Row.Accepted switch
    {
        true => Row.Status + " · ACCEPTED",
        false => Row.Status + " · 미승인",
        null => Row.Status,
    };

    /// <summary>쓰이지 않는 줄(꺼짐·미승인)은 흐리게 또는 주의로.</summary>
    public bool IsDisabled => !string.Equals(Row.Status, "ENABLED", StringComparison.OrdinalIgnoreCase);

    public bool IsUnaccepted => Row.Accepted == false;

    public string CreatedText => Row.Created == DateTime.MinValue
        ? "—"
        : Row.Created.ToString(Row.Created.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>SQL 앞부분 한 줄(줄바꿈·연속 공백을 하나로).</summary>
    public string SqlPreview => Preview(Row.SqlText, 120);

    internal static string Preview(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var flat = string.Join(' ', text.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }
}
