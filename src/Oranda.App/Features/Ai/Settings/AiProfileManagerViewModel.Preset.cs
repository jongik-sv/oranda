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

/// <summary>AiProfileManagerViewModel 구획: 프리셋·변경 추적.</summary>
public sealed partial class AiProfileManagerViewModel
{
    // ------------------------------------------------------------------ 프리셋·변경 추적

    /// <summary>
    /// 프리셋 카드를 고른다. 새 프리셋의 <see cref="AiProfilePresets.NewDraft"/> 값(형식·URL)으로 바꾸되, 사용자가 고친 칸은 남긴다:
    /// URL 은 이전 프리셋의 기본값(또는 빈 칸)일 때만 새 기본값으로 바뀌고, 형식은 직접 입력이 아닐 때만 프리셋 값을 따른다.
    /// 모델명·토큰·이름은 지우지 않는다(이름은 이전 프리셋 이름 그대로일 때만 새 이름). 입력 중인 키는 서버(스킴·호스트·포트)가 그대로면 두고,
    /// 다른 서버로 바뀌면 새 서버로 시험·저장되지 않게 지우고 한 줄로 알린다. 서버가 알려 준 모델 후보는 제공자가 바뀌므로 비운다.
    /// </summary>
    public void SelectPreset(AiProfilePreset preset)
    {
        if (IsSaving || !IsEditable || SafeInfo(preset) is null || preset == Preset)
        {
            return;
        }

        var localBefore = LocalAi;
        var previous = SafeInfo(Preset);
        var draft = AiProfilePresets.NewDraft(preset);
        var next = SafeInfo(preset)!;
        var url = BaseUrl.Trim();
        var urlBefore = url;
        if (url.Length == 0 || (previous is not null && string.Equals(url, previous.DefaultBaseUrl, StringComparison.Ordinal)))
        {
            BaseUrl = draft.BaseUrl;
        }

        if (!next.FormatSelectable)
        {
            Format = draft.Format;
        }

        if (previous is not null && (Name.Trim().Length == 0 || string.Equals(Name.Trim(), PresetName(previous), StringComparison.Ordinal)))
        {
            Name = PresetName(next);
        }

        Preset = preset;
        CancelOperation(); // 앞 제공자에게 보낸 조회의 늦은 결과가 새 프리셋 화면을 바꾸지 못하게 한다
        ReplaceModels([]);
        _detectedModels = [];
        var keyDropped = !SameServer(urlBefore, BaseUrl) && !string.IsNullOrWhiteSpace(EnteredKeyProvider?.Invoke());
        if (keyDropped)
        {
            EnteredKeyConsumed?.Invoke(this, EventArgs.Empty); // 앞 서버에 입력하던 키가 새 서버로 시험·저장되지 않게 칸을 비운다
        }
        if (ImagesNote.Length > 0)
        {
            SupportsImages = null; // 앞 서버가 알려 준 이미지 지원은 새 제공자에게 맞지 않는다
            ImagesNote = string.Empty;
        }

        ResultKind = AiResultKind.None;
        if (keyDropped)
        {
            SetResult(AiResultKind.Info, _t.T("다른 서버로 바뀌어 입력한 API Key 를 지웠습니다. 새 서버의 키를 입력하세요.",
                "The server changed, so the API key you typed was cleared. Enter the key for the new server."));
        }

        if (localBefore && !LocalAi)
        {
            SetResult(AiResultKind.Info, keyDropped ? ResultText + " " + LocalAiLapsedText : LocalAiLapsedText); // 주소가 바뀌어 지정이 풀렸다는 안내가 위 초기화에 지워지지 않게 한다
        }

        MarkSelected();
    }

    [RelayCommand]
    private void ChoosePreset(AiPresetCard? card)
    {
        if (card is not null)
        {
            SelectPreset(card.Preset);
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(LocalAi):
                _localAiUrl = LocalAi ? BaseUrl.Trim() : null;
                goto case nameof(Name);
            case nameof(BaseUrl):
            case nameof(Format):
                // 지정한 서버와 다른 주소가 되면 값을 가리지 않는 지정을 잇지 않는다(다시 확인하게 한다).
                if (LocalAi && !string.Equals(BaseUrl.Trim(), _localAiUrl, StringComparison.Ordinal))
                {
                    LocalAi = false;
                    SetResult(AiResultKind.Info, LocalAiLapsedText);
                }

                if (IsBusy)
                {
                    CancelOperation(); // 바뀌기 전 주소로 보낸 조회는 결과를 버린다
                    ResultKind = AiResultKind.None;
                }

                if (UrlError.Length > 0 && AiProfileUrl.BaseFor(Format, BaseUrl) is not null)
                {
                    UrlError = string.Empty;
                }

                goto case nameof(Name);
            case nameof(ModelName):
                if (IsBusy && _operationIsTest)
                {
                    CancelOperation(); // 바뀌기 전 모델로 보낸 시험 결과는 버린다
                    ResultKind = AiResultKind.None;
                }

                ReapplyDetectedImages();
                if (ModelError.Length > 0 && ModelName.Trim().Length > 0)
                {
                    ModelError = string.Empty;
                }

                goto case nameof(Name);
            case nameof(SupportsImages):
                if (!_applyingImages)
                {
                    _imagesManual = true; // 사용자가 직접 바꾼 값은 목록으로 덮지 않는다(다른 칸을 고친 것은 해당 없음)
                }

                goto case nameof(Name);
            case nameof(Name):
            case nameof(MaxInputTokensText):
            case nameof(MaxOutputTokensText):
            case nameof(Preset):
            case nameof(ClearKeyRequested):
                IsDirty = true;
                if (_attempted)
                {
                    Validate(EnteredKeyProvider?.Invoke());
                }

                break;
        }
    }

    /// <summary>키 칸에 글이 입력됐다(창이 알린다). 키도 저장하지 않은 변경이므로 버림 확인 대상이다.</summary>
    public void NotifyKeyTyped(bool hasText)
    {
        if (hasText && !_loading)
        {
            IsDirty = true;
        }
    }

    [RelayCommand]
    private void ToggleKeyVisible() => IsKeyVisible = !IsKeyVisible;

    /// <summary>저장할 때 저장된 키를 지운다(입력 칸을 비운 것과 다르다: 빈 칸은 변경 없음).</summary>
    [RelayCommand]
    private void ClearStoredKey()
    {
        if (CanClearKey)
        {
            ClearKeyRequested = !ClearKeyRequested;
        }
    }

    [RelayCommand]
    private async Task Cancel()
    {
        if (await ConfirmDiscardAsync().ConfigureAwait(true))
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
