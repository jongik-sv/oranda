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

/// <summary>AiProfileManagerViewModel 구획: 서버 조회 (모델 새로고침 · 연결 테스트).</summary>
public sealed partial class AiProfileManagerViewModel
{
    // ------------------------------------------------------------------ 서버 조회 (모델 새로고침 · 연결 테스트)

    private void SetResult(AiResultKind kind, string text)
    {
        ResultText = text;
        ResultKind = kind;
    }

    private CancellationToken BeginOperation()
    {
        CancelOperation();
        _operation = new CancellationTokenSource();
        _operationIsTest = false;
        IsBusy = true;
        return _operation.Token;
    }

    /// <summary>도는 서버 조회를 멈추고 그 결과가 화면을 바꾸지 못하게 한다(프로필 전환·저장·창 닫기).</summary>
    public void CancelOperation()
    {
        _version++;
        var running = _operation;
        _operation = null;
        if (running is not null)
        {
            try
            {
                running.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 이미 끝난 작업이다.
            }

            running.Dispose();
        }

        if (IsBusy)
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void StopProbe()
    {
        if (IsBusy)
        {
            CancelOperation();
            SetResult(AiResultKind.Info, _t.T("작업을 멈췄습니다.", "Stopped."));
        }
    }

    // 저장된 키는 저장된 프로필의 서버(스킴·호스트·포트)로만 쓴다: 주소를 고친 채 키 칸을 비워 두고 시험하면 옛 키가 새 서버로 나가기 때문이다.
    private bool StoredKeyMatchesAddress()
    {
        var saved = _services.Settings.Current.Ai.Profiles.FirstOrDefault(p => p.Id == _editingId);
        return saved is not null && SameServer(saved.BaseUrl, BaseUrl);
    }

    private static bool SameServer(string? a, string? b) =>
        Uri.TryCreate(a?.Trim(), UriKind.Absolute, out var left)
        && Uri.TryCreate(b?.Trim(), UriKind.Absolute, out var right)
        && left.GetLeftPart(UriPartial.Authority).Equals(right.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    /// <summary>키 칸이 비어 있고 저장된 키가 있는데 주소가 다른 서버로 바뀌었는가(이때 저장된 키로 시험하지 않는다).</summary>
    private bool StoredKeyBlockedByAddress(string? entered) =>
        string.IsNullOrWhiteSpace(entered) && HasStoredKey && !ClearKeyRequested && !StoredKeyMatchesAddress();

    private async Task<string?> ResolveKeyAsync(string? entered)
    {
        if (!string.IsNullOrWhiteSpace(entered))
        {
            return entered.Trim();
        }

        if (!HasStoredKey || ClearKeyRequested || _services.Credentials is not { } store || !StoredKeyMatchesAddress())
        {
            return null;
        }

        try
        {
            var bytes = await store.LoadAsync(AiProfileCredentials.KeyFor(_editingId)).ConfigureAwait(true);
            if (bytes is null)
            {
                return null;
            }

            var key = Encoding.UTF8.GetString(bytes);
            Array.Clear(bytes);
            return key.Length == 0 ? null : key;
        }
        catch (Exception ex) when (IsStoreError(ex))
        {
            return null;
        }
    }

    private bool CheckProbeInputs(bool needModel)
    {
        UrlError = AiProfileUrl.BaseFor(Format, BaseUrl) is null
            ? _t.T("http:// 또는 https:// 로 시작하는 올바른 주소를 입력하세요(사용자 정보·쿼리는 쓸 수 없습니다).",
                "Enter a valid address starting with http:// or https:// (user info and query strings are not allowed).")
            : string.Empty;
        ModelError = needModel && ModelName.Trim().Length == 0 ? _t.T("모델명을 입력하세요.", "Enter a model name.") : string.Empty;
        return UrlError.Length == 0 && ModelError.Length == 0;
    }

    /// <summary>서버의 모델 목록을 읽어 모델 후보와 이미지 지원 확인란을 채운다. 저장 전 입력으로도 할 수 있다. 사용자가 확인란을 바꿀 수 있다.</summary>
    public async Task RefreshModelsAsync(string? enteredKey)
    {
        if (_services.Probe is not { } probe || !CanProbe || !CheckProbeInputs(needModel: false))
        {
            return;
        }

        if (StoredKeyBlockedByAddress(enteredKey))
        {
            SetResult(AiResultKind.Failure, _t.T("주소가 저장된 프로필과 다른 서버로 바뀌어 저장된 키를 보내지 않습니다. API Key 를 다시 입력하세요.",
                "The address now points to a different server than the saved profile, so the stored key is not sent. Enter the API key again."));
            return;
        }

        var token = BeginOperation();
        var version = _version;
        var key = await ResolveKeyAsync(enteredKey).ConfigureAwait(true);
        if (_disposed || version != _version)
        {
            return;
        }

        SetResult(AiResultKind.Info, _t.T("모델 목록을 불러오는 중…", "Loading the model list…"));
        try
        {
            var result = await probe.ListModelsAsync(new AiProfileProbeRequest(BuildProfile(), key), token).ConfigureAwait(true);
            if (_disposed || version != _version)
            {
                return;
            }

            if (!result.Success)
            {
                SetResult(AiResultKind.Failure, _t.T("모델 목록을 불러오지 못했습니다: ", "Could not load the model list: ") + (result.Message ?? string.Empty));
                return;
            }

            ReplaceModels(result.Models.Select(m => m.Id));

            if (ModelName.Trim().Length == 0 && result.Models.Count > 0)
            {
                ModelName = result.Models[0].Id;
            }

            ApplyDetectedImages(result.Models);
            SetResult(AiResultKind.Success, _t.T($"모델 {result.Models.Count}개를 불러왔습니다.", $"Loaded {result.Models.Count} model(s)."));
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // 멈춤·창 닫기·프로필 전환이다(취소 뒤에 다른 예외로 끝나도 같다). 안내는 멈춘 쪽이 이미 남겼다.
        }
        catch (OperationCanceledException) when (!_disposed && version == _version)
        {
            // 구현 자체의 시간 초과: 취소 요청이 아니므로 실패로 보인다.
            SetResult(AiResultKind.Failure, _t.T("서버가 제때 응답하지 않았습니다.", "The server did not respond in time."));
        }
        finally
        {
            if (!_disposed && version == _version)
            {
                IsBusy = false;
            }
        }
    }

    private void ApplyDetectedImages(IReadOnlyList<AiModelInfo> models)
    {
        _detectedModels = models;
        _imagesManual = false; // 새 목록을 받으면 다시 서버 값을 따른다
        ReapplyDetectedImages();
    }

    // 받은 목록에서 지금 모델의 이미지 지원을 다시 찾는다. 사용자가 확인란을 직접 바꿨으면 건드리지 않는다.
    private void ReapplyDetectedImages()
    {
        if (_detectedModels.Count == 0 || _imagesManual)
        {
            return;
        }

        var chosen = _detectedModels.FirstOrDefault(m => string.Equals(m.Id, ModelName.Trim(), StringComparison.Ordinal));
        _applyingImages = true;
        try
        {
            if (chosen?.SupportsImages is { } supports)
            {
                SupportsImages = supports;
                ImagesNote = _t.T("서버 응답에서 자동으로 확인함", "Detected from the server response");
            }
            else if (ImagesNote.Length > 0)
            {
                SupportsImages = null; // 앞 모델에서 자동으로 채운 값이 이 모델에는 맞지 않는다
                ImagesNote = string.Empty;
            }
        }
        finally
        {
            _applyingImages = false;
        }
    }

    /// <summary>고른 모델로 작은 요청을 한 번 보내 성공·실패·걸린 시간을 보인다. 저장 전 입력으로도 할 수 있고 도중에 멈출 수 있다.</summary>
    public async Task TestConnectionAsync(string? enteredKey)
    {
        if (_services.Probe is not { } probe || !CanProbe || !CheckProbeInputs(needModel: true))
        {
            return;
        }

        if (StoredKeyBlockedByAddress(enteredKey))
        {
            SetResult(AiResultKind.Failure, _t.T("주소가 저장된 프로필과 다른 서버로 바뀌어 저장된 키를 보내지 않습니다. API Key 를 다시 입력하세요.",
                "The address now points to a different server than the saved profile, so the stored key is not sent. Enter the API key again."));
            return;
        }

        var token = BeginOperation();
        _operationIsTest = true;
        var version = _version;
        var key = await ResolveKeyAsync(enteredKey).ConfigureAwait(true);
        if (_disposed || version != _version)
        {
            return;
        }

        SetResult(AiResultKind.Info, _t.T("연결을 시험하는 중…", "Testing the connection…"));
        try
        {
            var result = await probe.TestConnectionAsync(new AiProfileProbeRequest(BuildProfile(), key), token).ConfigureAwait(true);
            if (_disposed || version != _version)
            {
                return;
            }

            var seconds = result.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
            var detail = string.IsNullOrWhiteSpace(result.Message) ? string.Empty : " · " + result.Message;
            SetResult(
                result.Success ? AiResultKind.Success : AiResultKind.Failure,
                result.Success
                    ? _t.T($"연결 성공 · {seconds} 초{detail}", $"Connected · {seconds} s{detail}")
                    : _t.T($"연결 실패 · {seconds} 초{detail}", $"Connection failed · {seconds} s{detail}"));
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // 멈춤·창 닫기·프로필 전환이다(취소 뒤에 다른 예외로 끝나도 같다).
        }
        catch (OperationCanceledException) when (!_disposed && version == _version)
        {
            SetResult(AiResultKind.Failure, _t.T("서버가 제때 응답하지 않았습니다.", "The server did not respond in time."));
        }
        finally
        {
            if (!_disposed && version == _version)
            {
                IsBusy = false;
            }
        }
    }

    [RelayCommand]
    private Task RefreshModels() => RefreshModelsAsync(EnteredKeyProvider?.Invoke());

    [RelayCommand]
    private Task TestConnection() => TestConnectionAsync(EnteredKeyProvider?.Invoke());

    /// <summary>창을 닫을 때: 도는 조회를 멈추고, 늦게 도착한 결과가 닫힌 창을 바꾸지 않게 한다.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelOperation();
    }
}
