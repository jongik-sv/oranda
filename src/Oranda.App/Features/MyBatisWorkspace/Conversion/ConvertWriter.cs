using System.Data.Common;
using System.Security;
using Oranda.App.Features.Localization;
using Oranda.Core.MyBatis;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Conversion;

/// <summary>매퍼 쓰기 결과. <see cref="Error"/> 가 있으면 쓰지 않았다(원래 파일 그대로).</summary>
/// <param name="Error">쓰지 못한 이유(화면 글). 썼으면 null.</param>
/// <param name="ScenariosSaved">저장한 첫 시나리오 수.</param>
/// <param name="ScenarioError">매퍼는 썼지만 시나리오 저장에 실패한 이유(화면 글). 없으면 null. 이때도 매퍼는 연다.</param>
public sealed record ConvertWriteResult(string? Error, int ScenariosSaved, string? ScenarioError = null)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// 바꾼 문장을 매퍼 파일에 쓰고 첫 시나리오를 만든다(TSK-03-05 J4, design.md 5.2·6장).
/// 매퍼 원문은 Apply(<see cref="MapperEdit.InsertElement"/>) 로만 만들고 <see cref="IMapperFileWriter"/> 로 쓴다(위험 R6).
/// 새 매퍼도 빈 골격을 Parse 한 뒤 Apply 한다. 기존 매퍼는 바꾸지 않은 부분이 바이트까지 같다(MB-01).
/// 시나리오는 파일 쓰기가 끝난 뒤에만 저장한다.
/// </summary>
public sealed class ConvertWriter(IMapperFileWriter writer, IMyBatisScenarioStore? scenarios)
{
    /// <summary>MyBatis 3 매퍼 DOCTYPE. 없으면 MyBatis 의 DTD 검증이 실패한다(TSK-02-02 특이점 1).</summary>
    internal const string MapperDocType =
        "<!DOCTYPE mapper PUBLIC \"-//mybatis.org//DTD Mapper 3.0//EN\" \"https://mybatis.org/dtd/mybatis-3-mapper.dtd\">";

    /// <summary>새 매퍼 골격: xml 선언 + DOCTYPE + 빈 mapper. 줄 끝은 <paramref name="newLine"/>, 끝 줄바꿈 있음.</summary>
    public static string NewMapperText(string ns, string newLine)
    {
        ArgumentNullException.ThrowIfNull(ns);
        ArgumentNullException.ThrowIfNull(newLine);
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + newLine
            + MapperDocType + newLine
            + "<mapper namespace=\"" + SecurityElement.Escape(ns) + "\">" + newLine
            + "</mapper>" + newLine;
    }

