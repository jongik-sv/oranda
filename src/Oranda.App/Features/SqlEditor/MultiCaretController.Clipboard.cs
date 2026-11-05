using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace Oranda.App.Features.SqlEditor;

// MultiCaretController 의 일부: 클립보드. AvaloniaEdit 의 복사는 글자만 넣고 붙여넣기는 열 블록 모양을 되살리지 않으며 추가 커서를 모른다.
public sealed partial class MultiCaretController
{
    /// <summary>열 블록으로 복사한 글에 붙이는 표지(응용 프로그램 형식). 플랫폼이 사용자 정의 형식을 남기지 못할 때를 위해 <see cref="s_lastBlock"/> 도 함께 쓴다.</summary>
    internal static readonly DataFormat<string> BlockFormat = DataFormat.CreateStringApplicationFormat("oranda-block-text");

    // 이 프로세스에서 마지막으로 열 블록으로 복사한 글. 클립보드 글이 이것과 같으면 블록으로 붙인다.
    private static string? s_lastBlock;

    /// <summary>복사·잘라내기를 이 컨트롤러가 맡는가(추가 커서가 있거나 열 블록 선택이 있다).</summary>
    public bool OwnsClipboard => HasExtra || _area.Selection is RectangleSelection { IsEmpty: false };

    private IClipboard? Clipboard => TopLevel.GetTopLevel(_area)?.Clipboard;

    /// <summary>
    /// 선택을 복사한다. 추가 커서가 있으면 선택을 가진 커서마다 글을 문서 순서로 줄바꿈으로 이어 복사하고(선택 없는 커서는 건너뛴다),
    /// 열 블록 선택이면 블록 표지를 붙여 복사한다. 복사할 선택이 없으면 아무것도 하지 않는다.
    /// </summary>
    public async void Copy() => await RunGuardedAsync(() => CopyOrCutAsync(cut: false)).ConfigureAwait(true);

    /// <summary>복사와 같되 복사한 선택을 지운다(한 번의 실행 취소 단계).</summary>
    public async void Cut() => await RunGuardedAsync(() => CopyOrCutAsync(cut: true)).ConfigureAwait(true);

