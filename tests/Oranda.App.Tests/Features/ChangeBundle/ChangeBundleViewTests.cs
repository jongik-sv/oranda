using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.ChangeBundle;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.ChangeBundle;
using Oranda.Core.Connection;
using Oranda.Language;
using Oranda.Language.Lexing;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.ChangeBundle;

// 소유: te-bundle (T9). 반영 묶음 창·실행 창·저장 창을 실제로 그려 본다(Headless).
[Collection(ChangeBundleDialogsGroup.Name)]
public sealed class ChangeBundleViewTests : IDisposable
{
    private readonly ScriptedBundleDialogs _dialogs = new();
    private readonly IChangeBundleDialogs _previous = ChangeBundleDialogs.Current;

    public ChangeBundleViewTests() => ChangeBundleDialogs.Current = _dialogs;

    public void Dispose() => ChangeBundleDialogs.Current = _previous;

    [Fact]
    public async Task Panel_shows_rows_dims_unchecked_and_buttons_follow_selection()
    {
        var store = new MemoryBundleStore();
        var b = await store.SeedAsync("릴리스 1", Dbms.Oracle, ["SAMPLE"], "ALTER TABLE SAMPLE.ORDERS ADD (NOTE VARCHAR2(10))", "DROP TABLE SAMPLE.OLD_ORDERS");
        await store.SetCheckedAsync((await store.ListItemsAsync(b.Id, default))[1].Id, false, default);
        using var vm = ChangeBundleViewModel.ForTests(store, new MemoryBundleService(store));
        await vm.ReloadAsync();

        await HeadlessHost.Run(async () =>
        {
            var view = new ChangeBundleView { DataContext = vm };
            var window = new Window { Content = view, Width = 900, Height = 400 };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var list = view.FindControl<ListBox>("List")!;
                Assert.Equal(2, list.ItemCount);
                var rows = list.GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("bundle-row")).ToList();
                Assert.Equal(2, rows.Count);
                Assert.DoesNotContain("dimmed", rows[0].Classes);
                Assert.Contains("dimmed", rows[1].Classes);
                Assert.True(view.FindControl<Button>("RunButton")!.IsEnabled);
                Assert.False(view.FindControl<Button>("UpButton")!.IsEnabled);
                Assert.Equal("릴리스 1", (view.FindControl<ComboBox>("BundleBox")!.SelectedItem as ChangeBundleInfo)?.Name);

                list.SelectedIndex = 1;
                Dispatcher.UIThread.RunJobs();
                Assert.True(view.FindControl<Button>("UpButton")!.IsEnabled);

                // 「묶음 지우기」 단추 → 확인 창(가짜는 취소) → 묶음은 남는다.
                view.FindControl<Button>("DeleteBundleButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.Contains(_dialogs.Asked, a => a.StartsWith("묶음 지우기:", StringComparison.Ordinal));
                Assert.Single(await store.ListBundlesAsync(default));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Run_and_save_windows_render_connections_and_owner_map()
    {
        var bundle = new ChangeBundleInfo(1, "릴리스 1", DateTimeOffset.Now, DateTimeOffset.Now, Dbms.Oracle, ["SAMPLE"], true);
        var item = new ChangeBundleItem(1, 1, 1, "DROP TABLE SAMPLE.OLD_ORDERS", ChangeBundleItemSource.SqlEditor, "SAMPLE", "OLD_ORDERS", true,
            DateTimeOffset.Now, "h", ChangeBundleCautions.None, null);
        var prod = new FakeSession(TestData.Profile(ConnectionEnvironment.Production) with { Name = "prod-example" }, TestData.Server());
        var run = new ChangeBundleRunViewModel(bundle, [item], [prod], null, new ChangeBundleOwnerRewriter(), new LanguageService(),
            new FakeExecutionService(), null);
        var save = new ChangeBundleSaveViewModel(bundle, [item], new ChangeBundleOwnerRewriter(), new LanguageService());

        await HeadlessHost.Run(() =>
        {
            var runWindow = new ChangeBundleRunWindow { DataContext = run };
            var saveWindow = new ChangeBundleSaveWindow { DataContext = save };
            runWindow.Show();
            saveWindow.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                Assert.True(run.IsProduction); // 운영 접속뿐이면 그것이 기본, 빨간 안내
                Assert.True(runWindow.FindControl<Button>("RunButton")!.IsEnabled);
                Assert.Single(OwnerBoxes(runWindow));
                Assert.Single(OwnerBoxes(saveWindow));
                Assert.True(saveWindow.FindControl<Button>("SaveButton")!.IsEnabled);
            }
            finally
            {
                runWindow.Close();
                saveWindow.Close();
            }

            return Task.CompletedTask;
        });
    }

    private static List<TextBox> OwnerBoxes(Window window) =>
        [.. window.GetVisualDescendants().OfType<TextBox>().Where(t => Avalonia.Automation.AutomationProperties.GetName(t) == "바꿀 소유자")];
}
