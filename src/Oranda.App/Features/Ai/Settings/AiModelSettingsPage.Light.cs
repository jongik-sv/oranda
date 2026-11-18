using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Settings;

namespace Oranda.App.Features.Ai.Settings;

/// <summary>
/// 「가벼운 일 모델」 줄(설계 §1.3): 주 모델과 따로 간단한 일(SQL 설명·오류 고치기·요약)에 쓸 대상을 하나 고른다.
/// 저장 값은 <see cref="AiSettings.Light"/>. 「주 모델과 같음」 은 null 이다. 명령별 칸·노력 칸은 없다(노력은 라우터가 low 로 고정).
/// 지워진 프로필을 가리키면 「프로필 없음」 으로 보이고 주 모델로 몰래 바꾸지 않는다.
/// </summary>
public sealed partial class AiModelSettingsPage
{
    /// <summary>가벼운 일 모델로 바로 고를 수 있는 Claude Code 모델(싼 쪽부터).</summary>
    public static readonly IReadOnlyList<string> LightClaudeModels = ["haiku", "sonnet"];

    // 읽은 값을 그대로 들고 있다가 사용자가 고르기 전에는 그대로 저장한다(바꾸지 않았는데 저장 값이 달라 보이지 않게).
    private AiLightSettings? _light;

    [ObservableProperty]
    private bool _isLightDropdownOpen;

    /// <summary>가벼운 일 모델 줄 설명(한 줄).</summary>
    public string LightHintText => _t.T("간단한 일(SQL 설명·오류 고치기·요약)에 씁니다. 입력이 크면 주 모델로 보냅니다.",
        "Used for simple tasks (SQL explanation, error fixing, summaries). Large inputs go to the main model.");

    /// <summary>고른 가벼운 일 모델(대상을 고르지 않았으면 null = 주 모델과 같음).</summary>
    public AiLightSettings? Light => _light is { HasTarget: true } ? _light : null;

    private AiActiveProfile LightActive => AiActiveProfile.Find(
        new AiSettings { Driver = AiDriverKind.ClaudeCode, Light = Light, Profiles = _profiles, CustomClis = CurrentCustomClis() }, AiTier.Light, _systemClis);

    /// <summary>가벼운 일 모델이 쓸 수 없는 대상(지워진·잘못된 프로필, 쓸 수 없는 CLI)을 가리킨다.</summary>
    public bool ShowLightProblem => ShowSelection && Light is not null
        && LightActive.Kind is AiActiveKind.ProfileMissing or AiActiveKind.ProfileInvalid or AiActiveKind.SystemCliUnavailable or AiActiveKind.CustomCliUnavailable;

    public string LightProblemText => _t.T("가벼운 일 모델을 쓸 수 없습니다. 다른 줄을 고르세요. 이대로 두면 간단한 일을 할 때 오류가 나며, 주 모델로 바뀌지 않습니다.",
        "The light-task model cannot be used. Pick another row. If left as is, simple tasks fail with an error; the main model is not used instead.");

    /// <summary>가벼운 일 드롭다운 단추에 보이는 이름.</summary>
    public string LightSelectedTitle
    {
        get
        {
            if (Light is not { } light)
            {
                return SameAsMainTitle;
            }

            var active = LightActive;
            return active.Kind switch
            {
                AiActiveKind.ClaudeCode => ClaudeModelTitle(light.Model),
                AiActiveKind.Profile or AiActiveKind.ProfileInvalid => ProfileTitle(active.Profile!),
                AiActiveKind.ProfileMissing => _t.T("프로필 없음", "No profile"),
                AiActiveKind.SystemCli => active.SystemCli!.DisplayName,
                AiActiveKind.SystemCliUnavailable => active.SystemCli?.DisplayName ?? _t.T("사용할 수 없는 시스템 CLI", "System CLI unavailable"),
                AiActiveKind.CustomCli => CustomCliTitle(active.CustomCli!),
                AiActiveKind.CustomCliUnavailable => FindCustomCliRow(active.SystemCliId) is { } row ? row.DisplayName : _t.T("사용할 수 없는 기타 CLI", "Custom CLI unavailable"),
                _ => string.Empty,
            };
        }
    }

    private string SameAsMainTitle => _t.T("주 모델과 같음", "Same as main model");

    private static string ClaudeModelTitle(string? model) => $"Claude Code · {model?.Trim()}";

    private string ProfileTitle(AiLlmProfile profile) => profile.IsLocalAi ? $"{profile.Name} {_t.T("(로컬)", "(local)")}" : profile.Name;

