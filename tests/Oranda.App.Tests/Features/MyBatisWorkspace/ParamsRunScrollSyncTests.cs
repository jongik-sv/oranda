using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using AvaloniaEdit;
using Oranda.App.Features.MyBatisWorkspace.Editor;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.MyBatisWorkspace.Flow;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>
/// 쿼리 탭 안 분할의 스크롤 맞춤: 원문을 내리면 펼친 SQL 이 대응 줄로, 펼친 SQL 을 내리면 원문이 대응 줄로 간다. 맞춘 뒤 핑퐁이 없고,
/// 펼친 SQL 글이 바뀌어도(SqlCodeView 가 맨 위로 보냄) 원문 기준 위치를 지키며, 「스크롤 맞춤」 토글을 끄면 따라가지 않는다(설정에 기억).
/// </summary>
public sealed partial class ParamsRunTests
{
    private const string LongStatementId = "selectLongList";

    /// <summary>
    /// 화면보다 긴 문장: 열 줄 사이사이에 꺼진 &lt;if&gt; 블록(3줄)이 끼어 있어 펼친 SQL 이 원문보다 짧다(줄이 1:1 이 아니다).
    /// </summary>
    private static string LongStatement()
    {
        var xml = new StringBuilder("<select id=\"" + LongStatementId + "\" parameterType=\"map\">\n  SELECT A.COL0\n");
        for (int i = 1; i < 120; i++)
        {
            xml.Append(CultureInfo.InvariantCulture, $"       , A.COL{i}\n");
            if (i % 4 == 0)
            {
                xml.Append(CultureInfo.InvariantCulture, $"  <if test=\"flag != null\">\n       , B.X{i} AS FLAG{i}_#{{flag}}\n  </if>\n");
            }
        }

        return xml.Append("    FROM T A\n</select>").ToString();
    }

