using Oranda.App.Features.Ai.Settings;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>독립 리뷰 지적(저장된 키가 다른 서버로 나가는 길 · 이미지 지원 재적용 · 취소 뒤 예외 · 늦은 결과 · 알림)을 막는 시험.</summary>
public class AiProfileReviewTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private static AiLlmProfile OpenAi(Guid? id = null)
    {
        var draft = AiProfilePresets.NewDraft(AiProfilePreset.OpenAi);
        return draft with { Id = id ?? Guid.NewGuid(), Name = "OpenAI", Model = "model-example" };
    }

    private static async Task<(ProfileHarness Harness, AiLlmProfile Saved, AiProfileManagerViewModel Vm)> StoredKeyAsync()
    {
        var saved = OpenAi();
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(saved));
        harness.Credentials.Put(AiProfileCredentials.KeyFor(saved.Id), "old-key-example");
        var vm = harness.Manager();
        await vm.Ready;
        Assert.True(vm.HasStoredKey);
        return (harness, saved, vm);
    }

    private static async Task AssertOldKeyStaysHomeAsync(ProfileHarness harness, AiLlmProfile saved, AiProfileManagerViewModel vm)
    {
        Assert.False(await vm.SaveProfileAsync(null));
        Assert.NotEmpty(vm.KeyError);
        Assert.Equal(0, harness.Store.SaveCalls);
        Assert.Equal(saved.BaseUrl, harness.Ai.Profiles.Single().BaseUrl);
        Assert.Equal("old-key-example", harness.Credentials.Get(AiProfileCredentials.KeyFor(saved.Id)));

        await vm.TestConnectionAsync(null); // 시험·새로고침에서도 옛 키는 새 주소로 나가지 않는다
        await vm.RefreshModelsAsync(null);
        Assert.Equal(0, harness.Probe.Calls);
    }

    // ---------------------------------------------------------------- 차단 1: 저장된 키는 저장된 서버로만

    [Fact]
    public async Task Saving_without_a_key_after_switching_to_another_provider_card_is_blocked()
    {
        var (harness, saved, vm) = await StoredKeyAsync();
        vm.SelectPreset(AiProfilePreset.ZAi); // 기본 주소였으므로 Z.AI 주소로 바뀐다
        Assert.NotEqual(saved.BaseUrl, vm.BaseUrl);
        Assert.Contains("다른 서버", vm.KeyPlaceholder);
        await AssertOldKeyStaysHomeAsync(harness, saved, vm);
    }

    [Fact]
    public async Task Saving_without_a_key_after_changing_the_address_is_blocked()
    {
        var (harness, saved, vm) = await StoredKeyAsync();
        vm.BaseUrl = "http://other.example:9000";
        await AssertOldKeyStaysHomeAsync(harness, saved, vm);
    }

    [Fact]
    public async Task Saving_without_a_key_after_switching_to_a_keyless_preset_is_blocked()
    {
        var (harness, saved, vm) = await StoredKeyAsync();
        vm.SelectPreset(AiProfilePreset.Ollama); // 키가 필요 없는 프리셋도 같다
        await AssertOldKeyStaysHomeAsync(harness, saved, vm);
    }

    [Fact]
    public async Task A_new_key_or_clearing_the_stored_key_lets_the_server_change_through_and_the_old_key_is_never_used()
    {
        var (harness, saved, vm) = await StoredKeyAsync();
        vm.BaseUrl = "http://other.example:9000";
        Assert.True(await vm.SaveProfileAsync("new-key-example"));
        Assert.Equal("new-key-example", harness.Credentials.Get(AiProfileCredentials.KeyFor(saved.Id)));
        await vm.TestConnectionAsync(null);
        Assert.Equal("new-key-example", harness.Probe.LastRequest!.ApiKey);

        var (harness2, saved2, vm2) = await StoredKeyAsync();
        vm2.SelectPreset(AiProfilePreset.Ollama);
        vm2.ClearStoredKeyCommand.Execute(null);
        Assert.True(await vm2.SaveProfileAsync(null));
        Assert.Null(harness2.Credentials.Get(AiProfileCredentials.KeyFor(saved2.Id)));
        Assert.Equal(AiProfilePresets.NewDraft(AiProfilePreset.Ollama).BaseUrl, harness2.Ai.Profiles.Single().BaseUrl);
    }

    [Fact]
    public async Task Saving_without_a_key_on_the_same_server_keeps_the_stored_key()
    {
        var (harness, saved, vm) = await StoredKeyAsync();
        vm.BaseUrl = saved.BaseUrl + "/extra"; // 경로만 다른 같은 서버
        Assert.True(await vm.SaveProfileAsync(null));
        Assert.Equal("old-key-example", harness.Credentials.Get(AiProfileCredentials.KeyFor(saved.Id)));
    }

    // ---------------------------------------------------------------- 지적 3·4·6: 이미지 지원 · 알림 · 변경 표시

    [Fact]
    public async Task Changing_the_model_reapplies_the_detected_image_support_unless_the_user_set_it()
    {
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(ProfileSamples.Example()));
        harness.Probe.ModelResult = AiModelListResult.Ok([new AiModelInfo("text-only", false), new AiModelInfo("vision-example", true)]);
        var vm = harness.Manager();
        await vm.Ready;

        vm.ModelName = "text-only";
        await vm.RefreshModelsAsync(null);
        Assert.False(vm.SupportsImages);
        vm.ModelName = "vision-example";
        Assert.True(vm.SupportsImages);
        Assert.NotEmpty(vm.ImagesNote);

        vm.SupportsImages = false; // 사용자가 직접 바꾼 값
        vm.ModelName = "text-only";
        vm.ModelName = "vision-example";
        Assert.False(vm.SupportsImages);
    }

    [Fact]
    public async Task Refreshing_the_model_list_alone_does_not_mark_the_form_changed()
    {
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(ProfileSamples.Example()));
        harness.Probe.ModelResult = AiModelListResult.Ok([new AiModelInfo("a"), new AiModelInfo("b")]);
        var vm = harness.Manager();
        await vm.Ready;
        Assert.False(vm.IsDirty);
        await vm.RefreshModelsAsync(null);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task Loading_a_profile_or_starting_a_draft_raises_CanDelete_changed()
    {
        var exampleProfile = ProfileSamples.Example();
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(exampleProfile));
        var vm = harness.Manager();
        await vm.Ready;
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await vm.NewProfileAsync();
        Assert.Contains(nameof(AiProfileManagerViewModel.CanDelete), raised);
        raised.Clear();
        await vm.SelectProfileAsync(exampleProfile.Id);
        Assert.Contains(nameof(AiProfileManagerViewModel.CanDelete), raised);
    }

    [Fact]
    public async Task The_model_hint_goes_away_once_a_name_is_typed_and_a_running_test_is_dropped_when_the_model_changes()
    {
        var harness = await ProfileHarness.CreateAsync();
        var vm = harness.Manager(startNew: true);
        vm.BaseUrl = "https://llm.example/v1";
        await vm.TestConnectionAsync(null);
        Assert.NotEmpty(vm.ModelError);
        vm.ModelName = "model-example";
        Assert.Empty(vm.ModelError);

        harness.Probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = vm.TestConnectionAsync(null);
        Assert.True(vm.IsBusy);
        vm.ModelName = "other-model";
        Assert.False(vm.IsBusy);
        harness.Probe.Gate.SetResult();
        await running.WaitAsync(Limit);
        Assert.Equal(AiResultKind.None, vm.ResultKind);
    }

    [Fact]
    public async Task Switching_the_preset_clears_the_typed_key_only_when_the_server_changes()
    {
        var harness = await ProfileHarness.CreateAsync();
        var vm = harness.Manager(startNew: true);
        var typed = "typed-example";
        vm.EnteredKeyProvider = () => typed;
        var cleared = 0;
        vm.EnteredKeyConsumed += (_, _) => cleared++;

        vm.BaseUrl = "https://llm.example/v1"; // 사용자가 고친 주소는 프리셋을 바꿔도 남는다 → 같은 서버
        vm.SelectPreset(AiProfilePreset.Gemini);
        Assert.Equal("https://llm.example/v1", vm.BaseUrl);
        Assert.Equal(0, cleared);

        vm.BaseUrl = string.Empty; // 기본 주소로 따라가면 다른 서버
        vm.SelectPreset(AiProfilePreset.OpenAi);
        Assert.Equal(1, cleared);
        Assert.Equal(AiResultKind.Info, vm.ResultKind);
        Assert.Contains("API Key", vm.ResultText);
    }

    // ---------------------------------------------------------------- 같은 서버 판정 · 키 저장소 장애

    [Theory]
    [InlineData("http://llm.example:9090")] // 포트
    [InlineData("https://llm.example:8080")] // 스킴
    [InlineData("http://other.example:8080")] // 호스트
    public async Task Saving_without_a_key_is_blocked_when_the_port_scheme_or_host_differs(string address)
    {
        var exampleProfile = ProfileSamples.Example();
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(exampleProfile));
        harness.Credentials.Put(AiProfileCredentials.KeyFor(exampleProfile.Id), "old-key-example");
        var vm = harness.Manager();
        await vm.Ready;
        vm.BaseUrl = address;
        Assert.False(await vm.SaveProfileAsync(null));
        Assert.Equal(exampleProfile.BaseUrl, harness.Ai.Profiles.Single().BaseUrl);
    }

    [Fact]
    public async Task A_failed_key_removal_stops_the_save_and_keeps_the_old_profile_and_key()
    {
        var saved = ProfileSamples.Example(); // 키가 선택인 프로필: 지워도 검사는 통과해 저장 단계까지 간다
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(saved));
        harness.Credentials.Put(AiProfileCredentials.KeyFor(saved.Id), "old-key-example");
        var vm = harness.Manager();
        await vm.Ready;
        vm.BaseUrl = "http://other.example:9000";
        vm.ClearStoredKeyCommand.Execute(null);
        harness.Credentials.FailDelete = true;

        Assert.False(await vm.SaveProfileAsync(null));
        Assert.Equal(0, harness.Store.SaveCalls);
        Assert.Equal(saved.BaseUrl, harness.Ai.Profiles.Single().BaseUrl);
        Assert.Equal("old-key-example", harness.Credentials.Get(AiProfileCredentials.KeyFor(saved.Id)));
        Assert.NotEmpty(vm.GeneralError);
    }

    [Fact]
    public async Task A_key_that_could_not_be_read_when_the_window_opened_still_blocks_a_server_change()
    {
        var saved = OpenAi();
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(saved));
        harness.Credentials.Put(AiProfileCredentials.KeyFor(saved.Id), "old-key-example");
        harness.Credentials.FailLoad = true;
        var vm = harness.Manager();
        await vm.Ready;
        Assert.False(vm.HasStoredKey); // 읽기 실패로 「키 있음」 을 알 수 없다

        vm.SelectPreset(AiProfilePreset.Ollama);
        Assert.False(await vm.SaveProfileAsync(null));
        Assert.NotEmpty(vm.KeyError);
        Assert.Equal(saved.BaseUrl, harness.Ai.Profiles.Single().BaseUrl);
        Assert.Equal("old-key-example", harness.Credentials.Get(AiProfileCredentials.KeyFor(saved.Id)));
    }

    [Fact]
    public async Task Saving_a_server_change_while_the_stored_key_is_still_being_read_is_blocked()
    {
        var saved = ProfileSamples.Example(); // 키가 선택인 프로필: 키 필수 검사에 막혀 통과하는 일이 없게 한다
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(saved));
        harness.Credentials.Put(AiProfileCredentials.KeyFor(saved.Id), "old-key-example");
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Credentials.HoldFirstLoad = hold;
        var vm = harness.Manager();
        Assert.False(vm.HasStoredKey); // 아직 읽는 중

        vm.BaseUrl = "http://other.example:9000";
        Assert.False(await vm.SaveProfileAsync(null).WaitAsync(Limit));
        Assert.Equal(saved.BaseUrl, harness.Ai.Profiles.Single().BaseUrl);

        hold.SetResult();
        await vm.Ready.WaitAsync(Limit);
        Assert.Equal("old-key-example", harness.Credentials.Get(AiProfileCredentials.KeyFor(saved.Id)));
    }

    [Fact]
    public async Task The_manual_image_choice_flag_is_set_only_by_the_image_checkbox_not_by_other_edits()
    {
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(ProfileSamples.Example()));
        harness.Probe.ModelResult = AiModelListResult.Ok([new AiModelInfo("text-only", false), new AiModelInfo("vision-example", true)]);
        var vm = harness.Manager();
        await vm.Ready;
        await vm.RefreshModelsAsync(null);

        vm.ModelName = "text-only"; // 새로고침 → text 선택 → vision 선택
        Assert.False(vm.SupportsImages);
        vm.ModelName = "vision-example";
        Assert.True(vm.SupportsImages);

        vm.Name = "고친 이름"; // 이름을 고친 뒤 text 선택
        vm.ModelName = "text-only";
        Assert.False(vm.SupportsImages);
        vm.ModelName = "vision-example";
        Assert.True(vm.SupportsImages);
    }

    // ---------------------------------------------------------------- 지적 5·7: 취소 뒤 예외 · 늦은 결과

    [Fact]
    public async Task A_non_cancel_exception_after_a_cancel_is_swallowed_for_both_queries()
    {
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(ProfileSamples.Example()));
        var vm = harness.Manager();
        await vm.Ready;
        harness.Probe.ThrowOtherAfterCancel = true;

        harness.Probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = vm.RefreshModelsAsync(null);
        vm.CancelOperation();
        await refresh.WaitAsync(Limit);

        harness.Probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var test = vm.TestConnectionAsync(null);
        vm.CancelOperation();
        await test.WaitAsync(Limit);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task A_late_result_after_switching_the_preset_changes_nothing()
    {
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(ProfileSamples.Example()));
        var vm = harness.Manager();
        await vm.Ready;
        harness.Probe.ModelResult = AiModelListResult.Ok([new AiModelInfo("late-model", true)]);
        harness.Probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var refresh = vm.RefreshModelsAsync(null);
        vm.SelectPreset(AiProfilePreset.OpenAi);
        harness.Probe.Gate.SetResult();
        await refresh.WaitAsync(Limit);
        Assert.Empty(vm.Models);
        Assert.Equal(AiResultKind.None, vm.ResultKind);

        harness.Probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var test = vm.TestConnectionAsync(null);
        vm.SelectPreset(AiProfilePreset.Gemini);
        harness.Probe.Gate.SetResult();
        await test.WaitAsync(Limit);
        Assert.Equal(AiResultKind.None, vm.ResultKind);
    }

    [Fact]
    public async Task A_normal_result_that_arrives_after_the_cancel_is_discarded()
    {
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(ProfileSamples.Example()));
        var vm = harness.Manager();
        await vm.Ready;
        harness.Probe.IgnoreCancel = true;
        harness.Probe.ModelResult = AiModelListResult.Ok([new AiModelInfo("late-model", true)]);
        harness.Probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var refresh = vm.RefreshModelsAsync(null);
        vm.SelectPreset(AiProfilePreset.OpenAi);
        harness.Probe.Gate.SetResult();
        await refresh.WaitAsync(Limit);
        Assert.Empty(vm.Models);
        Assert.Equal(AiResultKind.None, vm.ResultKind);

        harness.Probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var test = vm.TestConnectionAsync(null);
        vm.BaseUrl = "http://localhost:1234/v1";
        harness.Probe.Gate.SetResult();
        await test.WaitAsync(Limit);
        Assert.Equal(AiResultKind.None, vm.ResultKind);
    }

    // ---------------------------------------------------------------- 후속: 저장 중 잠금 · 복원 실패 · 읽기 실패 때 지우기

    private static async Task<(ProfileHarness Harness, AiLlmProfile A, AiLlmProfile B, AiProfileManagerViewModel Vm)> TwoProfilesAsync(bool keyUnknown = false)
    {
        var a = ProfileSamples.Example();
        var b = ProfileSamples.Ollama();
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(a, b));
        harness.Credentials.Put(AiProfileCredentials.KeyFor(a.Id), "old-key-example");
        harness.Credentials.FailLoad = keyUnknown; // 창을 열 때 키를 읽지 못해 「키 있음」 을 모른다: 검사는 통과하고 저장 시점 재검사만 막을 수 있다
        var vm = harness.Manager(select: a.Id);
        await vm.Ready;
        harness.Credentials.FailLoad = false;
        return (harness, a, b, vm);
    }

    [Fact]
    public async Task Moving_to_another_profile_while_the_save_waits_for_the_key_store_does_not_save_it()
    {
        var (harness, a, b, vm) = await TwoProfilesAsync(keyUnknown: true);
        vm.BaseUrl = "http://other.example:9000"; // 서버가 바뀌어 저장 시점 재검사가 키 저장소를 읽는다
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Credentials.HoldFirstLoad = hold;

        var saving = vm.SaveProfileAsync(null);
        Assert.True(vm.IsSaving);
        await vm.SelectProfileAsync(b.Id); // 키체인 프롬프트가 떠 있는 동안의 다른 조작
        vm.SelectPreset(AiProfilePreset.OpenAi);
        await vm.NewProfileAsync();
        Assert.Equal(a.Id, vm.EditingId);

        hold.SetResult();
        Assert.False(await saving.WaitAsync(Limit));
        Assert.Equal(a.BaseUrl, harness.Ai.Profiles.Single(p => p.Id == a.Id).BaseUrl);
        Assert.Equal(b, harness.Ai.Profiles.Single(p => p.Id == b.Id));
        Assert.Equal("old-key-example", harness.Credentials.Get(AiProfileCredentials.KeyFor(a.Id)));
    }

    [Fact]
    public async Task Deleting_the_profile_while_the_save_waits_for_the_key_store_does_nothing()
    {
        var (harness, a, _, vm) = await TwoProfilesAsync(keyUnknown: true);
        vm.BaseUrl = "http://other.example:9000";
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Credentials.HoldFirstLoad = hold;

        var saving = vm.SaveProfileAsync(null);
        Assert.False(await vm.DeleteProfileAsync());
        Assert.Contains(harness.Ai.Profiles, p => p.Id == a.Id);

        hold.SetResult();
        Assert.False(await saving.WaitAsync(Limit));
        Assert.Contains(harness.Ai.Profiles, p => p.Id == a.Id);
        Assert.Equal("old-key-example", harness.Credentials.Get(AiProfileCredentials.KeyFor(a.Id)));
        Assert.False(vm.IsSaving);
    }

    [Fact]
    public async Task A_cleared_key_is_restored_when_the_settings_write_fails()
    {
        var (harness, a, _, vm) = await TwoProfilesAsync();
        vm.ClearStoredKeyCommand.Execute(null);
        harness.Store.FailSave = true;

        Assert.False(await vm.SaveProfileAsync(null));
        Assert.Equal("old-key-example", harness.Credentials.Get(AiProfileCredentials.KeyFor(a.Id))); // 지운 키를 되돌렸다
        Assert.True(vm.HasStoredKey);
    }

    [Fact]
    public async Task When_the_cleared_key_cannot_be_restored_the_user_is_told_the_key_is_gone()
    {
        var (harness, a, _, vm) = await TwoProfilesAsync();
        vm.ClearStoredKeyCommand.Execute(null);
        harness.Store.FailSave = true;
        harness.Credentials.FailSave = true; // 복원(SaveAsync)도 실패

        Assert.False(await vm.SaveProfileAsync(null));
        Assert.Null(harness.Credentials.Get(AiProfileCredentials.KeyFor(a.Id)));
        Assert.False(vm.HasStoredKey);
        Assert.Contains("사라졌", vm.GeneralError);
    }

    [Fact]
    public async Task Clearing_a_key_that_could_not_be_read_works_and_a_failed_write_after_it_reports_the_loss()
    {
        var a = ProfileSamples.Example();
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(a));
        harness.Credentials.Put(AiProfileCredentials.KeyFor(a.Id), "old-key-example");
        harness.Credentials.FailLoad = true;
        var vm = harness.Manager();
        await vm.Ready;
        Assert.False(vm.HasStoredKey);
        Assert.True(vm.KeyReadFailed);
        Assert.True(vm.CanClearKey); // 읽기 실패에서도 지우기 단추가 보인다

        vm.ClearStoredKeyCommand.Execute(null);
        Assert.True(vm.ClearKeyRequested);

        harness.Store.FailSave = true; // 이전 키를 읽지 못했으므로 쓰기 실패 때 되돌릴 수 없다
        Assert.False(await vm.SaveProfileAsync(null));
        Assert.Contains("사라졌", vm.GeneralError);
        Assert.False(vm.KeyReadFailed);

        harness.Store.FailSave = false;
        harness.Credentials.FailLoad = false;
        vm.ClearStoredKeyCommand.Execute(null);
        Assert.True(await vm.SaveProfileAsync(null)); // 이제는 지우고 저장이 된다
        Assert.Null(harness.Credentials.Get(AiProfileCredentials.KeyFor(a.Id)));
    }
}
