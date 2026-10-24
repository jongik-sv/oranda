using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Plans;
using Oranda.Core.Tuning;
using static Oranda.App.Features.Tuning.Stats.TuningText;

namespace Oranda.App.Features.Tuning.Cursors;

/// <summary>자식 커서 한 줄. 첫 칸 체크(<see cref="IsCompared"/>)로 비교할 두 자식을 고른다.</summary>
public sealed partial class ChildCursorItem(ChildCursorRow row, string sqlId) : ObservableObject
{
    public ChildCursorRow Row { get; } = row;

    public string SqlId { get; } = sqlId;

    [ObservableProperty]
    private bool _isCompared;

    /// <summary>실행당 gets 가 다른 자식보다 크게 나쁨(경고 글자).</summary>
    public bool GetsWorse { get; set; }

    /// <summary>실행당 경과가 다른 자식보다 크게 나쁨(경고 글자).</summary>
    public bool ElapsedWorse { get; set; }

    public CursorRef Cursor => new(SqlId, Row.ChildNumber, Row.InstanceId);

    /// <summary>「child 0 (1838229974)」.</summary>
    public string Label => string.Create(CultureInfo.InvariantCulture, $"child {Row.ChildNumber} ({Row.PlanHashValue})");

    public static string YesNo(bool value) => value ? "Y" : "N";

    /// <summary>실행당 경과(ms).</summary>
    public string ElapsedPerExecText => Row.ElapsedPerExecMicros is { } us
        ? (us / 1000).ToString(us >= 10_000 ? "N0" : "0.0", CultureInfo.InvariantCulture) + " ms"
        : string.Empty;

    public string GetsPerExecText => Row.GetsPerExec is { } g ? g.ToString(g >= 100 ? "N0" : "0.0", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>격자 칸(비교·인스턴스·child·plan_hash·바인드 민감·바인드 인식·공유 가능·실행·gets/실행·경과/실행). 첫 칸은 체크 상자가 그린다.</summary>
    public IReadOnlyList<object?> Cells =>
    [
        IsCompared ? "☑" : "☐",
        Row.InstanceId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        Row.ChildNumber.ToString(CultureInfo.InvariantCulture),
        Row.PlanHashValue.ToString(CultureInfo.InvariantCulture),
        YesNo(Row.IsBindSensitive),
        YesNo(Row.IsBindAware),
        YesNo(Row.IsShareable),
        Row.Executions.ToString("N0", CultureInfo.InvariantCulture),
        GetsPerExecText,
        ElapsedPerExecText,
    ];

    /// <summary>격자 머리글.</summary>
    public static IReadOnlyList<string> Headers =>
    [
        T("비교", "Compare"), T("인스턴스", "Instance"), "child", "plan_hash", T("바인드 민감", "Bind sensitive"), T("바인드 인식", "Bind aware"),
        T("공유 가능", "Shareable"), T("실행", "Executions"), T("gets/실행", "Gets/exec"), T("경과/실행", "Elapsed/exec"),
    ];

    /// <summary>실행당 값이 가장 좋은 자식보다 <paramref name="factor"/> 배 넘게 나쁘면 표시한다(자식이 둘 이상일 때).</summary>
    public static void MarkWorse(IReadOnlyList<ChildCursorItem> items, double factor = 5)
    {
        ArgumentNullException.ThrowIfNull(items);
        double? bestGets = null;
        double? bestElapsed = null;
        foreach (var i in items)
        {
            if (i.Row.GetsPerExec is { } g)
            {
                bestGets = bestGets is { } b ? Math.Min(b, g) : g;
            }

            if (i.Row.ElapsedPerExecMicros is { } e)
            {
                bestElapsed = bestElapsed is { } b ? Math.Min(b, e) : e;
            }
        }

        foreach (var i in items)
        {
            i.GetsWorse = items.Count > 1 && i.Row.GetsPerExec is { } g && bestGets is > 0 && g > bestGets.Value * factor;
            i.ElapsedWorse = items.Count > 1 && i.Row.ElapsedPerExecMicros is { } e && bestElapsed is > 0 && e > bestElapsed.Value * factor;
        }
    }
}

/// <summary>불일치 사유 한 줄(child·사유·뜻).</summary>
public sealed class CursorReasonItem(SharedCursorReason reason, string? meaning)
{
    public SharedCursorReason Reason { get; } = reason;

    /// <summary>뜻. 사전에 없으면 상세(12c+ XML) 첫 줄, 그것도 없으면 빈 글.</summary>
    public string Meaning { get; } = meaning ?? string.Empty;

    public IReadOnlyList<object?> Cells => [Reason.ChildNumber.ToString(CultureInfo.InvariantCulture), Reason.Reason, Meaning];

    public static IReadOnlyList<string> Headers => ["child", T("사유", "Reason"), T("뜻", "Meaning")];
}
