using Oranda.Core.Text;

namespace Oranda.App.Features.SqlEditor;

/// <summary>스크립트 실행 중 표시: 지금 실행하는 문장(또는 멈춘 오류 문장)의 문서 범위들. <paramref name="Failed"/> 면 오류로 멈춘 문장이다.</summary>
public sealed record RunningStatementMark(IReadOnlyList<TextRange> Ranges, bool Failed);

// SqlEditorViewModel 의 일부: 스크립트 실행 중 지금 문장 표시. 실행 컨트롤러가 정하고 View 가 옅은 배경으로 칠한다.
public sealed partial class SqlEditorViewModel
{
    /// <summary>지금 실행 중인(또는 오류로 멈춘) 문장 표시. 없으면 null. 글자가 바뀌면 지운다(범위가 어긋나므로).</summary>
    public RunningStatementMark? RunningStatement { get; private set; }

    /// <summary><see cref="RunningStatement"/> 가 바뀌었다는 알림(View 가 다시 칠한다).</summary>
    public event EventHandler? RunningStatementChanged;

    /// <summary>지금 문장 표시를 정한다. 범위는 문서 안으로 자르고 빈 범위는 뺀다. 남는 범위가 없으면 표시를 지운다.</summary>
    internal void SetRunningStatement(IReadOnlyList<TextRange> ranges, bool failed)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        var clipped = new List<TextRange>(ranges.Count);
        foreach (var r in ranges)
        {
            int start = Math.Clamp(r.Start, 0, Document.TextLength);
            int length = Math.Clamp(r.Length, 0, Document.TextLength - start);
            if (length > 0)
            {
                clipped.Add(new TextRange(start, length));
            }
        }

        if (clipped.Count == 0)
        {
            ClearRunningStatement();
            return;
        }

        RunningStatement = new RunningStatementMark(clipped, failed);
        RunningStatementChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>지금 문장 표시를 지운다. 표시가 없으면 아무것도 하지 않는다(키마다 불린다).</summary>
    internal void ClearRunningStatement()
    {
        if (RunningStatement is null)
        {
            return;
        }

        RunningStatement = null;
        RunningStatementChanged?.Invoke(this, EventArgs.Empty);
    }
}