    // async void 처리기에서 새어 나간 예외는 앱을 종료시키므로 로그만 남긴다.
    private static async Task RunGuardedAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Trace.TraceError("클립보드 처리 실패 ({0}): {1}", ex.GetType().Name, ex);
        }
    }

    /// <summary>복사·잘라내기. 글 계산과 문서 수정은 첫 <c>await</c> 앞에서 끝난다(클립보드 쓰기만 기다린다).</summary>
    internal Task CopyOrCutAsync(bool cut)
    {
        var doc = _editor.Document;
        string text;
        bool block = false;
        if (_area.Selection is RectangleSelection { IsEmpty: false } rs && !HasExtra)
        {
            text = rs.GetText();
            block = true;
            if (cut && !_area.IsReadOnly)
            {
                rs.ReplaceSelectionWithText(string.Empty);
            }
        }
        else
        {
            var picked = Slots().Where(s => !s.IsEmpty).ToList();
            if (picked.Count == 0)
            {
                return Task.CompletedTask;
            }

            text = string.Join(Environment.NewLine, picked.Select(s => doc.GetText(s.Start, s.Length)));
            if (cut && !_area.IsReadOnly)
            {
                EditAll((s, _) => s.IsEmpty ? null : new Edit(s.Start, s.Length, string.Empty));
            }
        }

        return SetClipboardAsync(text, block);
    }

    private async Task SetClipboardAsync(string text, bool block)
    {
        if (Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            var item = new DataTransferItem();
            item.SetText(text);
            if (block)
            {
                item.Set(BlockFormat, "1");
            }

            var data = new Avalonia.Input.DataTransfer();
            data.Add(item);
            s_lastBlock = block ? text : null;
            await clipboard.SetDataAsync(data).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Trace.TraceError("클립보드 쓰기 실패 ({0}): {1}", ex.GetType().Name, ex);
        }
    }

    /// <summary>
    /// 붙여넣기. 추가 커서나 열 블록 선택이 있거나 클립보드 글이 열 블록 복사본이면 이 컨트롤러가 붙이고, 아니면 <paramref name="fallback"/>(기본 붙여넣기)에 맡긴다.
    /// </summary>
    public async void Paste(Action fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        string? text = null;
        bool block = false;
        try
        {
            if (Clipboard is { } clipboard)
            {
                text = await clipboard.TryGetTextAsync().ConfigureAwait(true);
                // 표지를 먼저 본다: 글 비교가 먼저 참이 되면 표지 경로는 실행되지 않는다(다른 프로세스에서 복사한 블록은 표지로만 알 수 있다).
                block = text is not null && ((await clipboard.GetDataFormatsAsync().ConfigureAwait(true)).Contains(BlockFormat)
                    || text == s_lastBlock);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError("클립보드 읽기 실패 ({0}): {1}", ex.GetType().Name, ex);
        }

        if (string.IsNullOrEmpty(text) || !(OwnsClipboard || block))
        {
            fallback();
            return;
        }

        try
        {
            PasteText(text, block);
        }
        catch (Exception ex)
        {
            Trace.TraceError("붙여넣기 실패 ({0}): {1}", ex.GetType().Name, ex);
        }
    }

    /// <summary>
    /// 글을 붙인다. 추가 커서: 줄 수가 커서 수와 같으면 커서마다 한 줄씩, 다르면 전체를 각 커서에 넣는다.
    /// 열 블록 선택: 줄 수가 선택 줄 수와 같으면 줄마다 한 줄씩, 다르면 전체를 각 줄에 넣는다(블록 복사본이면 선택을 지우고 블록 모양으로 붙인다).
    /// 선택이 없고 블록 복사본이면 커서 자리에 블록 모양으로 붙인다.
    /// </summary>
    internal void PasteText(string text, bool block)
    {
        if (_area.IsReadOnly)
        {
            return;
        }

        var doc = _editor.Document;
        string newline = doc.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (HasExtra)
        {
            var parts = MultiCaretMath.DistributePaste(text, Slots().Count);
            EditAll((s, i) => new Edit(s.Start, s.Length, TextUtilities.NormalizeNewLines(parts[i], newline)));
            return;
        }

        if (_area.Selection is RectangleSelection { IsEmpty: false } rs)
        {
            if (block)
            {
                int line = Math.Min(rs.StartPosition.Line, rs.EndPosition.Line);
                int column = Math.Min(rs.StartPosition.Column, rs.EndPosition.Column);
                int visual = LeftVisualColumn(rs);
                doc.BeginUpdate(); // 블록을 지우고 붙이는 갱신을 한 번의 실행 취소 단계로 묶는다
                try
                {
                    rs.ReplaceSelectionWithText(string.Empty);
                    PasteBlock(new TextViewPosition(line, column, visual), text, newline);
                }
                finally
                {
                    doc.EndUpdate();
                }

                return;
            }

            // 블록 줄 수와 같은 줄 수의 글은 줄마다 한 줄씩, 아니면 전체를 모든 줄에 넣는다.
            var segments = rs.Segments.ToList();
            var each = MultiCaretMath.DistributePaste(text, segments.Count);
            _area.ClearSelection();
            doc.BeginUpdate();
            try
            {
                for (int i = segments.Count - 1; i >= 0; i--)
                {
                    doc.Replace(segments[i].StartOffset, segments[i].Length, TextUtilities.NormalizeNewLines(each[i], newline));
                }
            }
            finally
            {
                doc.EndUpdate();
            }

            int shift = 0;
            for (int i = 0; i < segments.Count - 1; i++)
            {
                shift += TextUtilities.NormalizeNewLines(each[i], newline).Length - segments[i].Length;
            }

            _area.Caret.Offset = segments[^1].StartOffset + shift + TextUtilities.NormalizeNewLines(each[^1], newline).Length;
            return;
        }

        if (block)
        {
            var position = _area.Caret.Position;
            if (_area.Selection.IsEmpty)
            {
                PasteBlock(position, text, newline);
                return;
            }

            // 일반 선택이 있으면 선택을 지우고 그 시작 자리에 붙인다(지우기와 붙이기는 한 번의 실행 취소 단계).
            int start = _area.Selection.SurroundingSegment.Offset;
            var begin = _area.Selection.StartPosition;
            var end = _area.Selection.EndPosition;
            var first = doc.GetOffset(begin.Location) <= doc.GetOffset(end.Location) ? begin : end;
            doc.BeginUpdate();
            try
            {
                _area.Selection.ReplaceSelectionWithText(string.Empty);
                var location = doc.GetLocation(start);
                int visual = first.VisualColumn >= 0 ? first.VisualColumn : location.Column - 1;
                PasteBlock(new TextViewPosition(location.Line, location.Column, visual), text, newline);
            }
            finally
            {
                doc.EndUpdate();
            }
        }
    }

    /// <summary>
    /// 블록 모양으로 붙인다. <c>PerformRectangularPaste</c> 는 끝 줄이 모자라면 아무것도 하지 않고 false 를 돌려주므로,
    /// 끝 줄을 먼저 만들고 채운 뒤 붙인다(줄 더하기·채우기와 붙이기는 한 실행 취소 단계).
    /// </summary>
    private void PasteBlock(TextViewPosition position, string text, string newline)
    {
        var doc = _editor.Document;
        doc.BeginUpdate(); // 줄 더하기와 붙이기를 한 실행 취소 단계로 묶는다
        try
        {
            // PerformRectangularPaste 는 끝 줄(시작 줄 + 글의 '\n' 개수)이 없거나 그 줄이 시작 열보다 짧으면(가상 공간이 꺼진 일반 선택·커서)
            // 아무것도 하지 않고 false 를 돌려준다. 끝 줄을 만들고 시작 열까지 공백으로 채워 둔다.
            int endLine = position.Line + text.Count(c => c == '\n');
            int missing = endLine - doc.LineCount;
            if (missing > 0)
            {
                doc.Insert(doc.TextLength, string.Concat(Enumerable.Repeat(newline, missing)));
            }

            var last = doc.GetLineByNumber(endLine);
            int shortBy = position.Column - 1 - last.Length;
            if (shortBy > 0)
            {
                doc.Insert(last.EndOffset, new string(' ', shortBy));
            }

            if (!RectangleSelection.PerformRectangularPaste(_area, position, text, true))
            {
                Trace.TraceWarning("블록 붙여넣기가 적용되지 않았다 (줄 {0}).", position.Line);
            }
        }
        finally
        {
            doc.EndUpdate();
        }
    }
}
