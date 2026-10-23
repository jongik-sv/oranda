using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Localization;
using Oranda.App.ViewModels;
using Oranda.Core.Formatting;
using Oranda.Core.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Conversion;

// ConvertDialogViewModel 의 일부: 바꾸기 알림·id 검사·미리보기·문장 바꾸기(ConvertDialogViewModel.cs 에서 나눔, 동작 불변).
public sealed partial class ConvertDialogViewModel
{
    /// <summary>
    /// 바꿀 수 있는 포함 줄을 바꿔 보고 그 알림(<see cref="ConvertedStatement.Notes"/>)을 확인 열에 합친다. 엔진 예외는 알림 없음으로 둔다.
    /// </summary>
    private void UpdateNotes(ConvertLineViewModel line)
    {
        if (!line.Include || line.Kind is null)
        {
            line.SetConvertNotes([]);
            return;
        }

        try
        {
            line.SetConvertNotes(ConvertLine(line, Options).Notes);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            line.SetConvertNotes([]);
        }
    }

    private void UpdateAllNotes()
    {
        foreach (var line in Lines)
        {
            UpdateNotes(line);
        }
    }

    /// <summary>id 오류(형식·기존 매퍼와 겹침·줄끼리 겹침)와 단추 상태를 다시 계산한다. 제외한 줄은 오류를 보이지 않는다.</summary>
    private void Recompute()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var l in Lines.Where(l => l.Include))
        {
            counts[l.Id] = counts.GetValueOrDefault(l.Id) + 1;
        }

        foreach (var l in Lines)
        {
            l.IdError = !l.Include ? null
                : !_isValidId(l.Id) ? UiText.Get("MbConvert.id.invalid", "문장 id 형식이 맞지 않습니다 (영문자·숫자·밑줄, 영문자로 시작)")
                : _existingIds.Contains(l.Id) ? UiText.Get("MbConvert.id.existing", "기존 매퍼에 같은 id 가 있습니다")
                : counts[l.Id] > 1 ? UiText.Get("MbConvert.id.duplicate", "다른 줄과 id 가 겹칩니다")
                : null;
        }

        OnPropertyChanged(nameof(IncludedCount));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(CanConfirm));
    }

    private void RefreshPreview()
    {
        OnPropertyChanged(nameof(PreviewTitle));
        OnPropertyChanged(nameof(FormatCaption));
        if (SelectedLine is not { Kind: not null } line)
        {
            PreviewXml = string.Empty;
            ScenarioText = string.Empty;
            return;
        }

        try
        {
            var statement = ConvertLine(line, Options);
            PreviewXml = statement.ElementText;
            ScenarioText = SaveScenario ? ScenarioSummary(statement.ScenarioValues) : UiText.Get("MbConvert.scenario.off", "시나리오로 저장하지 않습니다.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            PreviewXml = ex.Message;
            ScenarioText = string.Empty;
        }
    }

    private ConvertedStatement ConvertLine(ConvertLineViewModel line, ConvertOptions options)
    {
        var binds = line.Candidate.TabIndex >= 0 && line.Candidate.TabIndex < _tabs.Count ? _tabs[line.Candidate.TabIndex].Binds : [];
        return _convert(line.Candidate, line.Kind ?? line.Candidate.Kind ?? ConvertStatementKind.Select, line.Id, binds, options, _formatter);
    }

    /// <summary><c>이름 = 값 · …</c>. 값이 하나도 없으면 안내 글.</summary>
    internal static string ScenarioSummary(IReadOnlyDictionary<string, MyBatisScenarioValue> values)
    {
        if (values.Count == 0)
        {
            return UiText.Get("MbConvert.scenario.none", "바인드 값이 없습니다.");
        }

        return string.Join(" · ", values.Select(kv => kv.Key + " = " + ValueText(kv.Value)));
    }

    private static string ValueText(MyBatisScenarioValue v) => v.Kind switch
    {
        MyBatisScenarioValueKind.Null => "NULL",
        MyBatisScenarioValueKind.Text => "'" + v.Text + "'",
        MyBatisScenarioValueKind.List => "[" + string.Join(", ", (v.Items ?? []).Select(i => v.ItemKind == MyBatisScenarioValueKind.Text ? "'" + i + "'" : i)) + "]",
        _ => v.Text ?? string.Empty,
    };
}