    /// <summary>가벼운 일 드롭다운 줄: 주 모델과 같음, Claude Code 모델, 저장된 프로필, 시스템 CLI, 기타 CLI.</summary>
    public IReadOnlyList<AiProfileChoice> LightChoices
    {
        get
        {
            var light = Light;
            var active = LightActive;
            var rows = new List<AiProfileChoice> { new(AiChoiceKind.SameAsMain, SameAsMainTitle, IsCurrent: light is null) };

            var currentModel = light is not null && active.Kind == AiActiveKind.ClaudeCode ? light.Model?.Trim() : null;
            var hasLocal = _profiles.Any(p => p.IsLocalAi);
            foreach (var model in LightClaudeModels)
            {
                rows.Add(new AiProfileChoice(AiChoiceKind.ClaudeModel, ClaudeModelTitle(model),
                    model == "haiku" && !hasLocal ? _t.T("추천", "Recommended") : null,
                    IsCurrent: string.Equals(currentModel, model, StringComparison.OrdinalIgnoreCase), Model: model));
            }

            // 손으로 고친 다른 모델 이름도 현재 줄로 보이고 그대로 다시 고를 수 있다.
            if (currentModel is not null && !LightClaudeModels.Contains(currentModel, StringComparer.OrdinalIgnoreCase))
            {
                rows.Add(new AiProfileChoice(AiChoiceKind.ClaudeModel, ClaudeModelTitle(currentModel), IsCurrent: true, Model: currentModel));
            }

            var missingProfile = light is not null && active.Kind == AiActiveKind.ProfileMissing;
            if (_profiles.Count > 0 || missingProfile)
            {
                rows.Add(new AiProfileChoice(AiChoiceKind.Header, _t.T("저장된 프로필", "Saved profiles")));
                if (missingProfile)
                {
                    rows.Add(new AiProfileChoice(AiChoiceKind.MissingProfile, _t.T("프로필 없음", "No profile"), ProfileId: active.MissingId, IsCurrent: true,
                        Reason: _t.T("찾을 수 없음", "Not found")));
                }

                foreach (var profile in _profiles)
                {
                    var invalid = profile.Preset == AiProfilePreset.Unknown || profile.Format == AiApiFormat.Unknown;
                    rows.Add(new AiProfileChoice(AiChoiceKind.Profile, ProfileTitle(profile), profile.Model, profile.Id,
                        IsCurrent: light?.ProfileId == profile.Id,
                        Reason: invalid ? _t.T("이 버전에서 쓸 수 없음", "Not usable in this version") : null));
                }
            }

            AddLightCliChoices(rows, light, active);
            return rows;
        }
    }

    private void AddLightCliChoices(List<AiProfileChoice> rows, AiLightSettings? light, AiActiveProfile active)
    {
        var others = _systemClis.Where(c => c.Id != AiProfilePresets.ClaudeCodeId && c.Available).ToList();
        var pointsAtCli = light is not null && active.Kind is AiActiveKind.SystemCli or AiActiveKind.SystemCliUnavailable;
        if (others.Count > 0 || pointsAtCli)
        {
            rows.Add(new AiProfileChoice(AiChoiceKind.Header, _t.T("시스템 CLI", "System CLI")));
            foreach (var cli in others)
            {
                rows.Add(new AiProfileChoice(AiChoiceKind.SystemCli, cli.DisplayName, CliId: cli.Id,
                    IsCurrent: light is not null && active.Kind == AiActiveKind.SystemCli && active.SystemCliId == cli.Id));
            }

            if (pointsAtCli && active.Kind == AiActiveKind.SystemCliUnavailable)
            {
                rows.Add(new AiProfileChoice(AiChoiceKind.MissingCli, active.SystemCli?.DisplayName ?? _t.T("사용할 수 없는 시스템 CLI", "System CLI unavailable"),
                    IsCurrent: true, Reason: _t.T("쓸 수 없음", "Not available"), CliId: active.SystemCliId));
            }
        }

        var pointsAtCustom = light is not null && active.Kind is AiActiveKind.CustomCli or AiActiveKind.CustomCliUnavailable;
        if (CustomClis.Count == 0 && !pointsAtCustom)
        {
            return;
        }

        rows.Add(new AiProfileChoice(AiChoiceKind.Header, _t.T("기타 CLI", "Custom CLI")));
        var selectedId = pointsAtCustom && AiCustomCliTarget.TryGetId(active.SystemCliId, out var id) ? id : null;
        foreach (var row in CustomClis)
        {
            rows.Add(new AiProfileChoice(AiChoiceKind.CustomCli, row.DisplayName, CliId: row.Id,
                IsCurrent: selectedId is not null && string.Equals(selectedId, row.Id, StringComparison.Ordinal),
                Reason: AiCustomCliTarget.IsRunnable(row.ToModel()) ? null : _t.T("명령 없음", "No command")));
        }

        if (pointsAtCustom && FindCustomCliRow(active.SystemCliId) is null)
        {
            rows.Add(new AiProfileChoice(AiChoiceKind.MissingCustomCli, _t.T("사용할 수 없는 기타 CLI", "Custom CLI unavailable"), IsCurrent: true,
                Reason: _t.T("찾을 수 없음", "Not found"), CliId: selectedId));
        }
    }

    [RelayCommand]
    private void ToggleLightDropdown() => IsLightDropdownOpen = !IsLightDropdownOpen;

    /// <summary>가벼운 일 드롭다운에서 한 줄을 고른다. 고를 수 없는 줄은 무시한다. 노력은 저장하지 않는다(라우터가 low 로 고정).</summary>
    [RelayCommand]
    public void ChooseLight(AiProfileChoice? choice)
    {
        IsLightDropdownOpen = false;
        if (choice is null || !choice.Selectable)
        {
            return;
        }

        AiLightSettings? chosen = choice.Kind switch
        {
            AiChoiceKind.ClaudeModel when !string.IsNullOrWhiteSpace(choice.Model) => new AiLightSettings { Model = choice.Model },
            AiChoiceKind.Profile when choice.ProfileId is { } id => new AiLightSettings { ProfileId = id },
            AiChoiceKind.SystemCli when choice.CliId is { } cliId => new AiLightSettings { SystemCliId = cliId },
            AiChoiceKind.CustomCli when choice.CliId is { } customId => new AiLightSettings { SystemCliId = AiCustomCliTarget.SelectionOf(customId) },
            AiChoiceKind.SameAsMain => null,
            _ => _light,
        };

        _light = chosen;
        OnPropertyChanged(string.Empty);
    }
}
