using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.ChangeBundle;
using Oranda.Core.Connection;
using Oranda.Core.Language;
using Oranda.Core.Schema.TableEdit;

namespace Oranda.App.Features.ChangeBundle;

/// <summary>
/// 「의뢰용 스크립트 저장…」 창(설계 9.4, TE-17·TE-22): 체크한 문장만 화면 순서대로, 소유자 바꾸기를 거쳐 <see cref="RequestScriptWriter"/> 형식으로
/// UTF-8(BOM 없음)·OS 기본 줄바꿈 <c>.sql</c> 한 파일에 쓴다. DDL 이 아닌 문장이 있으면 저장을 막는다(쓰기 전에 전부 확인).
/// </summary>
public sealed partial class ChangeBundleSaveViewModel : ObservableObject
{
    private readonly ChangeBundleInfo _bundle;
    private readonly IReadOnlyList<ChangeBundleItem> _items;
    private readonly IOwnerRewriter? _rewriter;
    private readonly ILanguageService? _language;

    internal ChangeBundleSaveViewModel(ChangeBundleInfo bundle, IReadOnlyList<ChangeBundleItem> checkedItems, IOwnerRewriter? rewriter,
        ILanguageService? language)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(checkedItems);
        _bundle = bundle;
        _items = checkedItems;
        _rewriter = rewriter;
        _language = language;
        foreach (var row in OwnerMapRow.For(bundle, checkedItems))
        {
            OwnerMap.Add(row);
        }

        var redacted = ChangeBundleScript.RedactedCount(checkedItems);
        _statusText = redacted > 0 ? $"⚠ 비밀 값을 가린 문장 {redacted}개(***)는 저장한 파일에서 다시 적어야 한다." : string.Empty;
    }

    public ObservableCollection<OwnerMapRow> OwnerMap { get; } = [];

    public bool HasOwnerMap => OwnerMap.Count > 0;

    /// <summary>소유자를 붙이지 못한 문장 안내(소유자 바꾸기가 닿지 않는다). 없으면 빈 글.</summary>
    public string OwnerWarningText => ChangeBundleScript.UnqualifiedCount(_items) is var n and > 0
        ? $"⚠ 소유자를 붙이지 못한 문장 {n}개는 소유자 바꾸기가 적용되지 않고 실행 계정의 현재 스키마 기준으로 쓰인다."
        : string.Empty;

    public bool HasOwnerWarning => OwnerWarningText.Length > 0;

    /// <summary>머리 줄: 「묶음 이름 · 대상 DBMS · 체크한 문장 N개」.</summary>
    public string HeaderText => $"{_bundle.Name} · {Dbms} · 체크한 문장 {_items.Count}개";

    /// <summary>대상 DBMS(빈 묶음은 저장할 문장이 없다 — 그때는 Oracle).</summary>
    public Dbms Dbms => _bundle.Dbms ?? Dbms.Oracle;

    public bool CanSave => _items.Count > 0;

    [ObservableProperty]
    private string _statusText;

    /// <summary>저장 창이 권하는 파일 이름(묶음 이름에서 파일에 못 쓰는 글자를 뺀 것).</summary>
    public string SuggestedFileName
    {
        get
        {
            var invalid = Path.GetInvalidFileNameChars();
            var name = new string([.. _bundle.Name.Select(c => invalid.Contains(c) ? '_' : c)]).Trim();
            name = Regex.Replace(name, @"\s+", "_");
            return (name.Length == 0 ? "change-bundle" : name) + ".sql";
        }
    }

    /// <summary>파일 글(머리 주석 + 문장). DDL 이 아닌 문장이 있으면 <see cref="InvalidOperationException"/>.</summary>
    internal string BuildText(DateOnly createdOn, string newline)
    {
        var prepared = ChangeBundleScript.Prepare(_items, OwnerMapRow.ToMap(OwnerMap), _rewriter, _language);
        return RequestScriptWriter.Write(new RequestScriptHeader(_bundle.Name, createdOn, Dbms), [.. prepared.Select(p => p.Statement)], newline);
    }

    /// <summary>경로에 쓴다. 성공하면 true, 막히면 상태 줄에 이유를 두고 false.</summary>
    public async Task<bool> SaveAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!CanSave)
        {
            return false;
        }

        string text;
        try
        {
            text = BuildText(DateOnly.FromDateTime(DateTime.Now), Environment.NewLine);
        }
        catch (InvalidOperationException ex)
        {
            StatusText = "저장하지 않았다: " + ex.Message;
            return false;
        }

        try
        {
            await File.WriteAllTextAsync(path, text, RequestScriptWriter.Encoding, ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = "저장하지 못했다: " + ex.Message;
            return false;
        }

        StatusText = $"{_items.Count}문장을 저장했다: {path}";
        return true;
    }
}
