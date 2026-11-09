using System.Text;
using Oranda.Core.Execution;
using Oranda.Core.Transfer;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Transfer;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 내보내기(TI1a)를 실제 서버로 확인한다(읽기만): Tibero 질의 경로의 결과 집합을 <see cref="TiberoDataTransferService"/> 가 CSV·JSON·INSERT·xlsx 로 쓴다.
/// 고정 값(DUAL)으로 형식별 셀이 파일에 맞게 나오는지, 여러 페이지에 걸친 행이 빠짐없이 나오는지 본다. 서버·드라이버가 없으면 아무것도 하지 않는다.
/// </summary>
[Trait("Category", "Integration")]
public class TiberoExportIt
{
    private static readonly IReadOnlyDictionary<string, BindValue> NoBinds = new Dictionary<string, BindValue>();

    private const string Typed =
        "SELECT 1 AS ID, 'a,b' AS NAME, DATE '2026-03-05' AS D, TIMESTAMP '2026-03-05 14:30:15.25' AS TS, TO_CLOB('clobtext') AS C, HEXTORAW('CAFE') AS R, 1234.5 AS N FROM DUAL " +
        "UNION ALL SELECT 2, NULL, NULL, NULL, NULL, NULL, NULL FROM DUAL";

    [TiberoItFact]
    public async Task Typed_rows_are_written_as_csv_json_insert_and_xlsx()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var service = NewService(it);
        var query = new SqlStatement(Typed, default, StatementKind.Query);

        var csv = await ExportAsync(service, session, query, new ExportOptions(TransferFormat.Csv));
        var lines = csv.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        Assert.Equal(2, csv.Rows);
        Assert.Equal(3, lines.Count);
        Assert.Equal("ID,NAME,D,TS,C,R,N", lines[0]);
        Assert.Contains("\"a,b\"", lines[1], StringComparison.Ordinal);
        Assert.Contains("2026-03-05", lines[1], StringComparison.Ordinal);
        Assert.Contains("14:30:15.25", lines[1], StringComparison.Ordinal);
        Assert.Contains("clobtext", lines[1], StringComparison.Ordinal);
        Assert.Contains("CAFE", lines[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1234.5", lines[1], StringComparison.Ordinal);

        var json = await ExportAsync(service, session, query, new ExportOptions(TransferFormat.Json));
        using (var doc = System.Text.Json.JsonDocument.Parse(json.Text))
        {
            Assert.Equal(2, doc.RootElement.GetArrayLength());
        }

        var insert = await ExportAsync(service, session, query, new ExportOptions(TransferFormat.Insert) { InsertTableName = "ORT_EXPORT_TARGET" });
        Assert.Equal(2, insert.Text.Split("INSERT INTO", StringSplitOptions.None).Length - 1);
        Assert.Contains("'a,b'", insert.Text, StringComparison.Ordinal);

        var xlsx = await ExportBytesAsync(service, session, query, new ExportOptions(TransferFormat.Xlsx));
        Assert.True(xlsx.Length > 100);
        Assert.Equal((byte)'P', xlsx[0]); // zip 머리
        Assert.Equal((byte)'K', xlsx[1]);
    }

    [TiberoItFact]
    public async Task Rows_across_many_pages_are_all_written()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var service = NewService(it);
        var query = new SqlStatement("SELECT ROWNUM AS N FROM all_objects WHERE ROWNUM <= 1200", default, StatementKind.Query);

        var csv = await ExportAsync(service, session, query, new ExportOptions(TransferFormat.Csv));

        Assert.Equal(1200, csv.Rows);
        var lines = csv.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1201, lines.Length);
        Assert.Equal("1200", lines[^1].TrimEnd('\r'));
    }

    private static async Task<(string Text, long Rows)> ExportAsync(TiberoDataTransferService service, Oranda.Tibero.Sessions.TiberoSession session, SqlStatement query, ExportOptions options)
    {
        using var stream = new MemoryStream();
        var summary = await service.ExportAsync(session, query, NoBinds, options, stream, null, CancellationToken.None);
        return (new UTF8Encoding(false).GetString(stream.ToArray()).TrimStart('﻿'), summary.Rows);
    }

    private static async Task<byte[]> ExportBytesAsync(TiberoDataTransferService service, Oranda.Tibero.Sessions.TiberoSession session, SqlStatement query, ExportOptions options)
    {
        using var stream = new MemoryStream();
        await service.ExportAsync(session, query, NoBinds, options, stream, null, CancellationToken.None);
        return stream.ToArray();
    }

    private static TiberoDataTransferService NewService(TiberoItSessions it) =>
        new(new TiberoExecutionService(new AllowAll(), new NoQuestions(), it.Events, null, dbmsOutput: false), new TiberoMetadataProvider(new Schema.MemoryMetadataCache(), it.Events, Timeout.InfiniteTimeSpan, TiberoCatalogQuery.RunAsync));

    private sealed class AllowAll : IExecutionPolicy
    {
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct) =>
            ValueTask.FromResult(PolicyDecision.Allowed);
    }

    private sealed class NoQuestions : IExecutionInteraction
    {
        public Task<bool> ConfirmAsync(SqlStatement stmt, PolicyDecision decision, CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

        public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) =>
            Task.FromResult(ScriptErrorAction.Stop);
    }
}
