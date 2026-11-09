using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.Core.Connection;
using Oranda.Core.Execution;

namespace Oranda.App.Tests.Features.ConnectionManager;

// Tibero 서버는 읽기 전용 트랜잭션에서 쓰기를 막지 않는다(6.2.15 실측). 접속 대화상자가 Tibero + 읽기 전용일 때 안내를 보인다.
// 글자(UiText)는 전역이라 앱 정적 상태 묶음에서 한국어로 고정해 돈다.
[Collection("GridEditStatics")]
public sealed class ProfileEditorReadOnlyNoteTests : IDisposable
{
    private readonly IShellText? _previousText = UiText.Current;

    public ProfileEditorReadOnlyNoteTests() => UiText.Current = new ShellText(language: "ko");

    public void Dispose() => UiText.Current = _previousText;

    private static readonly Dbms[] Both = [Dbms.Oracle, Dbms.Tibero];

    [Fact]
    public void Note_shows_only_for_a_tibero_profile_with_read_only_on()
    {
        var vm = new ProfileEditorViewModel(Both);

        Assert.False(vm.ShowReadOnlyServerNote); // Oracle, 꺼짐

        vm.ReadOnly = true;
        Assert.False(vm.ShowReadOnlyServerNote); // Oracle, 켬: 서버가 막는다

        vm.Dbms = Dbms.Tibero;
        Assert.True(vm.ShowReadOnlyServerNote); // Tibero, 켬

        vm.ReadOnly = false;
        Assert.False(vm.ShowReadOnlyServerNote); // Tibero, 꺼짐

        vm.ReadOnly = true;
        vm.Dbms = Dbms.Oracle;
        Assert.False(vm.ShowReadOnlyServerNote);
    }

    [Fact]
    public void Note_raises_property_changes_when_either_input_changes()
    {
        var vm = new ProfileEditorViewModel(Both);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.ReadOnly = true;
        vm.Dbms = Dbms.Tibero;

        Assert.True(changed.Count(n => n == nameof(ProfileEditorViewModel.ShowReadOnlyServerNote)) >= 2);
    }

    [Fact]
    public void Saved_profiles_show_the_note_after_loading()
    {
        Assert.True(ProfileEditorViewModel.From(new ConnectionProfile("tb", "sys", "tbhost:8629/tb6", ReadOnly: true, Dbms: Dbms.Tibero), Both).ShowReadOnlyServerNote);
        Assert.False(ProfileEditorViewModel.From(new ConnectionProfile("tb", "sys", "tbhost:8629/tb6", ReadOnly: false, Dbms: Dbms.Tibero), Both).ShowReadOnlyServerNote);
        Assert.False(ProfileEditorViewModel.From(new ConnectionProfile("dev", "scott", "db:1521/svc", ReadOnly: true), Both).ShowReadOnlyServerNote);
    }

    [Fact]
    public void Note_text_says_the_server_does_not_block_writes_and_to_use_a_select_only_account()
    {
        var vm = new ProfileEditorViewModel(Both);
        Assert.Equal(string.Empty, vm.ReadOnlyServerNoteText);
        vm.Dbms = Dbms.Tibero;
        vm.ReadOnly = true;

        Assert.Equal("Tibero 는 서버가 읽기 전용 트랜잭션에서 쓰기를 막지 않습니다. 앱이 쓰기 문장을 막지만, 확실히 막으려면 SELECT 권한만 있는 계정을 쓰세요.", vm.ReadOnlyServerNoteText);

        UiText.Current = new ShellText(language: "en");
        Assert.Contains("does not block writes", vm.ReadOnlyServerNoteText, StringComparison.Ordinal);
        Assert.Contains("only SELECT privileges", vm.ReadOnlyServerNoteText, StringComparison.Ordinal);
    }

    [Fact]
    public void Policy_denial_text_exists_in_both_languages_and_in_the_literal_table()
    {
        Assert.Equal(ExecutionPolicy.TiberoForUpdateDeniedMessage, StringCatalog.Get("ko", ExecutionPolicy.TiberoForUpdateDeniedKey));
        var en = StringCatalog.Get("en", ExecutionPolicy.TiberoForUpdateDeniedKey);
        Assert.False(string.IsNullOrWhiteSpace(en));
        Assert.True(LiteralTranslator.Table.TryGetValue(ExecutionPolicy.TiberoForUpdateDeniedMessage, out var literal));
        Assert.Equal(en, literal);
    }
}