    private static (MyBatisStatementEditorView View, Window Window) LongView(Rig rig, SettingsService? settings)
    {
        rig.AddStatementTab(LongStatementId, LongStatement());
        var view = SplitView(rig, settings);
        var window = ShowWindow(view);
        Pump(window);
        return (view, window);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>사용자가 그 편집기 위에서 휠을 굴린 뒤(주도 칸이 됨) 정확한 오프셋으로 옮긴다.</summary>
    private static void UserScroll(Window window, TextEditor editor, double lines)
    {
        var center = editor.TranslatePoint(new Point(editor.Bounds.Width / 2, editor.Bounds.Height / 2), window)!.Value;
        window.MouseWheel(center, new Vector(0, -1));
        Pump(window);
        MyBatisScrollSync.ScrollTo(editor, lines * editor.TextArea.TextView.DefaultLineHeight);
        Pump(window);
    }

    private static TextEditor SourceOf(MyBatisStatementEditorView view) => view.Editor.TextEditorControl;

    private static TextEditor ExpandedOf(MyBatisStatementEditorView view) => view.Split.ExpandedView.CodeView.TextEditorControl;

    private static double OffsetOf(TextEditor editor) => editor.TextArea.TextView.ScrollOffset.Y;

    [Fact]
    public Task Scrolling_either_pane_moves_the_other_to_the_mapped_line_without_ping_pong() => Ui.Run(() =>
    {
        using var rig = Rig.Create();
        var (view, window) = LongView(rig, settings: null);
        var sync = view.ScrollSync;
        var vm = view.Expanded!;
        Assert.True(sync.IsEnabled);
        Assert.Contains("A.COL119", vm.Sql, StringComparison.Ordinal);
        Assert.True(sync.Map.ExpandedLineCount < sync.Map.SourceLineCount - 50, $"원문 {sync.Map.SourceLineCount}줄, 펼친 {sync.Map.ExpandedLineCount}줄");

        // 원문을 60줄 내린다: 펼친 SQL 맨 위가 대응표의 대응 위치다(꺼진 if 블록만큼 덜 내려간다).
        UserScroll(window, SourceOf(view), 60);
        Assert.Equal(60, sync.SourceTopLine, 2);
        var mapped = sync.Map.ToExpanded(sync.SourceTopLine);
        Assert.InRange(mapped, 30, 55);
        Assert.Equal(mapped, sync.ExpandedTopLine, 1);
        MyBatisFlowRig.Render(window, "split-scroll-synced", 1200, 640); // ORANDA_RENDER_DIR 이 있을 때만 PNG

        // 핑퐁 없음: 일을 여러 번 돌려도 두 오프셋이 그대로다.
        var (source, expanded) = (OffsetOf(SourceOf(view)), OffsetOf(ExpandedOf(view)));
        for (int i = 0; i < 5; i++)
        {
            Pump(window);
        }

        Assert.Equal(source, OffsetOf(SourceOf(view)), 1);
        Assert.Equal(expanded, OffsetOf(ExpandedOf(view)), 1);

        // 반대 방향: 펼친 SQL 을 20줄로 옮기면 원문이 대응 줄로 간다.
        UserScroll(window, ExpandedOf(view), 20);
        Assert.Equal(20, sync.ExpandedTopLine, 2);
        Assert.Equal(sync.Map.ToSource(20), sync.SourceTopLine, 1);
        Assert.True(sync.SourceTopLine > 24, $"원문 맨 위 {sync.SourceTopLine}");
        (source, expanded) = (OffsetOf(SourceOf(view)), OffsetOf(ExpandedOf(view)));
        for (int i = 0; i < 5; i++)
        {
            Pump(window);
        }

        Assert.Equal(source, OffsetOf(SourceOf(view)), 1);
        Assert.Equal(expanded, OffsetOf(ExpandedOf(view)), 1);
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task When_the_expanded_sql_changes_it_keeps_the_source_position_instead_of_jumping_to_the_top() => Ui.Run(async () =>
    {
        using var rig = Rig.Create();
        var (view, window) = LongView(rig, settings: null);
        var sync = view.ScrollSync;
        var vm = view.Expanded!;

        // 원문을 고친다(주도 칸 = 원문): 디바운스 뒤 펼친 SQL 이 바뀌어도 맨 위로 가지 않고 원문 위치에 맞는다.
        UserScroll(window, SourceOf(view), 60);
        var sourceOffset = OffsetOf(SourceOf(view));
        // 가짜 세션은 문장 요소를 탭 글에서 다시 읽지 않으므로 고친 원문을 요소에도 넣어 둔다(실제 세션은 탭 글을 따라간다).
        var document = SourceOf(view).Document;
        var at = document.Text.IndexOf("    FROM T A", StringComparison.Ordinal);
        rig.Session.Elements[LongStatementId] = rig.Session.Elements[LongStatementId] with { Text = document.Text.Insert(at, "       , A.ADDED\n") };
        document.Insert(at, "       , A.ADDED\n");
        await HeadlessHost.WaitUntil(() => vm.Sql.Contains("A.ADDED", StringComparison.Ordinal));
        Pump(window);
        Assert.Equal(sourceOffset, OffsetOf(SourceOf(view)), 1);
        Assert.Equal(sync.Map.ToExpanded(sync.SourceTopLine), sync.ExpandedTopLine, 1);
        Assert.True(sync.ExpandedTopLine > 30, $"펼친 SQL 맨 위 {sync.ExpandedTopLine}");

        // 마지막 입력이 펼친 SQL 칸이었어도: 바인드 창 값으로 꺼진 블록이 켜져 글이 길어지면, 맨 위로 간 펼친 쪽이 원문을 끌어올리지 않는다.
        UserScroll(window, ExpandedOf(view), 40);
        sourceOffset = OffsetOf(SourceOf(view));
        var sourceTop = sync.SourceTopLine;
        RowOf(view.Tab, "FLAG").Text = "1";
        await HeadlessHost.WaitUntil(() => vm.Sql.Contains("B.X4", StringComparison.Ordinal));
        Pump(window);
        Assert.Equal(sourceOffset, OffsetOf(SourceOf(view)), 1);
        Assert.Equal(sync.Map.ToExpanded(sourceTop), sync.ExpandedTopLine, 1);
        Assert.True(sync.ExpandedTopLine > 30, $"펼친 SQL 맨 위 {sync.ExpandedTopLine}");
        window.Close();
    });

    [Fact]
    public Task Turning_the_sync_toggle_off_stops_following_and_is_remembered() => Ui.Run(async () =>
    {
        using var rig = Rig.Create();
        var store = new MemorySettings();
        var settings = new SettingsService(store);
        var (view, window) = LongView(rig, settings);
        var split = view.Split;
        var sync = view.ScrollSync;
        var shownTogether = SplitView(rig, settings); // 화면에 함께 떠 있는 다른 탭(켬 상태로 읽어 둠)

        // 토글은 분할 단추 줄에 있고 기본 켬, 아이콘·툴팁이 있다.
        Assert.Contains(split.SyncScrollButton, split.Buttons.Children);
        Assert.True(split.SyncScrollButton.IsChecked);
        Assert.IsNotType<string>(split.SyncScrollButton.Content);
        Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(split.SyncScrollButton) as string));

        // 끄면 원문을 내려도 펼친 SQL 은 그대로다. 설정에 기억한다.
        Click(split.SyncScrollButton);
        await split.LastSave;
        Assert.False(split.IsScrollSynced);
        Assert.False(sync.IsEnabled);
        Assert.False(store.Saved.MyBatis.SyncScroll);
        var expanded = OffsetOf(ExpandedOf(view));
        UserScroll(window, SourceOf(view), 60);
        Assert.Equal(expanded, OffsetOf(ExpandedOf(view)), 1);

        // 함께 떠 있던 탭이 분할선을 옮겨도 바꾼 비율만 저장하므로 꺼 둔 스크롤 맞춤을 되돌리지 않는다.
        shownTogether.Split.SetRatio(0.4);
        await shownTogether.Split.LastSave;
        Assert.Equal(0.4, store.Saved.MyBatis.ExpandedRatio, 3);
        Assert.False(store.Saved.MyBatis.SyncScroll);

        // 다른 탭(새 View)도 꺼진 채 시작한다.
        var other = SplitView(rig, settings);
        Assert.False(other.Split.IsScrollSynced);
        Assert.False(other.ScrollSync.IsEnabled);

        // 다시 켜면 그 자리에서 바로 원문 위치에 맞춘다.
        Click(split.SyncScrollButton);
        await split.LastSave;
        Pump(window);
        Assert.True(store.Saved.MyBatis.SyncScroll);
        Assert.Equal(sync.Map.ToExpanded(sync.SourceTopLine), sync.ExpandedTopLine, 1);

        // 숨기면 맞춤도 쉰다. 숨긴 채 원문을 옮기고 다시 보이면 레이아웃 뒤에 원문 위치로 맞춘다.
        split.SetShown(false);
        Assert.False(sync.IsEnabled);
        UserScroll(window, SourceOf(view), 90);
        split.SetShown(true);
        Assert.True(sync.IsEnabled);
        Pump(window);
        Assert.Equal(90, sync.SourceTopLine, 1);
        Assert.Equal(sync.Map.ToExpanded(sync.SourceTopLine), sync.ExpandedTopLine, 1);
        window.Close();
    });

    [Fact]
    public Task A_folded_source_block_holds_still_while_the_expanded_sql_scrolls_through_it() => Ui.Run(() =>
    {
        using var rig = Rig.Create();
        var (view, window) = LongView(rig, settings: null);
        var sync = view.ScrollSync;
        var source = SourceOf(view);
        var folding = Assert.IsAssignableFrom<AvaloniaEdit.Folding.FoldingManager>(view.Editor.Folding);

        // 원문 21~70줄을 접는다. 펼친 SQL 을 1/4 줄씩 내려도(대응 위치가 줄 안 비율을 가짐) 원문은 뒤로 가지 않는다(접힌 구간 안에서 떨지 않는다).
        var document = source.Document;
        var fold = folding.CreateFolding(document.GetLineByNumber(21).Offset, document.GetLineByNumber(70).EndOffset);
        fold.IsFolded = true;
        Pump(window);
        var offsets = new List<double>();
        for (double line = 5; line <= 70; line += 0.25)
        {
            UserScroll(window, ExpandedOf(view), line);
            offsets.Add(OffsetOf(source));
        }

        for (int i = 1; i < offsets.Count; i++)
        {
            Assert.True(offsets[i] >= offsets[i - 1] - 0.5, $"원문 오프셋이 {offsets[i - 1]} → {offsets[i]} 로 뒤로 감(펼친 줄 {5 + (i * 0.25)})");
        }

        Assert.True(offsets[^1] > offsets[0]);
        Assert.True(sync.SourceTopLine > 70, $"원문 맨 위 {sync.SourceTopLine}");
        window.Close();
        return Task.CompletedTask;
    });
}
