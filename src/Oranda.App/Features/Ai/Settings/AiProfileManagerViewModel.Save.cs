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

/// <summary>AiProfileManagerViewModel 구획: 저장·삭제.</summary>
public sealed partial class AiProfileManagerViewModel
{
    // ------------------------------------------------------------------ 저장·삭제

    private static bool IsStoreError(Exception ex) =>
        ex is InvalidOperationException or ArgumentException or System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException
            or System.Security.Cryptography.CryptographicException; // Windows DPAPI 가 깨진·옮겨 온 키 파일을 풀 때 던진다

    private string ReadOnlyMessage => _t.T("설정 파일이 이 버전보다 새로워 읽기 전용입니다. 저장하지 않았습니다.",
        "The settings file is newer than this version and is read-only. Nothing was saved.");

    /// <summary>
    /// 입력을 검사하고 프로필을 즉시 기록한다. 순서: 검사 → 읽기 전용 확인 → 키 → 설정. 어느 단계가 실패해도 이유를 <see cref="GeneralError"/> 에 보이고 false 를 돌려주며,
    /// 설정 쓰기가 실패하면 방금 쓴 키를 되돌려 키만 남는 일(고아 키)이 없게 한다.
    /// </summary>
    /// <param name="enteredKey">키 입력 칸의 글. 비어 있으면 저장된 키를 그대로 둔다.</param>
    public async Task<bool> SaveProfileAsync(string? enteredKey)
    {
        if (_disposed || !CanSave)
        {
            return false;
        }

        CancelOperation();
        GeneralError = string.Empty;
        if (!Validate(enteredKey))
        {
            return false;
        }

        if (_services.Settings.IsReadOnly)
        {
            GeneralError = ReadOnlyMessage;
            return false;
        }

        // 키 저장소를 다시 읽는 동안(macOS 키체인 프롬프트가 떠 있을 수 있다)에도 다른 조작이 편집 대상을 바꾸지 못하게 먼저 잠근다.
        IsSaving = true;
        try
        {
            var id = _editingId;
            var address = BaseUrl;
            if (await StoredKeyBlocksSaveAsync(enteredKey).ConfigureAwait(true) || _disposed || id != _editingId || address != BaseUrl)
            {
                return false; // 막혔거나, 기다리는 사이 편집 대상·주소가 달라졌으면 저장하지 않는다
            }

            return await SaveCoreAsync(string.IsNullOrWhiteSpace(enteredKey) ? null : enteredKey.Trim()).ConfigureAwait(true);
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>
    /// 키를 입력하지 않았고 서버가 저장된 프로필과 달라졌으면, 저장 시점에 키 저장소를 다시 읽는다(창을 열 때의 읽기 실패·읽는 중 저장 때문에
    /// <see cref="HasStoredKey"/> 가 믿을 수 없다). 키가 있거나 읽지 못하면 옛 키가 새 서버로 나갈 수 있으므로 저장을 막는다. 지우기를 누른 경우는 막지 않는다.
    /// </summary>
    private async Task<bool> StoredKeyBlocksSaveAsync(string? enteredKey)
    {
        if (_isNew || !string.IsNullOrWhiteSpace(enteredKey) || ClearKeyRequested || StoredKeyMatchesAddress() || _services.Credentials is not { } store)
        {
            return false;
        }

        var id = _editingId;
        var blocked = false;
        try
        {
            var bytes = await store.LoadAsync(AiProfileCredentials.KeyFor(id)).ConfigureAwait(true);
            if (bytes is { Length: > 0 })
            {
                blocked = true;
                if (_editingId == id)
                {
                    HasStoredKey = true;
                    KeyReadFailed = false;
                }
            }

            if (bytes is not null)
            {
                Array.Clear(bytes);
            }
        }
        catch (Exception ex) when (IsStoreError(ex))
        {
            blocked = true; // 읽지 못하면 키가 있는지 알 수 없다: 안전한 쪽으로 막는다
            if (_editingId == id)
            {
                KeyReadFailed = true; // 지우기 단추를 쓸 수 있게 한다
            }
        }

        if (blocked && !_disposed)
        {
            KeyError = _t.T("주소가 저장된 프로필과 다른 서버입니다. 저장된 키를 보내지 않으니 새 API Key 를 입력하거나 저장된 키 지우기를 누르세요(키 저장소를 읽지 못했어도 지우기를 쓸 수 있습니다).",
                "The address points to a different server than the saved profile. The stored key is not sent: enter a new API key or clear the stored key (clearing also works when the key store could not be read).");
        }

        return blocked;
    }

    private async Task<bool> SaveCoreAsync(string? newKey)
    {
        var profile = BuildProfile();
        var credentialName = AiProfileCredentials.KeyFor(profile.Id);
        var clear = newKey is null && ClearKeyRequested;
        var store = _services.Credentials;
        byte[]? previousKey = null;
        var keyWritten = false;

        if (clear && store is not null)
        {
            // 서버가 바뀐 프로필의 옛 키가 남지 않게 설정을 쓰기 전에 지운다. 지우지 못하면 저장하지 않는다.
            try
            {
                try
                {
                    previousKey = await store.LoadAsync(credentialName).ConfigureAwait(true);
                }
                catch (Exception ex) when (IsStoreError(ex))
                {
                    previousKey = null;
                }

                await store.DeleteAsync(credentialName).ConfigureAwait(true);
                keyWritten = true;
            }
            catch (Exception ex) when (IsStoreError(ex))
            {
                GeneralError = _t.T("저장된 키를 지우지 못했습니다. 프로필은 저장하지 않았습니다: ", "Could not remove the stored key. The profile was not saved: ") + ex.Message;
                return false;
            }
        }

        if (newKey is not null && store is not null)
        {
            var secret = Encoding.UTF8.GetBytes(newKey);
            try
            {
                try
                {
                    previousKey = await store.LoadAsync(credentialName).ConfigureAwait(true);
                }
                catch (Exception ex) when (IsStoreError(ex))
                {
                    previousKey = null; // 읽을 수 없는 옛 키는 복원할 수 없다. 새 키를 입력해 고치려는 저장은 막지 않는다
                }

                await store.SaveAsync(credentialName, secret).ConfigureAwait(true);
                keyWritten = true;
            }
            catch (Exception ex) when (IsStoreError(ex))
            {
                GeneralError = _t.T("API 키를 저장하지 못했습니다. 프로필은 저장하지 않았습니다: ", "Could not save the API key. The profile was not saved: ") + ex.Message;
                return false;
            }
            finally
            {
                Array.Clear(secret);
            }
        }

        var before = _services.Settings.Current.Ai;
        try
        {
            await _services.Settings.UpdateAsync(s => s with { Ai = s.Ai with { Profiles = Upsert(s.Ai.Profiles, profile) } }).ConfigureAwait(true);
        }
        catch (Exception ex) when (IsStoreError(ex))
        {
            var (rollbackNote, keyLost) = await RollbackAsync(keyWritten, previousKey, credentialName, before, keyCleared: clear).ConfigureAwait(true);
            if (keyLost)
            {
                HasStoredKey = false; // 지운 키를 되돌리지 못했다: 화면도 키가 없다고 맞춘다
                KeyReadFailed = false;
                ClearKeyRequested = false;
            }

            GeneralError = _t.T("설정 파일에 저장하지 못했습니다: ", "Could not write the settings file: ") + ex.Message + rollbackNote;
            if (previousKey is not null)
            {
                Array.Clear(previousKey);
            }

            return false;
        }

        if (previousKey is not null)
        {
            Array.Clear(previousKey);
        }

        var wasNew = _isNew;
        _isNew = false;
        _attempted = false;
        HasStoredKey = newKey is not null || (HasStoredKey && !clear);
        ClearKeyRequested = false;
        IsDirty = false; // 위 대입이 변경 표시를 켜므로 마지막에 끈다
        var latest = _services.Settings.Current.Ai;
        PublishChanged(before, latest);
        _applied?.Invoke(latest, null, wasNew && _selectNewOnSave ? profile.Id : null);
        ReloadItems();
        MarkSelected();
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(IsExistingProfile));
        EnteredKeyConsumed?.Invoke(this, EventArgs.Empty);
        GeneralError = string.Empty;
        SetResult(AiResultKind.Success, _t.T("프로필을 저장했습니다.", "Profile saved."));
        return true;
    }

    private async Task<(string Note, bool KeyLost)> RollbackAsync(bool keyWritten, byte[]? previousKey, string credentialName, AiSettings before, bool keyCleared = false)
    {
        var note = string.Empty;
        var keyLost = false;
        if (keyWritten && _services.Credentials is { } store)
        {
            try
            {
                if (previousKey is not null)
                {
                    await store.SaveAsync(credentialName, previousKey).ConfigureAwait(true);
                }
                else
                {
                    await store.DeleteAsync(credentialName).ConfigureAwait(true);
                    if (keyCleared)
                    {
                        // 지우기 전에 옛 키를 읽지 못했으므로 되돌릴 값이 없다: 키는 사라졌다.
                        keyLost = true;
                        note = " " + _t.T("또한 지운 저장된 키는 읽을 수 없어 되돌리지 못했습니다. 키가 사라졌으니 API Key 를 다시 입력하세요.",
                            "Also, the stored key that was removed could not be read beforehand and cannot be restored. The key is gone: enter the API key again.");
                    }
                }
            }
            catch (Exception ex) when (IsStoreError(ex))
            {
                if (keyCleared)
                {
                    keyLost = true;
                    note = " " + _t.T("또한 지운 저장된 키를 되돌리지 못해 키가 사라졌습니다. API Key 를 다시 입력하세요: ",
                        "Also, the stored key that was removed could not be restored, so the key is gone. Enter the API key again: ") + ex.Message;
                }
                else
                {
                    note = " " + _t.T("또한 방금 저장한 키를 되돌리지 못했습니다(저장소에 새 키가 남았을 수 있습니다): ", "Also, the key that was just saved could not be undone (the new key may remain in the store): ") + ex.Message;
                }
            }
        }

        // 설정 서비스는 저장소에 쓰기 전에 메모리 값을 바꾼다: 쓰기 실패 뒤에도 화면·실행이 쓰지 않은 프로필을 보지 않도록 메모리 값을 되돌린다.
        try
        {
            await _services.Settings.UpdateAsync(s => s with { Ai = s.Ai with { Profiles = before.Profiles, ActiveProfileId = before.ActiveProfileId, SystemCliId = before.SystemCliId } }).ConfigureAwait(true);
        }
        catch (Exception ex) when (IsStoreError(ex))
        {
            // 같은 저장소가 다시 실패해도 메모리 값은 이미 되돌려졌다.
        }

        return (note, keyLost);
    }

    private static List<AiLlmProfile> Upsert(IReadOnlyList<AiLlmProfile> list, AiLlmProfile profile)
    {
        var next = list.ToList();
        var index = next.FindIndex(p => p.Id == profile.Id);
        if (index >= 0)
        {
            next[index] = profile;
        }
        else
        {
            next.Add(profile);
        }

        return next;
    }

    /// <summary>
    /// 편집 중인 저장된 프로필을 지운다. 확인을 거친 뒤 설정 → 키 순으로 지운다. 활성 프로필이면 <c>ActiveProfileId</c> 를 비워 Claude Code 로 되돌린다(설정 쪽 페이지가 안내를 보인다).
    /// 설정 파일이 읽기 전용이면 지우지 않는다. 키 삭제가 실패해도 프로필은 이미 지워졌으므로 안내만 하고 true 를 돌려준다.
    /// </summary>
    public async Task<bool> DeleteProfileAsync()
    {
        if (_disposed || !CanDelete || IsSaving)
        {
            return false;
        }

        CancelOperation();
        var id = _editingId;
        var name = Name.Trim().Length > 0 ? Name.Trim() : _services.Settings.Current.Ai.Profiles.FirstOrDefault(p => p.Id == id)?.Name ?? string.Empty;
        var inUse = _services.Settings.Current.Ai.ActiveProfileId == id;
        var question = _t.T($"프로필 「{name}」 과(와) 저장된 키를 지웁니다. 계속할까요?", $"Delete the profile \"{name}\" and its stored key?");
        if (inUse)
        {
            question += " " + _t.T("이 프로필은 지금 사용 중이라 지우면 Claude Code 로 되돌아갑니다.", "This profile is in use, so Claude Code is selected again after deleting it.");
        }

        // 확인을 기다리는 동안 창이 닫혔으면(Dispose) 지우지 않는다.
        if (!await _confirm(question).ConfigureAwait(true) || _disposed)
        {
            return false;
        }

        GeneralError = string.Empty;
        if (_services.Settings.IsReadOnly)
        {
            GeneralError = ReadOnlyMessage;
            return false;
        }

        IsSaving = true;
        try
        {
            var before = _services.Settings.Current.Ai;
            try
            {
                await _services.Settings.UpdateAsync(s => s with
                {
                    Ai = s.Ai with
                    {
                        Profiles = [.. s.Ai.Profiles.Where(p => p.Id != id)],
                        ActiveProfileId = s.Ai.ActiveProfileId == id ? null : s.Ai.ActiveProfileId,
                        SystemCliId = s.Ai.ActiveProfileId == id ? null : s.Ai.SystemCliId, // 되돌아가는 곳은 Claude Code 이므로 낡은 시스템 CLI 값도 비운다
                    },
                }).ConfigureAwait(true);
            }
            catch (Exception ex) when (IsStoreError(ex))
            {
                _ = await RollbackAsync(false, null, string.Empty, before).ConfigureAwait(true);
                GeneralError = _t.T("설정 파일에 저장하지 못했습니다: ", "Could not write the settings file: ") + ex.Message;
                return false;
            }

            string? note = null;
            if (_services.Credentials is { } store)
            {
                try
                {
                    await store.DeleteAsync(AiProfileCredentials.KeyFor(id)).ConfigureAwait(true);
                }
                catch (Exception ex) when (IsStoreError(ex))
                {
                    note = _t.T("프로필은 지웠지만 저장된 키를 지우지 못했습니다: ", "The profile was deleted, but the stored key could not be removed: ") + ex.Message;
                }
            }

            var latest = _services.Settings.Current.Ai;
            PublishChanged(before, latest);
            _applied?.Invoke(latest, id, null);
            ReloadItems();
            IsDirty = false;
            if (Items.FirstOrDefault(i => i.IsUsable) is { } next)
            {
                LoadProfile(latest.Profiles.First(p => p.Id == next.Id));
                await RefreshKeyStateAsync(next.Id).ConfigureAwait(true);
            }
            else
            {
                BeginDraft(AiProfilePreset.Custom);
            }

            GeneralError = note ?? string.Empty;
            SetResult(note is null ? AiResultKind.Success : AiResultKind.Info, note ?? _t.T("프로필을 지웠습니다.", "Profile deleted."));
            return true;
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void PublishChanged(AiSettings before, AiSettings after)
    {
        if (before != after)
        {
            _services.Bus?.Publish(new SettingsChanged("ai"));
        }
    }

    [RelayCommand]
    private async Task Save() => await SaveProfileAsync(EnteredKeyProvider?.Invoke()).ConfigureAwait(true);

    [RelayCommand]
    private async Task Delete() => await DeleteProfileAsync().ConfigureAwait(true);
}
