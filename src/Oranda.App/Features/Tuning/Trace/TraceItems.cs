using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Oranda.Core.Tuning;

namespace Oranda.App.Features.Tuning.Trace;

/// <summary>읽은 트레이스 파일 하나(목록 한 줄).</summary>
/// <param name="Info">서버 경로·읽기 가능 여부</param>
/// <param name="Lines">읽은 줄(앞 <see cref="TraceViewerViewModel.MaxReadBytes"/> 까지가 기본, 「모두 읽기」면 전부)</param>
/// <param name="AllLines">서버가 돌려준 줄 전부</param>
/// <param name="ReadAt">읽은 시각(로컬)</param>
public sealed record TraceFileEntry(TraceFileInfo Info, IReadOnlyList<string> Lines, IReadOnlyList<string> AllLines, DateTime ReadAt)
{
    /// <summary>10053 로 연 파일(요약 대신 본문을 먼저 보인다).</summary>
    public bool Is10053 { get; init; }

    /// <summary>10053 사본 문장에 붙인 표시(본문에서 이 줄로 옮긴다).</summary>
    public string? Marker { get; init; }

    /// <summary>모든 줄의 크기(바이트, UTF-8 어림 = 글자 + 줄 끝).</summary>
    public long SizeBytes { get; } = AllLines.Sum(l => (long)l.Length + 1);

    public long ShownBytes { get; } = Lines.Sum(l => (long)l.Length + 1);

    public bool IsTruncated => Lines.Count < AllLines.Count;

    /// <summary>경로의 파일 이름.</summary>
    public string FileName
    {
        get
        {
            var p = Info.Path ?? string.Empty;
            var cut = Math.Max(p.LastIndexOf('/'), p.LastIndexOf('\\'));
            return cut >= 0 ? p[(cut + 1)..] : p;
        }
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1024 * 1024 => (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        >= 1024 => (bytes / 1024d).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B",
    };
}

/// <summary>TKPROF 식 요약 표 한 줄(문장).</summary>
public sealed class TraceStatementItem
{
    /// <summary>비중 막대 가장 긴 폭(DIP).</summary>
    public const double BarWidth = 60;

    public TraceStatementItem(int number, TraceStatement statement, long totalElapsed, bool isTop)
    {
        ArgumentNullException.ThrowIfNull(statement);
        Statement = statement;
        Number = number;
        SqlIdText = statement.SqlId ?? string.Empty;
        var flat = string.Join(' ', (statement.SqlText ?? string.Empty).Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        ShortText = flat.Length > 80 ? flat[..79] + "…" : flat;
        FullText = statement.SqlText ?? string.Empty;
        ExecutionsText = N(statement.ExecuteCount);
        CpuText = Seconds(statement.CpuMicros);
        ElapsedText = Seconds(statement.ElapsedMicros);
        DiskText = N(statement.Disk);
        QueryText = N(statement.Query);
        CurrentText = N(statement.Current);
        RowsText = N(statement.Rows);
        Share = totalElapsed <= 0 ? 0 : Math.Clamp((double)statement.ElapsedMicros / totalElapsed, 0, 1);
        ShareText = Share > 0 && Share < 0.01 ? "<1%" : (Share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        BarLength = Math.Round(Share * BarWidth, 1);
        IsTop = isTop;
        IsRecursive = statement.Depth > 0;
    }

    public TraceStatement Statement { get; }

    public int Number { get; }

    public string SqlIdText { get; }

    public string ShortText { get; }

    public string FullText { get; }

    public string ExecutionsText { get; }

    public string CpuText { get; }

    public string ElapsedText { get; }

    public string DiskText { get; }

    public string QueryText { get; }

    public string CurrentText { get; }

    public string RowsText { get; }

    /// <summary>파일 전체 경과 대비 비중(0~1).</summary>
    public double Share { get; }

    public string ShareText { get; }

    public double BarLength { get; }

    /// <summary>경과가 가장 큰 문장(주황 막대).</summary>
    public bool IsTop { get; }

    /// <summary>재귀 문장(dep&gt;0, 회색 막대).</summary>
    public bool IsRecursive { get; }

    /// <summary>나머지(파랑 막대).</summary>
    public bool IsNormal => !IsTop && !IsRecursive;

    public static string Seconds(long micros) => (micros / 1_000_000d).ToString("0.00", CultureInfo.InvariantCulture);

    public static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>대기 합계 한 줄. idle 대기(클라이언트 기다림 등)는 흐리게 그리고 합계에서 뺀다.</summary>
public sealed class TraceWaitItem
{
    /// <summary>세션이 일하지 않고 기다린 대기(합계에서 뺀다).</summary>
    public static readonly HashSet<string> IdleEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "SQL*Net message from client",
        "SQL*Net more data from client",
        "SQL*Net message from dblink",
        "PL/SQL lock timer",
        "pipe get",
        "rdbms ipc message",
        "jobq slave wait",
        "wait for unread message on broadcast channel",
        "Streams AQ: waiting for messages in the queue",
    };

    public TraceWaitItem(int number, TraceWaitTotal wait)
    {
        ArgumentNullException.ThrowIfNull(wait);
        Wait = wait;
        Number = number;
        IsIdle = IdleEvents.Contains(wait.Event);
        CountText = TraceStatementItem.N(wait.Count);
        AverageText = wait.Count > 0 ? (wait.ElapsedMicros / 1000d / wait.Count).ToString("0.00", CultureInfo.InvariantCulture) : string.Empty;
        TotalText = TraceStatementItem.Seconds(wait.ElapsedMicros);
    }

    public TraceWaitTotal Wait { get; }

    public int Number { get; }

    public string Event => Wait.Event;

    public bool IsIdle { get; }

    public string CountText { get; }

    /// <summary>한 번 평균(ms).</summary>
    public string AverageText { get; }

    /// <summary>합계(초).</summary>
    public string TotalText { get; }
}

/// <summary>본문 한 줄 종류(강조).</summary>
public enum TraceLineKind
{
    Normal,

    /// <summary>PARSING IN CURSOR(굵게).</summary>
    Parsing,

    /// <summary>WAIT(주황).</summary>
    Wait,

    /// <summary>idle WAIT(흐리게).</summary>
    IdleWait,

    /// <summary>STAT(실행 계획 행).</summary>
    Stat,
}

/// <summary>본문 한 줄(가상화된 목록이 그린다).</summary>
public sealed class TraceLineItem(int number, string text)
{
    public int Number { get; } = number;

    public string NumberText { get; } = number.ToString(CultureInfo.InvariantCulture);

    public string Text { get; } = text;

    public TraceLineKind Kind { get; } = Classify(text);

    public bool IsParsing => Kind == TraceLineKind.Parsing;

    public bool IsWait => Kind == TraceLineKind.Wait;

    public bool IsIdleWait => Kind == TraceLineKind.IdleWait;

    public static TraceLineKind Classify(string line)
    {
        if (line.StartsWith("PARSING IN CURSOR", StringComparison.Ordinal))
        {
            return TraceLineKind.Parsing;
        }

        if (line.StartsWith("WAIT #", StringComparison.Ordinal))
        {
            var at = line.IndexOf("nam='", StringComparison.Ordinal);
            var end = at < 0 ? -1 : line.IndexOf('\'', at + 5);
            var name = end > at ? line[(at + 5)..end] : string.Empty;
            return TraceWaitItem.IdleEvents.Contains(name) ? TraceLineKind.IdleWait : TraceLineKind.Wait;
        }

        return line.StartsWith("STAT #", StringComparison.Ordinal) ? TraceLineKind.Stat : TraceLineKind.Normal;
    }
}
