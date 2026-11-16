using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Events;
using Oranda.Core.Settings;

namespace Oranda.App.Features.Ai.Settings;

/// <summary>AiProfileManagerViewModel 구획: 목록·불러오기.</summary>
public sealed partial class AiProfileManagerViewModel
{
    // ------------------------------------------------------------------ 목록·불러오기

    private string CardSubtitle(AiProfilePresetInfo info) => info.Preset switch
    {
        AiProfilePreset.Anthropic => _t.T("Claude API (키 필요)", "Claude API (key required)"),
        AiProfilePreset.ZAi => _t.T("GLM 모델 (키 필요)", "GLM models (key required)"),
        AiProfilePreset.Gemini => _t.T("Gemini API (키 필요)", "Gemini API (key required)"),
        AiProfilePreset.OpenAi => _t.T("GPT 모델 (키 필요)", "GPT models (key required)"),
        AiProfilePreset.Ollama => _t.T("로컬 실행 (키 불필요)", "Runs locally (no key)"),
        AiProfilePreset.LmStudio => _t.T("로컬 실행 (키 불필요)", "Runs locally (no key)"),
        _ => _t.T("호환 API 직접 설정", "Configure a compatible API"),
    };

    /// <summary>카드·목록·기본 이름에 보이는 프리셋 이름. Core 의 「직접 입력」 만 한/영을 고른다(제공자 이름은 그대로).</summary>
    private string PresetName(AiProfilePresetInfo info) => info.Preset == AiProfilePreset.Custom ? _t.T(info.DisplayName, "Custom") : info.DisplayName;

    private static AiProfilePresetInfo? SafeInfo(AiProfilePreset preset) => AiProfilePresets.All.FirstOrDefault(p => p.Preset == preset);

    private string ListSubtitle(AiLlmProfile profile)
    {
        if (profile.Preset == AiProfilePreset.Unknown || profile.Format == AiApiFormat.Unknown)
        {
            return _t.T("이 버전에서 쓸 수 없음", "Not usable in this version");
        }

        var format = profile.Format == AiApiFormat.OpenAi ? "OpenAI" : "Anthropic";
        var local = profile.IsLocalAi ? _t.T(" · 로컬 AI", " · Local AI") : string.Empty;
        return _t.T($"{PresetName(SafeInfo(profile.Preset)!)} · {format} 형식", $"{PresetName(SafeInfo(profile.Preset)!)} · {format} format") + local;
    }

    private void ReloadItems()
    {
        Items.Clear();
        foreach (var profile in _services.Settings.Current.Ai.Profiles)
        {
            var usable = profile.Preset != AiProfilePreset.Unknown && profile.Format != AiApiFormat.Unknown;
            Items.Add(new AiProfileListItem(profile.Id, profile.Name, ListSubtitle(profile), usable) { IsSelected = profile.Id == _editingId && !_isNew });
        }
    }

    private void MarkSelected()
    {
        foreach (var item in Items)
        {
            item.IsSelected = !_isNew && item.Id == _editingId;
        }

        foreach (var card in PresetCards)
        {
            card.IsSelected = card.Preset == Preset;
        }
    }

