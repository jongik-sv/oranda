using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.HistoryScripts;
using Oranda.App.Shell.Controls;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Settings;
using Oranda.Core.Storage;

namespace Oranda.App.Tests.Features.HistoryScripts;

// 소유: gc-w9-narrow NL-01·NL-03. 실행 이력을 좁게(왼쪽·오른쪽) 붙였을 때 카드 목록으로 바뀌는지(Headless).
public sealed class HistoryNarrowLayoutTests
{
    private static HistoryEntry Entry(long id, string sql, HistoryStatus status = HistoryStatus.Succeeded, string? error = null) => new()
    {
        Id = id,
        ProfileId = Guid.Empty,
        ProfileName = "dev-example",
        ExecutedAt = new DateTimeOffset(2026, 11, 16, 9, 30, (int)id % 60, TimeSpan.Zero),
        SqlText = sql,
        StatementKind = StatementKind.Query,
        Source = HistorySource.Editor,
        Status = status,
        ElapsedMs = 42 * id,
        RowsAffected = status == HistoryStatus.Succeeded ? 3 : null,
        ErrorCode = error is null ? null : "ORA-00942",
        ErrorMessage = error,
    };

    private static List<HistoryEntry> Sample() =>
    [
        Entry(1, "SELECT empno, ename FROM emp WHERE deptno = :dept"),
        Entry(2, "UPDATE emp SET sal = sal * 1.1 WHERE empno = 7369"),
        Entry(3, "SELECT * FROM no_such_table", HistoryStatus.Failed, "ORA-00942: table or view does not exist"),
    ];

