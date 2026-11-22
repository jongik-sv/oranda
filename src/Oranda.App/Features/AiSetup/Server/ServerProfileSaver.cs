using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.Ai.Settings;
using Oranda.App.Shell;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Events;
using Oranda.Core.Settings;

namespace Oranda.App.Features.AiSetup.Server;

/// <summary>담당자에게 받은 서버 값(설정 파일 또는 주소만). 키는 여기 두지 않는다.</summary>
/// <param name="Name">프로필 이름. 비면 「회사 AI」.</param>
/// <param name="BaseUrl">서버 주소.</param>
/// <param name="Format">API 형식(직원 화면에는 보이지 않는다).</param>
/// <param name="Model">모델 이름. 없으면 서버 목록에서 고른다.</param>
/// <param name="LocalAi">파일에 「로컬 AI」 가 참으로 적혀 있을 때만 참.</param>
public sealed record ServerDraft(string Name, string BaseUrl, AiApiFormat Format, string? Model, bool LocalAi);

/// <summary>
/// 저장 결과. 실패면 <see cref="Error"/> 에 쉬운 말 이유. 프로필은 저장했는데 활성으로 고르지 못했으면 <see cref="ProfileId"/> 가 있다:
/// 다시 시도는 <see cref="ServerProfileSaver.ActivateAsync"/> 로 한다(다시 저장하면 같은 서버 프로필이 하나 더 생긴다).
/// </summary>
public sealed record ServerSaveResult(bool Success, Guid? ProfileId, string Error)
{
    public static ServerSaveResult Fail(string error) => new(false, null, error);
}

/// <summary>모델 찾기 결과. 성공이면 서버가 받아 준 형식과 고른 모델.</summary>
public sealed record ServerModelsResult(bool Success, AiApiFormat Format, IReadOnlyList<string> Models, string? Chosen, string Error);

/// <summary>
/// 회사 AI 서버 값을 프로필로 저장하고 활성으로 만든다. 저장 규칙(키 먼저·설정 다음·실패 때 키 되돌림·읽기 전용 거절)은
/// 프로필 관리 창의 <see cref="AiProfileManagerViewModel.SaveProfileAsync"/> 를 그대로 쓴다. 키는 키 저장소에만 간다.
/// </summary>
public sealed class ServerProfileSaver
{
    // 회사 서버는 OpenAI 호환이 흔하다: 먼저 묻고, 안 되면 Anthropic 형식으로 다시 묻는다.
    private static readonly AiApiFormat[] ProbeOrder = [AiApiFormat.OpenAi, AiApiFormat.Anthropic];

    private readonly AiProfileServices _services;
    private readonly ServerText _t;
    private bool _busy;

