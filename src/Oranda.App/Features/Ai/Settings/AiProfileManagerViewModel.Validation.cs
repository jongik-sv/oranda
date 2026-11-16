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

/// <summary>AiProfileManagerViewModel 구획: 입력 검사.</summary>
public sealed partial class AiProfileManagerViewModel
{
    // ------------------------------------------------------------------ 입력 검사

    private static bool TryPositive(string text, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;

    /// <summary>입력 칸을 검사해 오류 글을 채운다. 키 칸은 저장된 키·지우기 요청까지 함께 본다. 하나라도 틀리면 false.</summary>
    public bool Validate(string? enteredKey)
    {
        _attempted = true;
        NameError = Name.Trim().Length == 0 ? _t.T("프로필 이름을 입력하세요.", "Enter a profile name.") : string.Empty;
        UrlError = AiProfileUrl.BaseFor(Format, BaseUrl) is null
            ? _t.T("http:// 또는 https:// 로 시작하는 올바른 주소를 입력하세요(사용자 정보·쿼리는 쓸 수 없습니다).",
                "Enter a valid address starting with http:// or https:// (user info and query strings are not allowed).")
            : string.Empty;
        ModelError = ModelName.Trim().Length == 0 ? _t.T("모델명을 입력하세요.", "Enter a model name.") : string.Empty;
        InputTokensError = TryPositive(MaxInputTokensText, out _) ? string.Empty : _t.T("1 이상의 정수를 입력하세요.", "Enter an integer of 1 or more.");
        OutputTokensError = TryPositive(MaxOutputTokensText, out _) ? string.Empty : _t.T("1 이상의 정수를 입력하세요.", "Enter an integer of 1 or more.");

        var typed = !string.IsNullOrWhiteSpace(enteredKey);
        var requirement = SafeInfo(Preset)?.KeyRequirement ?? AiKeyRequirement.Optional;
        var willHaveKey = typed || (HasStoredKey && !ClearKeyRequested);
        if (!typed && UrlError.Length == 0 && StoredKeyBlockedByAddress(enteredKey))
        {
            // 저장된 키는 저장된 서버로만 나간다: 서버가 바뀌면 새 키를 넣거나 저장된 키를 지워야 한다(키가 필요 없는 프리셋도 같다).
            KeyError = _t.T("주소가 저장된 프로필과 다른 서버입니다. 저장된 키를 보내지 않으니 새 API Key 를 입력하거나 저장된 키 지우기를 누르세요.",
                "The address points to a different server than the saved profile. The stored key is not sent: enter a new API key or clear the stored key.");
        }
        else if (requirement == AiKeyRequirement.Required && !willHaveKey)
        {
            KeyError = _t.T("이 서비스는 API Key 가 필요합니다.", "This service requires an API key.");
        }
        else if ((typed || ClearKeyRequested) && _services.Credentials is null)
        {
            KeyError = _t.T("키 저장소를 쓸 수 없어 키를 저장하거나 지울 수 없습니다.", "The credential store is unavailable, so the key cannot be saved or removed.");
        }
        else
        {
            KeyError = string.Empty;
        }

        return NameError.Length == 0 && UrlError.Length == 0 && ModelError.Length == 0 && InputTokensError.Length == 0
            && OutputTokensError.Length == 0 && KeyError.Length == 0;
    }

    private AiLlmProfile BuildProfile()
    {
        _ = TryPositive(MaxInputTokensText, out var input);
        _ = TryPositive(MaxOutputTokensText, out var output);
        return new AiLlmProfile
        {
            Id = _editingId,
            Name = Name.Trim(),
            Preset = Preset,
            Format = Format,
            BaseUrl = BaseUrl.Trim(),
            Model = ModelName.Trim(),
            MaxInputTokens = input,
            MaxOutputTokens = output,
            SupportsImages = SupportsImages,
            LocalAiConfirmedUrl = LocalAi ? BaseUrl.Trim() : null,
        };
    }
}
