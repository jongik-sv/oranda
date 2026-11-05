using System.Text.Json;
using Oranda.App.Features.Ai.Settings;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Events;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>LLM 프로필 관리 창 모델(set2 편집·검사, set3 서버 조회, set4 즉시 저장·키·알림).</summary>
public class AiProfileManagerTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private static void FillValid(AiProfileManagerViewModel vm, string name = "프로필", string url = "https://llm.example/v1", string model = "model-example")
    {
        vm.Name = name;
        vm.BaseUrl = url;
        vm.ModelName = model;
    }

    // ---------------------------------------------------------------- set2: 편집·검사

    [Fact]
    public async Task A_new_profile_starts_from_the_custom_preset_and_lists_seven_preset_cards_without_unknown()
    {
        var harness = await ProfileHarness.CreateAsync();
        var vm = harness.Manager();

        Assert.Equal(7, vm.PresetCards.Count);
        Assert.Equal(["Anthropic", "Z.AI", "Google Gemini", "OpenAI", "Ollama", "LM Studio", "직접 입력"], vm.PresetCards.Select(c => c.Title));
        Assert.DoesNotContain(vm.PresetCards, c => c.Preset == AiProfilePreset.Unknown);
        Assert.Equal(AiProfilePreset.Custom, vm.Preset);
        Assert.True(vm.PresetCards.Single(c => c.IsSelected).Preset == AiProfilePreset.Custom);
        Assert.True(vm.FormatSelectable);
        Assert.False(vm.CanDelete);
    }

    [Fact]
    public async Task Choosing_a_preset_applies_its_new_draft_defaults()
    {
        var harness = await ProfileHarness.CreateAsync();
        var vm = harness.Manager();

        vm.SelectPreset(AiProfilePreset.Ollama);
        Assert.Equal(("http://localhost:11434/v1", AiApiFormat.OpenAi, false), (vm.BaseUrl, vm.Format, vm.FormatSelectable));
        Assert.Equal("Ollama", vm.Name);
        Assert.Contains("키가 필요 없습니다", vm.KeyPlaceholder);

        vm.SelectPreset(AiProfilePreset.ZAi);
        Assert.Equal(("https://api.z.ai/api/anthropic", AiApiFormat.Anthropic), (vm.BaseUrl, vm.Format));
    }

    [Fact]
    public async Task Switching_presets_does_not_silently_lose_what_the_user_typed()
    {
        var harness = await ProfileHarness.CreateAsync();
        var vm = harness.Manager();
        vm.SelectPreset(AiProfilePreset.Ollama);
        vm.BaseUrl = "http://llm.example:9000/v1"; // 사용자가 고친 주소
        vm.Name = "내 서버";
        vm.ModelName = "my-model";
        vm.MaxOutputTokensText = "4096";

        vm.SelectPreset(AiProfilePreset.OpenAi);

        Assert.Equal("http://llm.example:9000/v1", vm.BaseUrl);
        Assert.Equal(("내 서버", "my-model", "4096"), (vm.Name, vm.ModelName, vm.MaxOutputTokensText));
        Assert.Equal(AiApiFormat.OpenAi, vm.Format); // 형식은 사용자가 고를 수 없는 프리셋이므로 프리셋 값을 따른다
    }

    [Fact]
    public async Task Api_format_can_be_chosen_only_for_the_custom_preset()
    {
        var harness = await ProfileHarness.CreateAsync();
        var vm = harness.Manager();
        vm.IsFormatOpenAi = true;
        Assert.Equal(AiApiFormat.OpenAi, vm.Format);
        Assert.Contains("앱이 직접 호출", vm.FormatHint);
        vm.IsFormatAnthropic = true;
        Assert.Contains("claude 실행 파일", vm.FormatHint);

        vm.SelectPreset(AiProfilePreset.Gemini);
        vm.IsFormatAnthropic = true; // 무시된다
        Assert.Equal(AiApiFormat.OpenAi, vm.Format);
    }

    [Fact]
    public async Task Validation_reports_each_bad_field_and_blocks_the_save_without_writing_anything()
    {
        var harness = await ProfileHarness.CreateAsync();
        var vm = harness.Manager();
        vm.Name = "  ";
        vm.BaseUrl = "ftp://llm.example";
        vm.ModelName = "";
        vm.MaxInputTokensText = "0";
        vm.MaxOutputTokensText = "abc";

        Assert.False(await vm.SaveProfileAsync(null));

        Assert.NotEmpty(vm.NameError);
        Assert.NotEmpty(vm.UrlError);
        Assert.NotEmpty(vm.ModelError);
        Assert.NotEmpty(vm.InputTokensError);
        Assert.NotEmpty(vm.OutputTokensError);
        Assert.Empty(harness.Ai.Profiles);
        Assert.Equal(0, harness.Store.SaveCalls);
        Assert.Empty(harness.Bus.Published);

        // 고치면 오류 글이 따라서 사라진다.
        FillValid(vm);
        vm.MaxInputTokensText = "1000";
        vm.MaxOutputTokensText = "100";
        Assert.All(new[] { vm.NameError, vm.UrlError, vm.ModelError, vm.InputTokensError, vm.OutputTokensError }, e => Assert.Empty(e));
    }

    [Theory]
    [InlineData("http://user:pw@llm.example")]
    [InlineData("https://llm.example/v1?x=1")]
    [InlineData("llm.example")]
    public async Task Urls_with_secrets_or_without_a_scheme_are_rejected(string url)
    {
        var harness = await ProfileHarness.CreateAsync();
        var vm = harness.Manager();
        FillValid(vm, url: url);

        Assert.False(vm.Validate(null));
        Assert.NotEmpty(vm.UrlError);
    }

    [Fact]
    public async Task Presets_that_need_a_key_refuse_to_save_without_one_but_local_servers_do_not()
    {
        var harness = await ProfileHarness.CreateAsync();
        var vm = harness.Manager();
        vm.SelectPreset(AiProfilePreset.OpenAi);
        vm.ModelName = "gpt-example";

        Assert.False(await vm.SaveProfileAsync(""));
        Assert.NotEmpty(vm.KeyError);
        Assert.Empty(harness.Ai.Profiles);

        vm.SelectPreset(AiProfilePreset.Ollama);
        vm.ModelName = "gemma-example";
        Assert.True(await vm.SaveProfileAsync(null));
        Assert.Empty(vm.KeyError);
    }

    [Fact]
    public async Task Local_ai_is_off_by_default_even_for_a_local_preset_and_is_saved_with_its_address()
    {
        var harness = await ProfileHarness.CreateAsync();
        var vm = harness.Manager();

        vm.SelectPreset(AiProfilePreset.Ollama);
        Assert.False(vm.LocalAi); // 프리셋·주소로 판단하지 않는다
        vm.SelectPreset(AiProfilePreset.Custom);
        FillValid(vm, url: "http://192.0.2.10:8080/v1");
        Assert.False(vm.ShowLocalAiPlainTextWarning);

        vm.LocalAi = true;
        Assert.True(vm.ShowLocalAiPlainTextWarning); // 사내 서버의 plain http: 값이 암호화 없이 간다
        Assert.True(await vm.SaveProfileAsync("secret"));

        var saved = Assert.Single(harness.Ai.Profiles);
        Assert.Equal("http://192.0.2.10:8080/v1", saved.LocalAiConfirmedUrl);
        Assert.True(saved.IsLocalAi);
        Assert.Contains("로컬 AI", Assert.Single(vm.Items).Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_the_address_turns_local_ai_off_and_says_so()
    {
        var harness = await ProfileHarness.CreateAsync();
        var vm = harness.Manager();
        FillValid(vm, url: "http://192.0.2.10:8080/v1");
        vm.LocalAi = true;

        vm.BaseUrl = " http://192.0.2.10:8080/v1 "; // 앞뒤 공백은 같은 주소다
        Assert.True(vm.LocalAi);

        vm.BaseUrl = "http://192.0.2.11:8080/v1";
        Assert.False(vm.LocalAi);
        Assert.Contains("로컬 AI", vm.ResultText, StringComparison.Ordinal);

        Assert.True(await vm.SaveProfileAsync("secret"));
        var saved = Assert.Single(harness.Ai.Profiles);
        Assert.Null(saved.LocalAiConfirmedUrl);
        Assert.False(saved.IsLocalAi);
    }

    [Fact]
    public async Task A_preset_that_changes_the_address_turns_local_ai_off_and_the_notice_stays()
    {
        var harness = await ProfileHarness.CreateAsync();
        var vm = harness.Manager();
        vm.SelectPreset(AiProfilePreset.Ollama);
        vm.LocalAi = true;

        vm.SelectPreset(AiProfilePreset.LmStudio);

        Assert.False(vm.LocalAi);
        Assert.True(vm.ResultIsInfo);
        Assert.Contains("로컬 AI", vm.ResultText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Selecting_a_profile_shows_its_own_local_ai_state_and_the_toggle_marks_the_form_dirty()
    {
        var harness = await ProfileHarness.CreateAsync();
        var vm = harness.Manager();
        FillValid(vm, name: "로컬", url: "http://192.0.2.10:8080/v1");
        vm.LocalAi = true;
        Assert.True(vm.IsDirty);
        Assert.True(await vm.SaveProfileAsync("k1"));
        var localId = vm.EditingId;
        await vm.NewProfileAsync();
        Assert.False(vm.LocalAi); // 새 초안은 앞 프로필의 지정을 잇지 않는다
        FillValid(vm, name: "외부", url: "https://llm.example/v1");
        Assert.True(await vm.SaveProfileAsync("k2"));

        await vm.SelectProfileAsync(localId);
        Assert.True(vm.LocalAi);
        Assert.False(vm.IsDirty);

        vm.LocalAi = false;
        Assert.True(vm.IsDirty);
        Assert.True(await vm.SaveProfileAsync(null));
        Assert.False(harness.Ai.Profiles.Single(p => p.Id == localId).IsLocalAi);
    }

    [Fact]
    public async Task Two_local_profiles_each_keep_their_own_address_when_switching_between_them()
    {
        var harness = await ProfileHarness.CreateAsync();
        var vm = harness.Manager();
        FillValid(vm, name: "로컬1", url: "http://192.0.2.10:8080/v1");
        vm.LocalAi = true;
        Assert.True(await vm.SaveProfileAsync("k1"));
        var first = vm.EditingId;
        await vm.NewProfileAsync();
        FillValid(vm, name: "로컬2", url: "http://192.0.2.11:8080/v1");
        vm.LocalAi = true;
        Assert.True(await vm.SaveProfileAsync("k2"));

        await vm.SelectProfileAsync(first); // 켜짐 → 켜짐: 값은 그대로지만 기억한 주소는 바뀌어야 한다
        vm.Name = "이름만 고침";
        Assert.True(await vm.SaveProfileAsync(null));

        var saved = harness.Ai.Profiles.Single(p => p.Id == first);
        Assert.Equal("http://192.0.2.10:8080/v1", saved.LocalAiConfirmedUrl);
        Assert.True(saved.IsLocalAi);
    }

    [Fact]
    public async Task The_plain_http_warning_follows_the_url()
    {
        var harness = await ProfileHarness.CreateAsync();
        var vm = harness.Manager();
        vm.BaseUrl = "http://llm.example:8080";
        Assert.True(vm.ShowPlainTextWarning);
        vm.BaseUrl = "http://localhost:8080";
        Assert.False(vm.ShowPlainTextWarning);
        vm.BaseUrl = "https://llm.example";
        Assert.False(vm.ShowPlainTextWarning);
    }

    [Fact]
    public async Task A_profile_this_build_does_not_know_can_only_be_deleted_and_other_profiles_survive_saves()
    {
        var unknown = ProfileSamples.Example() with { Preset = AiProfilePreset.Unknown, Name = "미래 프로필" };
        var exampleProfile = ProfileSamples.Example();
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(unknown, exampleProfile));

        var vm = harness.Manager(select: unknown.Id);
        Assert.False(vm.IsEditable);
        Assert.False(vm.CanSave);
        Assert.NotEmpty(vm.InvalidReason);
        Assert.False(await vm.SaveProfileAsync(null));
        Assert.True(vm.CanDelete);

        await vm.SelectProfileAsync(exampleProfile.Id);
        vm.Name = "이름 바꿈";
        Assert.True(await vm.SaveProfileAsync(null));
        Assert.Equal(["미래 프로필", "이름 바꿈"], harness.Ai.Profiles.Select(p => p.Name));
        Assert.Equal(AiProfilePreset.Unknown, harness.Ai.Profiles[0].Preset);
    }

    // ---------------------------------------------------------------- set4: 저장·키·알림

    [Fact]
    public async Task Saving_writes_the_profile_immediately_stores_the_key_outside_the_settings_and_announces_the_change()
    {
        var harness = await ProfileHarness.CreateAsync();
        var applied = new List<(AiSettings Latest, Guid? Deleted, Guid? Created)>();
        var vm = harness.Manager(applied: (l, d, c) => applied.Add((l, d, c)), startNew: true);
        FillValid(vm, name: "사내 서버", url: "http://llm.example:8080", model: "example-model");
        vm.SupportsImages = true;
        vm.MaxInputTokensText = "32000";
        var consumed = false;
        vm.EnteredKeyConsumed += (_, _) => consumed = true;

        Assert.True(await vm.SaveProfileAsync("  secret-value-example  "));

        var saved = Assert.Single(harness.Ai.Profiles);
        Assert.Equal(("사내 서버", AiProfilePreset.Custom, AiApiFormat.Anthropic, "http://llm.example:8080", "example-model", 32000, true),
            (saved.Name, saved.Preset, saved.Format, saved.BaseUrl, saved.Model, saved.MaxInputTokens, saved.SupportsImages));
        Assert.Equal(1, harness.Store.SaveCalls);
        Assert.Equal("secret-value-example", harness.Credentials.Get(AiProfileCredentials.KeyFor(saved.Id)));
        Assert.DoesNotContain("secret-value-example", JsonSerializer.Serialize(harness.Store.Saved.Ai));
        Assert.Equal("ai", Assert.Single(harness.Bus.PublishedOf<SettingsChanged>()).Section);
        Assert.Equal(saved.Id, Assert.Single(applied).Created);
        Assert.True(vm.HasStoredKey);
        Assert.True(consumed);
        Assert.Equal(AiResultKind.Success, vm.ResultKind);
        Assert.True(vm.CanDelete); // 저장 뒤에는 저장된 프로필을 편집 중이다
        Assert.DoesNotContain("secret-value-example", vm.ToString());
    }

    [Fact]
    public async Task Saving_an_existing_profile_with_an_empty_key_field_keeps_the_stored_key()
    {
        var exampleProfile = ProfileSamples.Example();
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(exampleProfile));
        harness.Credentials.Put(AiProfileCredentials.KeyFor(exampleProfile.Id), "stored-example");
        var vm = harness.Manager();
        await vm.Ready;
        Assert.True(vm.HasStoredKey);
        Assert.Contains("비워 두면", vm.KeyPlaceholder);

        vm.ModelName = "other-model";
        Assert.True(await vm.SaveProfileAsync(""));

        Assert.Equal("stored-example", harness.Credentials.Get(AiProfileCredentials.KeyFor(exampleProfile.Id)));
        Assert.Equal("other-model", harness.Ai.Profiles[0].Model);
    }

    [Fact]
    public async Task Clearing_the_stored_key_is_a_separate_action_and_a_typed_key_replaces_it()
    {
        var profile = ProfileSamples.Example() with { Preset = AiProfilePreset.Custom };
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(profile));
        var name = AiProfileCredentials.KeyFor(profile.Id);
        harness.Credentials.Put(name, "stored-example");
        var vm = harness.Manager();
        await vm.Ready;

        vm.ClearStoredKeyCommand.Execute(null);
        Assert.True(vm.ClearKeyRequested);
        Assert.Contains("지웁니다", vm.KeyPlaceholder);
        Assert.True(await vm.SaveProfileAsync(null));
        Assert.Null(harness.Credentials.Get(name));
        Assert.False(vm.HasStoredKey);

        Assert.True(await vm.SaveProfileAsync("new-example"));
        Assert.Equal("new-example", harness.Credentials.Get(name));
    }

    [Fact]
    public async Task A_failed_key_write_saves_no_profile()
    {
        var harness = await ProfileHarness.CreateAsync();
        harness.Credentials.FailSave = true;
        var vm = harness.Manager();
        FillValid(vm);

        Assert.False(await vm.SaveProfileAsync("secret-example"));

        Assert.NotEmpty(vm.GeneralError);
        Assert.DoesNotContain("secret-example", vm.GeneralError);
        Assert.Empty(harness.Ai.Profiles);
        Assert.Empty(harness.Bus.Published);
    }

    [Fact]
    public async Task A_failed_settings_write_rolls_back_a_new_key_and_restores_a_replaced_one()
    {
        var exampleProfile = ProfileSamples.Example();
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(exampleProfile));
        var oldName = AiProfileCredentials.KeyFor(exampleProfile.Id);
        harness.Credentials.Put(oldName, "old-example");
        var vm = harness.Manager();
        await vm.Ready;
        harness.Store.FailSave = true;

        vm.Name = "바뀐 이름";
        Assert.False(await vm.SaveProfileAsync("replacement-example"));
        Assert.Equal("old-example", harness.Credentials.Get(oldName)); // 옛 키 복원
        Assert.Equal("예제 LLM", harness.Ai.Profiles[0].Name); // 메모리 값도 되돌림
        Assert.NotEmpty(vm.GeneralError);

        var fresh = harness.Manager(startNew: true);
        FillValid(fresh);
        Assert.False(await fresh.SaveProfileAsync("fresh-example"));
        Assert.DoesNotContain(harness.Credentials.Keys, k => k != oldName); // 고아 키가 남지 않는다
        Assert.Single(harness.Ai.Profiles);
        Assert.Empty(harness.Bus.Published);
    }

    [Fact]
    public async Task A_read_only_settings_file_refuses_to_save_instead_of_pretending_to_succeed()
    {
        var harness = await ProfileHarness.CreateAsync(readOnly: true);
        Assert.True(harness.Settings.IsReadOnly);
        var vm = harness.Manager();
        FillValid(vm);

        Assert.False(await vm.SaveProfileAsync("secret-example"));

        Assert.Contains("읽기 전용", vm.GeneralError);
        Assert.Empty(harness.Ai.Profiles);
        Assert.Empty(harness.Credentials.Keys);
        Assert.Equal(0, harness.Store.SaveCalls);
        Assert.Empty(harness.Bus.Published);
    }

    [Fact]
    public async Task Saving_without_a_credential_store_is_refused_when_a_key_is_needed()
    {
        var harness = await ProfileHarness.CreateAsync(withCredentials: false);
        var vm = harness.Manager();
        vm.SelectPreset(AiProfilePreset.OpenAi);
        vm.ModelName = "gpt-example";

        Assert.False(await vm.SaveProfileAsync("secret-example"));
        Assert.NotEmpty(vm.KeyError);
        Assert.Empty(harness.Ai.Profiles);
    }

    [Fact]
    public async Task Deleting_asks_first_then_removes_the_profile_and_its_key_and_resets_an_active_one()
    {
        var exampleProfile = ProfileSamples.Example();
        var ollama = ProfileSamples.Ollama();
        var harness = await ProfileHarness.CreateAsync(new AiSettings { Profiles = [exampleProfile, ollama], ActiveProfileId = exampleProfile.Id });
        harness.Credentials.Put(AiProfileCredentials.KeyFor(exampleProfile.Id), "stored-example");
        var page = harness.Page();
        var asked = new List<string>();
        var answer = false;
        var vm = harness.Manager(confirm: m => { asked.Add(m); return Task.FromResult(answer); }, applied: page.ApplyStoreChange, select: exampleProfile.Id);
        await vm.Ready;

        Assert.False(await vm.DeleteProfileAsync()); // 거절
        Assert.Equal(2, harness.Ai.Profiles.Count);
        Assert.Contains("예제 LLM", Assert.Single(asked));

        answer = true;
        Assert.True(await vm.DeleteProfileAsync());

        Assert.Equal([ollama], harness.Ai.Profiles);
        Assert.Null(harness.Ai.ActiveProfileId);
        Assert.Null(harness.Credentials.Get(AiProfileCredentials.KeyFor(exampleProfile.Id)));
        Assert.Single(harness.Bus.PublishedOf<SettingsChanged>());
        Assert.Equal(AiActiveKind.ClaudeCode, page.ActiveKind);
        Assert.Contains("Claude Code", page.Notice);
        Assert.Equal("로컬 Ollama", vm.Name); // 남은 프로필로 옮겨 간다
    }

    [Fact]
    public async Task Deleting_an_inactive_profile_leaves_the_pending_page_selection_alone()
    {
        var exampleProfile = ProfileSamples.Example();
        var ollama = ProfileSamples.Ollama();
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(exampleProfile, ollama));
        var page = harness.Page();
        page.Choose(page.Choices.Single(c => c.ProfileId == ollama.Id)); // 아직 확인 전인 선택
        var vm = harness.Manager(applied: page.ApplyStoreChange, select: exampleProfile.Id);

        Assert.True(await vm.DeleteProfileAsync());

        Assert.Equal(ollama.Id, page.ActiveProfileId);
        Assert.Empty(page.Notice);
    }

    [Fact]
    public async Task Delete_is_refused_on_a_read_only_file_and_a_failed_key_removal_is_reported_after_the_profile_is_gone()
    {
        var exampleProfile = ProfileSamples.Example();
        var readOnly = await ProfileHarness.CreateAsync(ProfileSamples.With(exampleProfile), readOnly: true);
        var refused = readOnly.Manager();
        Assert.False(await refused.DeleteProfileAsync());
        Assert.Contains("읽기 전용", refused.GeneralError);
        Assert.Single(readOnly.Ai.Profiles);

        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(exampleProfile));
        harness.Credentials.Put(AiProfileCredentials.KeyFor(exampleProfile.Id), "stored-example");
        harness.Credentials.FailDelete = true;
        var vm = harness.Manager();
        Assert.True(await vm.DeleteProfileAsync());
        Assert.Empty(harness.Ai.Profiles);
        Assert.NotEmpty(vm.GeneralError); // 키가 남았다는 안내
    }

    [Fact]
    public async Task A_failed_settings_write_on_delete_keeps_the_profile_and_its_key()
    {
        var exampleProfile = ProfileSamples.Example();
        var harness = await ProfileHarness.CreateAsync(new AiSettings { Profiles = [exampleProfile], ActiveProfileId = exampleProfile.Id });
        harness.Credentials.Put(AiProfileCredentials.KeyFor(exampleProfile.Id), "stored-example");
        var vm = harness.Manager();
        harness.Store.FailSave = true;

        Assert.False(await vm.DeleteProfileAsync());

        Assert.Equal([exampleProfile], harness.Ai.Profiles);
        Assert.Equal(exampleProfile.Id, harness.Ai.ActiveProfileId);
        Assert.Equal("stored-example", harness.Credentials.Get(AiProfileCredentials.KeyFor(exampleProfile.Id)));
    }

    [Fact]
    public async Task Leaving_with_unsaved_edits_asks_before_discarding()
    {
        var exampleProfile = ProfileSamples.Example();
        var ollama = ProfileSamples.Ollama();
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(exampleProfile, ollama));
        var allow = false;
        var asked = 0;
        var vm = harness.Manager(confirm: _ => { asked++; return Task.FromResult(allow); }, select: exampleProfile.Id);
        vm.Name = "고친 이름";

        await vm.SelectProfileAsync(ollama.Id);
        Assert.Equal(("고친 이름", 1), (vm.Name, asked));

        allow = true;
        await vm.SelectProfileAsync(ollama.Id);
        Assert.Equal("로컬 Ollama", vm.Name);
    }

    [Fact]
    public async Task An_unknown_image_support_stays_unknown_until_the_user_decides()
    {
        var profile = ProfileSamples.Ollama(); // SupportsImages = null(모름)
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(profile));
        var vm = harness.Manager();
        Assert.False(vm.ImagesChecked);

        vm.Name = "이름만 바꿈";
        Assert.True(await vm.SaveProfileAsync(null));
        Assert.Null(harness.Ai.Profiles[0].SupportsImages);

        vm.ImagesChecked = true;
        Assert.True(await vm.SaveProfileAsync(null));
        Assert.True(harness.Ai.Profiles[0].SupportsImages);
    }

    [Fact]
    public async Task An_unreadable_stored_key_does_not_break_selecting_or_replacing_it()
    {
        var exampleProfile = ProfileSamples.Example();
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(exampleProfile));
        harness.Credentials.Put(AiProfileCredentials.KeyFor(exampleProfile.Id), "stored-example");
        harness.Credentials.FailLoad = true;
        var vm = harness.Manager();
        await vm.Ready; // 예외로 새지 않는다
        Assert.False(vm.HasStoredKey);

        Assert.True(await vm.SaveProfileAsync("new-example")); // 옛 키를 못 읽어도 새 키로 고치는 저장은 된다
        harness.Credentials.FailLoad = false;
        Assert.Equal("new-example", harness.Credentials.Get(AiProfileCredentials.KeyFor(exampleProfile.Id)));

        harness.Credentials.FailLoad = true;
        vm.ModelName = "m";
        await vm.TestConnectionAsync(null); // 키 읽기 실패가 IsBusy 를 남기지 않는다
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task A_failed_key_restore_is_reported_together_with_the_settings_failure()
    {
        var exampleProfile = ProfileSamples.Example();
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(exampleProfile));
        harness.Credentials.Put(AiProfileCredentials.KeyFor(exampleProfile.Id), "old-example");
        var vm = harness.Manager();
        await vm.Ready;
        harness.Store.FailSave = true;
        harness.Credentials.FailSaveAfter = 1; // 첫 쓰기(새 키)만 성공하고 복원 쓰기는 실패

        Assert.False(await vm.SaveProfileAsync("new-example"));

        Assert.Contains("설정 파일에 저장하지 못했습니다", vm.GeneralError);
        Assert.Contains("되돌리지 못했습니다", vm.GeneralError);
    }

    [Fact]
    public async Task The_stored_key_is_not_sent_to_a_different_server_than_the_saved_profile()
    {
        var exampleProfile = ProfileSamples.Example();
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(exampleProfile));
        harness.Credentials.Put(AiProfileCredentials.KeyFor(exampleProfile.Id), "stored-example");
        var vm = harness.Manager();
        await vm.Ready;

        vm.BaseUrl = "http://other.example:9000";
        await vm.TestConnectionAsync(null);
        Assert.Equal(0, harness.Probe.Calls);
        Assert.Equal(AiResultKind.Failure, vm.ResultKind);

        await vm.TestConnectionAsync("typed-example"); // 입력한 키는 보낸다
        Assert.Equal("typed-example", harness.Probe.LastRequest!.ApiKey);

        vm.BaseUrl = "http://llm.example:8080/v1"; // 같은 서버(경로만 다름)는 저장된 키를 쓴다
        await vm.TestConnectionAsync(null);
        Assert.Equal("stored-example", harness.Probe.LastRequest!.ApiKey);
    }

    [Fact]
    public async Task Switching_the_preset_or_editing_the_address_discards_a_running_probe()
    {
        var harness = await ProfileHarness.CreateAsync();
        harness.Probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = harness.Manager();
        FillValid(vm);

        var running = vm.TestConnectionAsync(null);
        Assert.True(vm.IsBusy);
        vm.SelectPreset(AiProfilePreset.Ollama);
        await running.WaitAsync(Limit);
        Assert.False(vm.IsBusy);
        Assert.Equal(AiResultKind.None, vm.ResultKind);

        var second = vm.RefreshModelsAsync(null);
        vm.BaseUrl = "http://localhost:1234/v1";
        await second.WaitAsync(Limit);
        Assert.False(vm.IsBusy);
        Assert.Empty(vm.Models);
    }

    [Fact]
    public async Task A_cancellation_nobody_asked_for_is_shown_as_a_failure()
    {
        var harness = await ProfileHarness.CreateAsync();
        harness.Probe.ThrowUnrequestedCancel = true;
        var vm = harness.Manager();
        FillValid(vm);

        await vm.TestConnectionAsync(null);

        Assert.Equal(AiResultKind.Failure, vm.ResultKind);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task A_new_profile_is_selected_on_the_page_only_when_opened_through_new_profile()
    {
        var harness = await ProfileHarness.CreateAsync();
        var viaManage = harness.Page();
        var manage = harness.Manager(applied: viaManage.ApplyStoreChange, startNew: false);
        FillValid(manage);
        Assert.True(await manage.SaveProfileAsync(null));
        Assert.Null(viaManage.ActiveProfileId); // 「프로필 관리」 로 만든 프로필은 선택을 바꾸지 않는다

        var viaNew = harness.Page();
        var created = harness.Manager(applied: viaNew.ApplyStoreChange, startNew: true);
        FillValid(created, name: "다른 프로필");
        Assert.True(await created.SaveProfileAsync(null));
        Assert.Equal(created.EditingId, viaNew.ActiveProfileId);
    }

    [Fact]
    public async Task Deleting_the_profile_the_saved_settings_use_tells_the_page_even_if_another_is_pending()
    {
        var exampleProfile = ProfileSamples.Example();
        var ollama = ProfileSamples.Ollama();
        var harness = await ProfileHarness.CreateAsync(new AiSettings { Profiles = [exampleProfile, ollama], ActiveProfileId = exampleProfile.Id });
        var page = harness.Page();
        page.Choose(page.Choices.Single(c => c.ProfileId == ollama.Id)); // 미확정 선택
        var asked = new List<string>();
        var vm = harness.Manager(confirm: m => { asked.Add(m); return Task.FromResult(true); }, applied: page.ApplyStoreChange, select: exampleProfile.Id);

        Assert.True(await vm.DeleteProfileAsync());

        Assert.Contains("사용 중", Assert.Single(asked));
        Assert.Null(harness.Ai.ActiveProfileId);
        Assert.NotEmpty(page.Notice);
        Assert.Equal(ollama.Id, page.ActiveProfileId);
    }

    [Fact]
    public async Task A_typed_key_counts_as_an_unsaved_change_and_a_late_confirmation_does_not_delete_after_closing()
    {
        var exampleProfile = ProfileSamples.Example();
        var ollama = ProfileSamples.Ollama();
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(exampleProfile, ollama));
        var asked = 0;
        var vm = harness.Manager(confirm: _ => { asked++; return Task.FromResult(false); }, select: exampleProfile.Id);
        vm.NotifyKeyTyped(true);
        await vm.SelectProfileAsync(ollama.Id);
        Assert.Equal(1, asked);

        var gate = new TaskCompletionSource<bool>();
        var slow = harness.Manager(confirm: _ => gate.Task, select: exampleProfile.Id);
        var deleting = slow.DeleteProfileAsync();
        slow.Dispose(); // 확인을 기다리는 동안 창이 닫힌다
        gate.SetResult(true);
        Assert.False(await deleting);
        Assert.Equal(2, harness.Ai.Profiles.Count);
    }

    // ---------------------------------------------------------------- set3: 모델 새로고침 · 연결 테스트

    [Fact]
    public async Task Without_a_probe_service_the_probe_buttons_are_disabled_and_the_model_name_is_typed()
    {
        var harness = await ProfileHarness.CreateAsync(withProbe: false);
        var vm = harness.Manager();
        FillValid(vm);

        Assert.False(vm.CanProbe);
        Assert.False(vm.HasProbe);
        await vm.RefreshModelsAsync(null);
        await vm.TestConnectionAsync(null);
        Assert.Equal(AiResultKind.None, vm.ResultKind);
        Assert.True(await vm.SaveProfileAsync(null)); // 모델명 직접 입력만으로 저장된다
    }

    [Fact]
    public async Task Refreshing_models_fills_the_list_and_the_image_checkbox_which_the_user_can_still_change()
    {
        var harness = await ProfileHarness.CreateAsync();
        harness.Probe.ModelResult = AiModelListResult.Ok([new AiModelInfo("text-only"), new AiModelInfo("vision-example", true)]);
        var vm = harness.Manager();
        vm.BaseUrl = "https://llm.example/v1";
        vm.ModelName = "vision-example";

        await vm.RefreshModelsAsync("key-example");

        Assert.Equal(["text-only", "vision-example"], vm.Models);
        Assert.True(vm.SupportsImages);
        Assert.NotEmpty(vm.ImagesNote);
        Assert.Equal(AiResultKind.Success, vm.ResultKind);
        Assert.Equal("key-example", harness.Probe.LastRequest!.ApiKey);
        Assert.DoesNotContain("key-example", harness.Probe.LastRequest.ToString());

        vm.SupportsImages = false; // 사용자가 바꾼다
        Assert.False(vm.SupportsImages);
        Assert.True(await vm.SaveProfileAsync(null));
        Assert.False(harness.Ai.Profiles[0].SupportsImages);
    }

    [Fact]
    public async Task A_failed_refresh_is_shown_as_a_result_and_keeps_what_was_typed()
    {
        var harness = await ProfileHarness.CreateAsync();
        harness.Probe.ModelResult = AiModelListResult.Fail("HTTP 401");
        var vm = harness.Manager();
        FillValid(vm, model: "typed-model");

        await vm.RefreshModelsAsync(null);

        Assert.Equal(AiResultKind.Failure, vm.ResultKind);
        Assert.Contains("HTTP 401", vm.ResultText);
        Assert.Equal("typed-model", vm.ModelName);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Connection_test_works_before_saving_and_shows_success_and_failure_with_the_elapsed_time()
    {
        var harness = await ProfileHarness.CreateAsync();
        var vm = harness.Manager();
        FillValid(vm);

        harness.Probe.TestResult = new AiConnectionTestResult(true, TimeSpan.FromSeconds(1.04), "이미지 입력 가능");
        await vm.TestConnectionAsync(null);
        Assert.Equal(AiResultKind.Success, vm.ResultKind);
        Assert.Equal("연결 성공 · 1.0 초 · 이미지 입력 가능", vm.ResultText);
        Assert.Empty(harness.Ai.Profiles); // 저장하지 않았다

        harness.Probe.TestResult = new AiConnectionTestResult(false, TimeSpan.FromSeconds(0.25), "HTTP 500");
        await vm.TestConnectionAsync(null);
        Assert.Equal(AiResultKind.Failure, vm.ResultKind);
        Assert.Equal("연결 실패 · 0.3 초 · HTTP 500", vm.ResultText);
    }

    [Fact]
    public async Task Connection_test_requires_a_valid_url_and_a_model_and_uses_the_stored_key_when_the_field_is_empty()
    {
        var exampleProfile = ProfileSamples.Example();
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(exampleProfile));
        harness.Credentials.Put(AiProfileCredentials.KeyFor(exampleProfile.Id), "stored-example");
        var vm = harness.Manager();
        await vm.Ready;

        vm.ModelName = "";
        await vm.TestConnectionAsync(null);
        Assert.NotEmpty(vm.ModelError);
        Assert.Equal(0, harness.Probe.Calls);

        vm.ModelName = "m";
        await vm.TestConnectionAsync(null);
        Assert.Equal("stored-example", harness.Probe.LastRequest!.ApiKey);

        vm.ClearStoredKeyCommand.Execute(null); // 지우기를 눌렀으면 저장된 키를 쓰지 않는다
        await vm.TestConnectionAsync(null);
        Assert.Null(harness.Probe.LastRequest!.ApiKey);
    }

    [Fact]
    public async Task Stopping_closing_or_switching_cancels_a_running_probe_and_late_results_are_ignored()
    {
        var exampleProfile = ProfileSamples.Example();
        var ollama = ProfileSamples.Ollama();
        var harness = await ProfileHarness.CreateAsync(ProfileSamples.With(exampleProfile, ollama));
        harness.Probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = harness.Manager(select: exampleProfile.Id);

        var running = vm.TestConnectionAsync(null);
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanProbe); // 도는 동안 같은 작업을 겹쳐 시작하지 않는다
        vm.StopProbeCommand.Execute(null);
        await running; // 취소는 예외로 새지 않는다
        Assert.False(vm.IsBusy);
        Assert.Equal(AiResultKind.Info, vm.ResultKind);

        // 다른 프로필로 옮기면 앞 프로필의 늦은 결과가 화면을 바꾸지 못한다.
        harness.Probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = vm.RefreshModelsAsync(null);
        await vm.SelectProfileAsync(ollama.Id);
        harness.Probe.Gate.SetResult();
        await late.WaitAsync(Limit);
        Assert.Empty(vm.Models);
        Assert.Equal(AiResultKind.None, vm.ResultKind);

        // 창을 닫으면(Dispose) 도는 조회가 멈춘다.
        harness.Probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = vm.TestConnectionAsync(null);
        vm.Dispose();
        await pending.WaitAsync(Limit);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Saving_while_a_probe_runs_stops_it_first()
    {
        var harness = await ProfileHarness.CreateAsync();
        harness.Probe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = harness.Manager();
        FillValid(vm);

        var running = vm.TestConnectionAsync(null);
        Assert.True(await vm.SaveProfileAsync(null));
        await running.WaitAsync(Limit);

        Assert.False(vm.IsBusy);
        Assert.Single(harness.Ai.Profiles);
    }
}