    public ServerProfileSaver(AiProfileServices services, IShellText? text)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _t = new ServerText(text);
    }

    /// <summary>암호화하지 않은 http 로 다른 PC 에 보내는 주소인가(화면이 경고 한 줄을 보인다).</summary>
    public static bool IsPlainText(string? baseUrl) => AiProfileUrl.IsPlainTextRemote(baseUrl);

    /// <summary>
    /// 모델이 정해진 값을 저장하고 활성 프로필로 고른다. 이름이 겹치면 「이름 (2)」 처럼 번호를 붙인다.
    /// 저장·활성 쓰기가 도는 중에 다시 부르면 아무것도 하지 않고 실패로 돌려준다(같은 프로필·키가 둘 생기지 않게).
    /// </summary>
    public async Task<ServerSaveResult> SaveAsync(ServerDraft draft, string? key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ct.ThrowIfCancellationRequested();
        if (_busy)
        {
            return ServerSaveResult.Fail(string.Empty);
        }

        _busy = true;
        try
        {
            return await SaveCoreAsync(draft, key).ConfigureAwait(true);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>저장한 프로필을 활성으로 고른다(저장 뒤 활성 쓰기만 실패했을 때의 다시 시도).</summary>
    public async Task<ServerSaveResult> ActivateAsync(Guid profileId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_busy)
        {
            return new ServerSaveResult(false, profileId, string.Empty);
        }

        _busy = true;
        try
        {
            return await ActivateCoreAsync(profileId).ConfigureAwait(true);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task<ServerSaveResult> SaveCoreAsync(ServerDraft draft, string? key)
    {
        if (draft.Format == AiApiFormat.Unknown || AiProfileUrl.BaseFor(draft.Format, draft.BaseUrl) is null)
        {
            return ServerSaveResult.Fail(_t.BadAddress);
        }

        if (string.IsNullOrWhiteSpace(draft.Model))
        {
            return ServerSaveResult.Fail(_t.NoModel);
        }

        var name = UniqueName(string.IsNullOrWhiteSpace(draft.Name) ? _t.DefaultName : draft.Name.Trim());
        using var editor = new AiProfileManagerViewModel(_services, _ => Task.FromResult(true), startNew: true);
        editor.Format = draft.Format;
        editor.BaseUrl = draft.BaseUrl.Trim();
        editor.ModelName = draft.Model.Trim();
        editor.Name = name;
        editor.LocalAi = draft.LocalAi; // 주소를 넣은 뒤에 켠다(주소가 바뀌면 지정이 풀린다)
        if (!await editor.SaveProfileAsync(key).ConfigureAwait(true))
        {
            return ServerSaveResult.Fail(ErrorFrom(editor));
        }

        return await ActivateCoreAsync(editor.EditingId).ConfigureAwait(true);
    }

    private async Task<ServerSaveResult> ActivateCoreAsync(Guid id)
    {
        if (_services.Settings.Current.Ai.Profiles.All(p => p.Id != id))
        {
            return ServerSaveResult.Fail(_t.ActivateFailed);
        }

        var previous = _services.Settings.Current.Ai;
        try
        {
            await _services.Settings.UpdateAsync(s => s with
            {
                Ai = s.Ai with { Driver = AiDriverKind.ClaudeCode, ActiveProfileId = id, SystemCliId = null },
            }, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (IsStoreError(ex))
        {
            // 설정 서비스는 쓰기 전에 메모리 값을 바꾼다: 쓰지 못한 선택이 남지 않게 되돌린다.
            try
            {
                await _services.Settings.UpdateAsync(s => s with
                {
                    Ai = s.Ai with { Driver = previous.Driver, ActiveProfileId = previous.ActiveProfileId, SystemCliId = previous.SystemCliId },
                }, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception again) when (IsStoreError(again))
            {
                // 같은 저장소가 다시 실패해도 메모리 값은 이미 되돌려졌다.
            }

            return new ServerSaveResult(false, id, _t.ActivateFailed + ex.Message);
        }

        if (previous != _services.Settings.Current.Ai)
        {
            _services.Bus?.Publish(new SettingsChanged("ai"));
        }

        return new ServerSaveResult(true, id, string.Empty);
    }

    /// <summary>
    /// 주소만 받았을 때 서버에 모델 목록을 묻는다. 형식은 OpenAI 호환 → Anthropic 순으로 시험해 처음 받아 준 쪽을 쓴다.
    /// 고르는 모델 = <paramref name="preferred"/> 가 목록에 있으면 그것, 아니면 첫 모델.
    /// </summary>
    public async Task<ServerModelsResult> FindModelsAsync(string baseUrl, string? key, string? preferred, CancellationToken ct)
    {
        if (_services.Probe is not { } probe)
        {
            return new(false, AiApiFormat.OpenAi, [], null, _t.NoProbe);
        }

        if (ProbeOrder.All(f => AiProfileUrl.BaseFor(f, baseUrl) is null))
        {
            return new(false, AiApiFormat.OpenAi, [], null, _t.BadAddress);
        }

        var trimmedKey = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        var emptyFormat = (AiApiFormat?)null;
        foreach (var format in ProbeOrder)
        {
            var profile = new AiLlmProfile { Id = Guid.NewGuid(), Name = _t.DefaultName, Preset = AiProfilePreset.Custom, Format = format, BaseUrl = baseUrl.Trim() };
            AiModelListResult result;
            try
            {
                result = await probe.ListModelsAsync(new AiProfileProbeRequest(profile, trimmedKey), ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                continue; // 구현 자체의 시간 초과: 다음 형식으로 묻는다
            }

            if (!result.Success)
            {
                continue;
            }

            var models = result.Models.Select(m => m.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
            if (models.Count == 0)
            {
                emptyFormat ??= format;
                continue;
            }

            var chosen = preferred is not null && models.Contains(preferred.Trim(), StringComparer.Ordinal) ? preferred.Trim() : models[0];
            return new(true, format, models, chosen, string.Empty);
        }

        return new(false, emptyFormat ?? AiApiFormat.OpenAi, [], null, emptyFormat is null ? _t.ModelsFailed : _t.NoModels);
    }

    private static bool IsStoreError(Exception ex) =>
        ex is InvalidOperationException or ArgumentException or System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException;

    private string UniqueName(string name)
    {
        var taken = _services.Settings.Current.Ai.Profiles.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name))
        {
            return name;
        }

        for (var n = 2; ; n++)
        {
            var candidate = $"{name} ({n})";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    // 관리 창의 오류 글을 직원용 쉬운 말로 바꾼다. 저장소·설정 쓰기 실패는 그 글 그대로 보인다(원인이 들어 있다).
    private string ErrorFrom(AiProfileManagerViewModel editor)
    {
        if (editor.UrlError.Length > 0)
        {
            return _t.BadAddress;
        }

        if (editor.ModelError.Length > 0)
        {
            return _t.NoModel;
        }

        if (editor.KeyError.Length > 0)
        {
            return _services.Credentials is null ? editor.KeyError : _t.KeyNeeded;
        }

        return editor.GeneralError.Length > 0 ? editor.GeneralError : editor.NameError;
    }
}
