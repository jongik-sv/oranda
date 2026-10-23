using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;
using Oranda.App.Shell.Appearance;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Shell;

/// <summary>고정폭 글꼴(R07): 앱 리소스 값이 <see cref="MonoFonts"/> 상수와 같고, 리소스로 바꾼 화면이 예전 글꼴 그대로 그려지는지.</summary>
public class MonoFontsTests
{
    [Fact]
    public Task App_resources_hold_the_same_font_names_as_the_helper() => HeadlessHost.Run(() =>
    {
        var resources = Application.Current!.Resources;
        Assert.True(resources.TryGetResource(MonoFonts.FixedKey, null, out var fixedFont));
        Assert.True(resources.TryGetResource(MonoFonts.CodeKey, null, out var codeFont));
        Assert.Equal(Names(FontFamily.Parse(MonoFonts.FixedName)), Names(Assert.IsType<FontFamily>(fixedFont)));
        Assert.Equal(Names(FontFamily.Parse(MonoFonts.CodeName)), Names(Assert.IsType<FontFamily>(codeFont)));
        Assert.Equal(Names(FontFamily.Parse(MonoFonts.CodeName)), Names(MonoFonts.Code));
    });

    [Theory]
    [InlineData(typeof(Oranda.App.Features.Execution.OutputView), MonoFonts.CodeName)]
    [InlineData(typeof(Oranda.App.Features.DbTools.DdlExtractView), MonoFonts.FixedName)]
    [InlineData(typeof(Oranda.App.Features.SqlMonitor.SqlMonitorView), MonoFonts.FixedName)]
    [InlineData(typeof(Oranda.App.Features.ExtensionTest.ExtensionTestView), MonoFonts.FixedName)]
    [InlineData(typeof(Oranda.App.Features.LobRowView.LobEditorView), MonoFonts.FixedName)]
    public Task Views_that_moved_to_the_resources_still_use_the_same_fixed_font(Type view, string expected) => HeadlessHost.Run(() =>
    {
        var control = (Control)Activator.CreateInstance(view)!;
        var window = new Window { Width = 800, Height = 600, Content = control };
        window.Show();
        try
        {
            window.UpdateLayout();
            var names = control.GetVisualDescendants().OfType<TemplatedControl>().Select(c => Names(c.FontFamily))
                .Concat(control.GetVisualDescendants().OfType<TextBlock>().Select(t => Names(t.FontFamily)));
            Assert.Contains(Names(FontFamily.Parse(expected)), names); // 예전 axaml 의 FontFamily="…" 와 같은 글꼴 목록
        }
        finally
        {
            window.Close();
        }
    });

    // FontFamily.Name 은 첫 이름뿐이라 대체 글꼴 목록 전체를 비교한다.
    private static string Names(FontFamily font) => string.Join(",", font.FamilyNames);
}
