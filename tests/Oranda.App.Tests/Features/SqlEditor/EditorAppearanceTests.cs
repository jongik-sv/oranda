using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.SqlEditor;

/// <summary>
/// 설정의 편집기 글꼴·크기·들여쓰기와 보기 → 확대·축소가 실제 편집기 컨트롤에 닿는다.
/// 예전에는 값이 앱 리소스에만 들어가고 편집기 View 는 글꼴을 직접 적어 두어 아무것도 바뀌지 않았다(리소스만 단정하던 시험은 통과했다).
/// </summary>
[Collection("EditorAppearance")]
public class EditorAppearanceTests
{
    [Fact]
    public Task Sql_editor_and_code_view_follow_editor_font_size_and_tab_size_settings() => HeadlessHost.Run(async () =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-font-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var h = new ShellHarness(dir);
            var editor = new SqlEditorView();
            var code = new SqlCodeView();
            var window = new Window { Width = 600, Height = 400, Content = new StackPanel { Children = { editor, code } } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var settings = h.Get<SettingsService>();
            await settings.UpdateAsync(s => s with { Editor = s.Editor with { FontFamily = "Consolas", FontSize = 17, TabSize = 2 } });
            await HeadlessHost.WaitUntil(() => editor.TextEditorControl.FontSize == 17);
            Assert.StartsWith("Consolas", editor.TextEditorControl.FontFamily.Name, StringComparison.Ordinal);
            Assert.Equal(2, editor.TextEditorControl.Options.IndentationSize);
            Assert.Equal(17, code.TextEditorControl.FontSize);
            Assert.StartsWith("Consolas", code.TextEditorControl.FontFamily.Name, StringComparison.Ordinal);

            await h.Get<ICommandRegistry>().ExecuteAsync("oranda.view.zoomIn", h.Get<IContextKeys>());
            await HeadlessHost.WaitUntil(() => editor.TextEditorControl.FontSize == 18);
            Assert.Equal(18, code.TextEditorControl.FontSize);
            window.Close();
            await settings.ZoomResetAsync();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    });
}

/// <summary>
/// 편집기 글꼴·화면 글자 배율 리소스는 프로세스에 하나라, 다른 시험이 병렬로 셸을 만들면 기본값으로 되돌리고,
/// 거꾸로 이 묶음이 바꾼 배율(150 %·200 %)을 병렬 시험이 보면 행 높이가 1.5·2배가 된다. 병렬 시험이 끝난 뒤 따로 돈다.
/// </summary>
[CollectionDefinition("EditorAppearance", DisableParallelization = true)]
public sealed class EditorAppearanceGroup
{
}