    [Fact]
    public async Task History_switches_to_cards_when_narrow_and_back_keeping_the_selection()
    {
        var store = new Store(Sample());
        using var vm = new HistoryViewModel(store, () => null) { CurrentConnectionOnly = false };
        await vm.ReloadAsync();

        await HeadlessHost.Run(() =>
        {
            var view = new HistoryView { DataContext = vm };
            var window = new Window { Content = view, Width = 800, Height = 420 };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var list = view.FindControl<ListBox>("List")!;
                Assert.False(NarrowLayout.GetIsNarrow(view));
                Assert.True(view.FindControl<Border>("HistoryHeaderBorder")!.IsVisible);
                Assert.True(view.FindControl<Border>("WideToolbar")!.IsVisible);
                Assert.False(view.FindControl<Border>("NarrowToolbar")!.IsVisible);
                Assert.Empty(Cards(list));

                list.SelectedItems!.Add(vm.Items[0]);
                list.SelectedItems.Add(vm.Items[2]);

                window.Width = 400;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();

                Assert.True(NarrowLayout.GetIsNarrow(view));
                Assert.Contains(NarrowLayout.NarrowClass, view.Classes);
                Assert.False(view.FindControl<Border>("HistoryHeaderBorder")!.IsVisible);
                Assert.False(view.FindControl<Border>("WideToolbar")!.IsVisible);
                Assert.True(view.FindControl<Border>("NarrowToolbar")!.IsVisible);
                Assert.Equal(3, Cards(list).Count);
                Assert.Equal(2, list.SelectedItems.Count);
                Assert.NotNull(vm.Selected);

                // 오류가 있는 카드만 3줄(오류 줄)이 보인다. 접속 이름은 「이 접속만」 이 꺼져 있으면 보인다.
                var failed = Cards(list).Single(c => c.DataContext is HistoryItem { ErrorText: not null });
                Assert.Contains(failed.GetLogicalDescendants().OfType<TextBlock>(), t => t.IsVisible && t.Text == vm.Items.First(i => i.ErrorText is not null).ErrorText);
                Assert.Contains(failed.GetLogicalDescendants().OfType<TextBlock>(), t => t.IsVisible && t.Text == "dev-example");

                // 오류가 있는 이력을 고르면 고른 문장 상자 아래 빨간 띠에 오류 전문이 보인다(좁은 배치만).
                var band = view.FindControl<Panel>("DetailErrorBand")!;
                vm.Selected = vm.Items.Single(i => i.ErrorText is not null);
                Dispatcher.UIThread.RunJobs();
                Assert.True(band.IsVisible);
                Assert.Contains(band.GetLogicalDescendants().OfType<SelectableTextBlock>(), t => t.IsEffectivelyVisible && t.Text == vm.Selected!.ErrorText);

                Oranda.App.Tests.Features.MyBatisWorkspace.Flow.MyBatisFlowRig.Render(window, "history-narrow-400", 400, 420);
                if (Oranda.App.Tests.Manual.ManualCapture.Enabled)
                {
                    Oranda.App.Tests.Manual.ManualCapture.Save(window, "history-narrow"); // 매뉴얼 그림(좁게 붙였을 때)
                }

                // 기본 왼쪽 폭(약 280)에서도 카드 첫 줄의 경과·행 수가 잘리지 않는다(접속 이름이 먼저 줄어든다).
                window.Width = 280;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                // 글자 폭은 렌더 방식(헤드리스 기본 글꼴·Skia)에 따라 달라서 칸 끝을 재지 않고, 접속 이름이 남는 칸만 받아 줄어드는지 본다.
                var card = Cards(list).First(c => c.DataContext is HistoryItem { RowsText.Length: > 0 });
                var rows = card.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text?.EndsWith('행') == true);
                var profile = card.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "dev-example");
                var rowsRight = rows.TranslatePoint(new Avalonia.Point(rows.Bounds.Width, 0), card)!.Value.X;
                var profileLeft = profile.TranslatePoint(default, card)!.Value.X;
                Assert.True(profileLeft >= rowsRight - 0.5, $"접속 이름 {profileLeft} 이 행 수 칸 {rowsRight} 을 밀어냄");
                Assert.True(profile.Bounds.Width <= Math.Max(0, card.Bounds.Width - rowsRight) + 0.5, $"접속 이름 폭 {profile.Bounds.Width}");
                Assert.Equal(TextTrimming.CharacterEllipsis, profile.TextTrimming);

                window.Width = 800;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                Assert.False(NarrowLayout.GetIsNarrow(view));
                Assert.True(view.FindControl<Border>("HistoryHeaderBorder")!.IsVisible);
                Assert.Empty(Cards(list));
                Assert.Equal(vm.Selected, list.SelectedItem);
                Assert.False(band.IsVisible); // 넓은 배치는 지금 그대로
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Narrow_history_keeps_delete_key_menu_and_status_filter()
    {
        var store = new Store(Sample());
        using var vm = new HistoryViewModel(store, () => null) { CurrentConnectionOnly = false };
        await vm.ReloadAsync();

        await HeadlessHost.Run(async () =>
        {
            var view = new HistoryView { DataContext = vm };
            var window = new Window { Content = view, Width = 400, Height = 360 };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                Assert.True(NarrowLayout.GetIsNarrow(view));
                var list = view.FindControl<ListBox>("List")!;

                // Delete 키 = 넓은 배치와 같은 처리(고른 줄 지우기).
                list.SelectedItem = vm.Items[0];
                var first = vm.Items[0].Id;
                list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete });
                await WaitAsync(() => vm.Items.Count == 2);
                Assert.Equal([first], store.Deleted);

                // 「…」 메뉴의 지우기 = 같은 처리.
                list.SelectedItem = vm.Items[0];
                var second = vm.Items[0].Id;
                view.FindControl<MenuItem>("NarrowDeleteMenu")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await WaitAsync(() => vm.Items.Count == 1);
                Assert.Equal([first, second], store.Deleted);

                // 칩 줄(이 접속만 · 상태)은 처음엔 펼쳐져 있고 「필터」 로 접는다. 상태는 넓은 배치의 상태 상자와 같은 값.
                var chips = view.FindControl<WrapPanel>("NarrowFilterChips")!;
                var filter = view.FindControl<Avalonia.Controls.Primitives.ToggleButton>("NarrowFilterToggle")!;
                Assert.True(chips.IsVisible);
                filter.IsChecked = false;
                Dispatcher.UIThread.RunJobs();
                Assert.False(chips.IsVisible);
                filter.IsChecked = true;
                Dispatcher.UIThread.RunJobs();
                Assert.True(chips.IsVisible);
                view.FindControl<ComboBox>("NarrowStatusBox")!.SelectedItem = vm.StatusFilters[^1];
                Assert.Equal(vm.StatusFilters[^1], vm.StatusFilter);
                Assert.Equal(vm.StatusFilters[^1], view.FindControl<ComboBox>("StatusBox")!.SelectedItem);

                // 「모두 비우기」 도 메뉴로.
                view.FindControl<MenuItem>("NarrowClearMenu")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await WaitAsync(() => store.Cleared);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static List<StackPanel> Cards(ListBox list) =>
        [.. list.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Classes.Contains("history-card"))];

    private static async Task WaitAsync(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(done());
    }

    private sealed class Store(List<HistoryEntry> entries) : IHistoryStore
    {
        public List<long> Deleted { get; } = [];

        public bool Cleared { get; private set; }

        public Task<long> AddAsync(HistoryEntry entry, CancellationToken ct) => Task.FromResult(0L);

        public Task<IReadOnlyList<HistoryEntry>> SearchAsync(HistoryQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<HistoryEntry>>([.. entries
                .Where(e => query.Status is null || e.Status == query.Status)
                .OrderByDescending(e => e.Id).Take(query.Limit)]);

        public Task DeleteAsync(IReadOnlyList<long> ids, CancellationToken ct)
        {
            Deleted.AddRange(ids);
            entries.RemoveAll(e => ids.Contains(e.Id));
            return Task.CompletedTask;
        }

        public Task ClearAsync(Guid? profileId, CancellationToken ct)
        {
            Cleared = true;
            entries.Clear();
            return Task.CompletedTask;
        }

        public Task PruneAsync(HistorySettings settings, CancellationToken ct) => Task.CompletedTask;
    }
}
