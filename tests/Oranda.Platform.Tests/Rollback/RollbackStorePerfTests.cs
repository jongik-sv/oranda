using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Security;
using Oranda.Platform.Credentials;
using Oranda.Platform.Rollback;
using Oranda.Platform.Storage;
using Oranda.Testing;
using Xunit.Abstractions;

namespace Oranda.Platform.Tests.Rollback;

// 소유: RB-90 저장 쪽(RB-21 레인). 10만 행 UPDATE 의 원복 문서(원복 문장 10만 개)를 저장·읽는 시간과 암호화 파일 크기.
// 로컬에서는 돌리지 않는다(Category=Perf, CI 의 성능 실행에서만). 결과 줄은 "[RB-90] store-…" 로 남긴다.
[Trait("Category", "Perf")]
public sealed class RollbackStorePerfTests(ITestOutputHelper output) : IDisposable
{
    private const int Steps = 100_000;
    private const string Db = "FREE/FREEPDB1/SAMPLE";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-rollback-perf-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 무시한다.
        }
    }

    [Fact]
    public async Task Save_and_load_a_100k_step_document()
    {
        Directory.CreateDirectory(_dir);
        ICredentialStore credentials = OperatingSystem.IsWindows()
            ? new DpapiCredentialStore(new AppDataPaths(_dir))
            : new InMemoryCredentialStore();
        using var vault = new RollbackVault(credentials);
        var rollbackDir = Path.Combine(_dir, "rollback");
        using var store = SqliteRollbackStore.ForFile(Path.Combine(_dir, "oranda.db"), rollbackDir, vault, new PassThroughRedactor());

        var at = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
        var steps = new RollbackStep[Steps];
        for (var i = 0; i < Steps; i++)
        {
            var key = (i + 1).ToString(CultureInfo.InvariantCulture);
            var price = (1000 + (i % 9000)).ToString(CultureInfo.InvariantCulture);
            steps[i] = new RollbackStep($"UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = {price}, PRODUCT_NAME = '상품 {key}', UPDATED_AT = "
                + $"TO_TIMESTAMP('2026-11-13 09:00:00.123456789', 'SYYYY-MM-DD HH24:MI:SS.FF9') WHERE PRODUCT_ID = {key} AND UNIT_PRICE = {price}1");
        }

        var document = new RollbackScriptDocument(RollbackScriptDocument.CurrentSchemaVersion, 0,
        [
            new RollbackScriptStatement(1, at, "UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = UNIT_PRICE * 1.1, UPDATED_AT = SYSTIMESTAMP WHERE CATEGORY_ID = :c",
                [new RollbackBind("c", BindDataType.Number, "3")], steps, [], false, null, Steps),
        ]);
        var group = new RollbackGroup
        {
            ProfileId = Guid.NewGuid(),
            ProfileName = "prod-example",
            DbKey = Db,
            Environment = ConnectionEnvironment.Production,
            Dbms = Dbms.Oracle,
            StartedAt = at,
            EndedAt = at.AddSeconds(30),
            State = RollbackScriptState.Confirmed,
            StepCount = Steps,
            RowsTotal = Steps,
            Tables = [new DbObjectRef("SAMPLE", "PRODUCTS", "TABLE")],
            Statements =
            [
                new RollbackStatementInfo(1, at, DmlVerb.Update, new DbObjectRef("SAMPLE", "PRODUCTS", "TABLE"),
                    "UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = UNIT_PRICE * 1.1 …", Steps, null, RollbackScriptState.Confirmed, null, Steps, false),
            ],
        };

        await store.SearchAsync(new RollbackQuery(), default); // 마이그레이션·키 만들기는 재지 않는다
        await vault.ProtectAsync(ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, default);

        var save = Stopwatch.StartNew();
        var id = await store.SaveAsync(group, document, default);
        save.Stop();

        var load = Stopwatch.StartNew();
        var loaded = await store.LoadDocumentAsync(id, default);
        load.Stop();

        Assert.NotNull(loaded);
        Assert.Equal(Steps, Assert.Single(loaded.Statements).Steps.Count);
        var file = Assert.Single(Directory.GetFiles(rollbackDir, "*.orb", SearchOption.AllDirectories));
        var bytes = new FileInfo(file).Length;
        output.WriteLine($"[RB-90] store-save: {Ms(save.Elapsed)} ms ({Steps} steps)");
        output.WriteLine($"[RB-90] store-load: {Ms(load.Elapsed)} ms ({Steps} steps)");
        output.WriteLine($"[RB-90] store-file: {bytes.ToString(CultureInfo.InvariantCulture)} bytes ({(bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)} MiB, "
            + $"{(bytes / (double)Steps).ToString("0", CultureInfo.InvariantCulture)} bytes/step)");

        // 느슨한 상한: 저장·읽기가 사용자 커밋을 눈에 띄게 늦추지 않는다(수치는 RB-90 이 판단).
        Assert.True(save.Elapsed < TimeSpan.FromSeconds(30), $"save {Ms(save.Elapsed)} ms");
        Assert.True(load.Elapsed < TimeSpan.FromSeconds(30), $"load {Ms(load.Elapsed)} ms");
    }

    private static string Ms(TimeSpan t) => t.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture);

    private sealed class PassThroughRedactor : ISecretRedactor
    {
        public string Redact(string sqlText) => sqlText;

        public bool IsSensitiveBindName(string bindName) => false;
    }
}