    /// <summary>
    /// 기존 매퍼의 namespace·문장 id(문장 4종만). 대화 상자가 경로를 바꿀 때마다 부르므로(겹침 표시) 동기로 바로 읽는다.
    /// 없거나 읽지 못하면 <see cref="ExistingMapperRead.Error"/>.
    /// </summary>
    public static ExistingMapperRead ReadExisting(string path)
    {
        if (!File.Exists(path))
        {
            return new ExistingMapperRead(null, [], Text("MbConvert.error.missing", "매퍼 파일이 없습니다."));
        }

        try
        {
            var document = new MapperParser().Parse(File.ReadAllBytes(path));
            return new ExistingMapperRead(document.MapperNamespace, [.. document.Elements.Where(e => e.IsStatement && e.Id is not null).Select(e => e.Id!)]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return new ExistingMapperRead(null, [], Text("MbConvert.error.read", "매퍼를 읽지 못했습니다: ") + ex.Message);
        }
    }

    /// <summary>새 매퍼 파일을 만든다. 경로에 파일이 이미 있으면 쓰지 않는다(기존 매퍼 추가로 안내).</summary>
    public async Task<ConvertWriteResult> WriteNewAsync(string path, string ns, IReadOnlyList<ConvertedStatement> statements, bool saveScenarios, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(statements);
        if (File.Exists(path))
        {
            return Fail("MbConvert.error.exists", "같은 이름의 파일이 이미 있습니다. 「기존 매퍼에 문장 추가」 를 고르십시오.");
        }

        var format = MapperFileFormat.Default;
        var document = new MapperParser().Parse(NewMapperText(ns, format.NewLine));
        return await ApplyAndWriteAsync(path, document, statements, saveScenarios, ct).ConfigureAwait(false);
    }

    /// <summary>기존 매퍼 끝(<c>&lt;/mapper&gt;</c> 앞)에 문장을 더한다. 원래 문장들은 바이트가 같다.</summary>
    public async Task<ConvertWriteResult> AppendAsync(string path, IReadOnlyList<ConvertedStatement> statements, bool saveScenarios, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(statements);
        IMyBatisMapperDocument document;
        try
        {
            document = await ParseFileAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return Fail("MbConvert.error.read", "매퍼를 읽지 못했습니다: ", ex.Message);
        }

        return await ApplyAndWriteAsync(path, document, statements, saveScenarios, ct).ConfigureAwait(false);
    }

    private async Task<ConvertWriteResult> ApplyAndWriteAsync(
        string path, IMyBatisMapperDocument document, IReadOnlyList<ConvertedStatement> statements, bool saveScenarios, CancellationToken ct)
    {
        var indent = ElementIndent(document);
        var edits = statements.Select(s => (MapperEdit)new MapperEdit.InsertElement(s.Id, Indent(s.ElementText, indent))).ToList();
        string newText;
        try
        {
            newText = document.Apply(edits);
        }
        catch (ArgumentException ex)
        {
            return Fail("MbConvert.error.apply", "문장을 넣지 못했습니다: ", ex.Message);
        }

        var check = document.Check(newText, document.Hash);
        if (!check.IsWellFormed)
        {
            return Fail("MbConvert.error.xml", "바꾼 매퍼가 잘 짜인 xml 이 아닙니다: ", check.XmlError);
        }

        if (check.DuplicateIds.Count > 0)
        {
            return Fail("MbConvert.error.duplicate", "문장 id 가 겹칩니다: ", string.Join(", ", check.DuplicateIds));
        }

        try
        {
            await writer.WriteAsync(path, newText, document.Format, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("MbConvert.error.write", "매퍼를 쓰지 못했습니다: ", ex.Message);
        }

        var (saved, scenarioError) = saveScenarios ? await SaveScenariosAsync(path, document.MapperNamespace, statements, ct).ConfigureAwait(false) : (0, null);
        return new ConvertWriteResult(null, saved, scenarioError);
    }

    /// <summary>값이 있는 문장마다 첫 시나리오 「바꿀 때 값」 을 만든다. 파라미터가 없는 문장은 만들지 않는다.</summary>
    /// <remarks>
    /// 매퍼는 이미 썼으므로 저장소 실패(SQLite·취소 등)는 던지지 않고 이유를 돌려준다: 부른 쪽이 알리고 매퍼는 그대로 연다
    /// (다시 바꾸면 id 겹침으로 막히므로 여기서 멈추면 안 된다).
    /// </remarks>
    private async Task<(int Saved, string? Error)> SaveScenariosAsync(string path, string? ns, IReadOnlyList<ConvertedStatement> statements, CancellationToken ct)
    {
        if (scenarios is null)
        {
            return (0, null);
        }

        var name = Text("MbConvert.scenarioName", "바꿀 때 값");
        var fullPath = Path.GetFullPath(path);
        var pending = statements.Where(s => s.ScenarioValues.Count > 0).ToList();
        int saved = 0;
        try
        {
            foreach (var s in pending)
            {
                await scenarios.SaveAsync(
                    new MyBatisScenario { MapperPath = fullPath, Namespace = ns, StatementId = s.Id, Name = name, Values = s.ScenarioValues },
                    ct).ConfigureAwait(false);
                saved++;
            }
        }
        catch (Exception ex) when (ex is DbException or IOException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
        {
            var message = ConvertText.Format("MbConvert.error.scenario", "매퍼는 썼지만 시나리오 {0}개를 저장하지 못했습니다: ", pending.Count - saved);
            return (saved, message + ex.Message);
        }

        return (saved, null);
    }

    /// <summary>엔진 결과(태그 0칸, 줄 끝 \n)의 둘째 줄부터 요소 들여쓰기를 붙인다. 첫 줄은 Apply 가 붙인다. 빈 줄·여러 줄 리터럴 안 줄은 그대로 둔다.</summary>
    internal static string Indent(string elementText, string indent) => ConvertEngine.IndentElement(elementText, indent);

    /// <summary>새 요소의 들여쓰기: 마지막 요소 줄의 앞 공백(Apply 의 <c>InsertBeforeClose</c> 와 같은 규칙), 요소가 없으면 2칸.</summary>
    internal static string ElementIndent(IMyBatisMapperDocument document)
    {
        if (document.Elements.Count == 0)
        {
            return "  ";
        }

        var text = document.Text;
        int start = document.Elements[^1].ElementRange.Start;
        int lineStart = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        int i = lineStart;
        while (i < start && text[i] is ' ' or '\t')
        {
            i++;
        }

        return i == start ? text[lineStart..start] : "  ";
    }

    /// <summary>바이트로 읽는다(euc-kr 매퍼를 UTF-8 로 잘못 읽지 않게, handoff-02-01).</summary>
    private static async Task<IMyBatisMapperDocument> ParseFileAsync(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return new MapperParser().Parse(bytes);
    }

    private static ConvertWriteResult Fail(string key, string fallback, string? detail = null) => new(Text(key, fallback) + detail, 0);

    private static string Text(string key, string fallback) => UiText.Get(key, fallback);
}
