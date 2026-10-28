using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Headless;
using Avalonia.Threading;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Oracle.Connection;

namespace Oranda.App.Tests.Features.ConnectionManager;

/// <summary>
/// 사용자 지정 접속 문자열을 여러 줄 tnsnames.ora 형식으로 넣는다: 저장은 적은 모양 그대로, 접속·미리보기·방식 판별은 한 줄로 정리한 값.
/// </summary>
public sealed class ConnectionCustomDescriptorTests
{
    private const string Entry =
        "# 영업 복제본\n"
        + "ORCLPDB.EXAMPLE =\n"
        + "  (DESCRIPTION =\n"
        + "    (ADDRESS_LIST =\n"
        + "      (ADDRESS = (PROTOCOL = TCP)(HOST = db.example.com)(PORT = 1521))\n"
        + "    )\n"
        + "    (CONNECT_DATA =\n"
        + "      (SERVICE_NAME = ORCLPDB)\n"
        + "    )\n"
        + "  )";

    private const string OneLine = "(DESCRIPTION=(ADDRESS_LIST=(ADDRESS=(PROTOCOL=TCP)(HOST=db.example.com)(PORT=1521)))(CONNECT_DATA=(SERVICE_NAME=ORCLPDB)))";

    [Fact]
    public void Multi_line_entry_is_saved_as_typed_and_connects_with_one_line()
    {
        var vm = ProfileEditorViewModel.ForNew();
        vm.User = "scott";
        vm.Mode = ConnectionMode.EasyConnect;
        vm.EasyConnect = Entry;

        Assert.Equal("ORCLPDB.EXAMPLE", vm.Name); // 이름이 비어 있으면 별칭을 이름으로 제안한다
        Assert.Empty(vm.EasyConnectError);
        Assert.Equal("scott@" + OneLine, vm.ConnectionStringPreview);

        var profile = vm.TryBuild(out var errors)!;
        Assert.Empty(errors);
        Assert.Equal(Entry, profile.DataSource); // 모양 그대로 저장
        Assert.Equal(OneLine, new global::Oracle.ManagedDataAccess.Client.OracleConnectionStringBuilder(profile.BuildConnectionString("pw")).DataSource);

        var reopened = ProfileEditorViewModel.From(profile);
        Assert.Equal(ConnectionMode.EasyConnect, reopened.Mode);
        Assert.Equal(Entry, reopened.EasyConnect);
        Assert.False(reopened.IsDirty);
        Assert.Equal("scott@" + OneLine, new ConnectionTreeNode("x", profile).Subtitle);
    }

    [Fact]
    public void A_name_the_user_typed_is_not_replaced_by_the_alias()
    {
        var vm = ProfileEditorViewModel.ForNew();
        vm.Name = "영업";
        vm.Mode = ConnectionMode.EasyConnect;
        vm.EasyConnect = Entry;
        Assert.Equal("영업", vm.Name);
    }

    [Fact]
    public void Unbalanced_parentheses_show_an_error_and_block_saving()
    {
        var vm = ProfileEditorViewModel.ForNew();
        vm.Name = "x";
        vm.User = "scott";
        vm.Mode = ConnectionMode.EasyConnect;
        vm.EasyConnect = "(DESCRIPTION =\n  (ADDRESS = (PROTOCOL = TCP)(HOST = db.example.com)(PORT = 1521))\n  (CONNECT_DATA = (SERVICE_NAME = ORCLPDB))";

        Assert.Contains("괄호", vm.EasyConnectError, StringComparison.Ordinal);
        Assert.Null(vm.TryBuild(out var errors));
        Assert.Contains(errors, e => e.Contains("괄호", StringComparison.Ordinal));

        vm.EasyConnect += ")";
        Assert.Empty(vm.EasyConnectError);
        Assert.NotNull(vm.TryBuild(out _));
    }