    // 편집형 ComboBox 는 목록이 바뀌면 입력한 글을 지울 수 있다: 모델명은 그대로 되돌린다.
    private void ReplaceModels(IEnumerable<string> ids)
    {
        var typed = ModelName;
        var wasLoading = _loading;
        _loading = true; // 목록을 바꾸는 것만으로 변경 표시·검사·취소가 일어나지 않게 한다
        try
        {
            Models.Clear();
            foreach (var id in ids)
            {
                Models.Add(id);
            }

            if (ModelName != typed)
            {
                ModelName = typed;
            }
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void ClearEditorState()
    {
        _attempted = false;
        UrlError = ModelError = InputTokensError = OutputTokensError = KeyError = GeneralError = NameError = string.Empty;
        ResultKind = AiResultKind.None;
        ResultText = string.Empty;
        ClearKeyRequested = false;
        HasStoredKey = false;
        KeyReadFailed = false;
        IsKeyVisible = false;
        ImagesNote = string.Empty;
        _detectedModels = [];
        _imagesManual = false;
        Models.Clear();
        IsDirty = false;
    }

    private void LoadProfile(AiLlmProfile profile)
    {
        CancelOperation();
        _loading = true;
        try
        {
            ClearEditorState();
            _isNew = false;
            _editingId = profile.Id;
            IsEditing = true;
            var usable = profile.Preset != AiProfilePreset.Unknown && profile.Format != AiApiFormat.Unknown;
            IsEditable = usable;
            InvalidReason = usable ? string.Empty : _t.T("이 버전이 모르는 프리셋·API 형식이라 고칠 수 없습니다. 지우기만 할 수 있고, 다른 프로필은 영향을 받지 않습니다.",
                "This version does not recognize this preset or API format, so it cannot be edited. You can only delete it; other profiles are not affected.");
            Preset = profile.Preset;
            Format = profile.Format == AiApiFormat.Unknown ? AiApiFormat.Anthropic : profile.Format;
            Name = profile.Name;
            BaseUrl = profile.BaseUrl;
            LocalAi = profile.IsLocalAi;
            _localAiUrl = LocalAi ? BaseUrl.Trim() : null;
            ModelName = profile.Model;
            MaxInputTokensText = profile.MaxInputTokens.ToString(CultureInfo.InvariantCulture);
            MaxOutputTokensText = profile.MaxOutputTokens.ToString(CultureInfo.InvariantCulture);
            SupportsImages = profile.SupportsImages;
            MarkSelected();
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(CanProbe));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(IsExistingProfile));
        OnPropertyChanged(nameof(EditingId));
    }

    private void BeginDraft(AiProfilePreset preset)
    {
        CancelOperation();
        _loading = true;
        try
        {
            ClearEditorState();
            var draft = AiProfilePresets.NewDraft(preset);
            _isNew = true;
            _editingId = draft.Id;
            IsEditing = true;
            IsEditable = true;
            InvalidReason = string.Empty;
            Preset = preset;
            Format = draft.Format;
            Name = PresetName(SafeInfo(preset)!);
            BaseUrl = draft.BaseUrl;
            LocalAi = false;
            _localAiUrl = null;
            ModelName = string.Empty;
            MaxInputTokensText = draft.MaxInputTokens.ToString(CultureInfo.InvariantCulture);
            MaxOutputTokensText = draft.MaxOutputTokens.ToString(CultureInfo.InvariantCulture);
            SupportsImages = null;
            MarkSelected();
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(CanProbe));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(IsExistingProfile));
        OnPropertyChanged(nameof(EditingId));
    }

    private async Task RefreshKeyStateAsync(Guid id)
    {
        if (_services.Credentials is not { } store)
        {
            return;
        }

        try
        {
            var bytes = await store.LoadAsync(AiProfileCredentials.KeyFor(id)).ConfigureAwait(true);
            var stored = bytes is { Length: > 0 };
            if (bytes is not null)
            {
                Array.Clear(bytes);
            }

            if (!_disposed && _editingId == id)
            {
                HasStoredKey = stored;
                KeyReadFailed = false;
            }
        }
        catch (Exception ex) when (IsStoreError(ex))
        {
            // 키 저장소를 못 읽으면 「저장된 키 있음」 을 알 수 없다. 저장할 때 서버가 바뀌었으면 다시 읽어 막고, 지우기도 쓸 수 있다.
            if (!_disposed && _editingId == id)
            {
                KeyReadFailed = true;
            }
        }
    }

    /// <summary>왼쪽 목록에서 프로필을 고른다. 저장하지 않은 변경이 있으면 버려도 되는지 묻는다.</summary>
    public async Task SelectProfileAsync(Guid id)
    {
        if (IsSaving || (_editingId == id && !_isNew))
        {
            return;
        }

        if (!await ConfirmDiscardAsync().ConfigureAwait(true))
        {
            return;
        }

        var profile = _services.Settings.Current.Ai.Profiles.FirstOrDefault(p => p.Id == id);
        if (profile is null)
        {
            return;
        }

        LoadProfile(profile);
        await RefreshKeyStateAsync(id).ConfigureAwait(true);
    }

    /// <summary>「새 프로필」: 직접 입력 초안을 연다. 저장하지 않은 변경이 있으면 버려도 되는지 묻는다.</summary>
    public async Task NewProfileAsync()
    {
        if (IsSaving)
        {
            return;
        }

        if (!await ConfirmDiscardAsync().ConfigureAwait(true))
        {
            return;
        }

        BeginDraft(AiProfilePreset.Custom);
    }

    /// <summary>저장하지 않은 변경이 있으면 버려도 되는지 묻는다(없으면 true). 창 닫기도 같은 확인을 쓴다.</summary>
    internal async Task<bool> ConfirmDiscardAsync() =>
        !IsDirty || await _confirm(_t.T("저장하지 않은 변경이 있습니다. 버리고 계속할까요?", "There are unsaved changes. Discard them and continue?")).ConfigureAwait(true);

    [RelayCommand]
    private Task SelectItem(AiProfileListItem? item) => item is null ? Task.CompletedTask : SelectProfileAsync(item.Id);

    [RelayCommand]
    private Task NewProfile() => NewProfileAsync();
}