    [Fact]
    public void Multi_line_simple_sid_descriptor_and_one_line_profiles_read_back_as_before()
    {
        var sid = "(DESCRIPTION =\n  (ADDRESS = (PROTOCOL = TCP)(HOST = db.example.com)(PORT = 1521))\n  (CONNECT_DATA = (SID = ORCL))\n)";
        var vm = ProfileEditorViewModel.From(new ConnectionProfile("sid", "scott", sid));
        Assert.Equal(ConnectionMode.HostPortService, vm.Mode);
        Assert.True(vm.UseSid);
        Assert.Equal(("db.example.com", "1521", "ORCL"), (vm.Host, vm.Port, vm.Service));

        var ez = ProfileEditorViewModel.From(new ConnectionProfile("ez", "scott", OneLine));
        Assert.Equal(ConnectionMode.EasyConnect, ez.Mode);
        Assert.Equal(OneLine, ez.EasyConnect);
        Assert.Equal(OneLine, ez.TryBuild(out _)!.DataSource);
    }

    [Fact]
    public Task Custom_box_takes_enter_as_a_new_line_and_keeps_the_shape_after_save() => HeadlessHost.Run(async () =>
    {
        using var fx = new ServiceFixture();
        await fx.Service.SaveProfileAsync(new ConnectionProfile("custom", "scott", OneLine), "pw", true, CancellationToken.None);
        var vm = new ConnectionManagerViewModel(fx.Service);
        await vm.LoadAsync();
        vm.Selected = vm.AllNodes().Single(n => n.Title == "custom");
        var window = new ConnectionManagerWindow { DataContext = vm, Width = 900, Height = 640 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var editor = window.FindControl<ProfileEditorView>("EditorView")!;
            var box = editor.FindControl<TextBox>("EasyConnectBox")!;
            Assert.True(box.IsEffectivelyVisible);
            Assert.True(box.AcceptsReturn);
            Assert.False(box.AcceptsTab); // Tab 은 다음 칸으로
            var oneLineHeight = box.Bounds.Height;

            // Enter 는 칸 안 줄 바꿈이다(접속·저장 단추로 가지 않는다).
            box.Text = "(DESCRIPTION =";
            box.Focus();
            box.CaretIndex = box.Text.Length;
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyTextInput("  (ADDRESS = (PROTOCOL = TCP)(HOST = db.example.com)(PORT = 1521))");
            Dispatcher.UIThread.RunJobs();
            Assert.Contains('\n', vm.Editor!.EasyConnect.Replace("\r\n", "\n", StringComparison.Ordinal));

            // 괄호가 덜 닫히면 칸 아래에 안내가 보인다.
            var error = editor.FindControl<TextBlock>("EasyConnectErrorText")!;
            Assert.True(error.IsEffectivelyVisible);
            Assert.Contains("괄호", error.Text, StringComparison.Ordinal);

            vm.Editor.EasyConnect = Entry.Replace("ORCLPDB.EXAMPLE =\n", string.Empty, StringComparison.Ordinal);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.False(error.IsEffectivelyVisible);
            Assert.True(box.Bounds.Height > oneLineHeight, $"여러 줄이면 칸이 늘어난다: {box.Bounds.Height} <= {oneLineHeight}");
            Assert.True(box.Bounds.Height <= box.MaxHeight + 0.5);

            var saved = vm.Editor.EasyConnect;
            Assert.True(await vm.SaveCurrentAsync());
            var stored = (await fx.Service.LoadAsync(CancellationToken.None)).Value.Profiles.Single();
            Assert.Equal(saved.Trim(), stored.DataSource);

            vm.Selected = null;
            Dispatcher.UIThread.RunJobs();
            vm.Selected = vm.AllNodes().Single(n => n.Title == "custom");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(saved.Trim(), editor.FindControl<TextBox>("EasyConnectBox")!.Text);
        }
        finally
        {
            window.Close();
        }
    });
}
