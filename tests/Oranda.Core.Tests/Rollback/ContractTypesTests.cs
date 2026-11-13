using System.Text;
using System.Text.Json;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Serialization;
using Oranda.Core.Settings;
using Oranda.Core.Text;
using Oranda.Testing;
using Oranda.Testing.Rollback;

namespace Oranda.Core.Tests.Rollback;

/// <summary>되돌리기 지점 공유 계약(RB-00)의 기본값·끔 구현·직렬화·가짜.</summary>
public class ContractTypesTests
{
    private static readonly SqlStatement Update = new("UPDATE T SET A = :a WHERE ID = 1", default(TextRange), StatementKind.Dml);

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, false, true, false)]
    public void ConfirmAnswer_default_follows_offer(bool applicable, bool overLimit, bool defaultOn, bool expected)
    {
        var extras = new ConfirmExtras(new RollbackOffer(applicable, NotCreatedReason.None) { OverLimit = overLimit, DefaultOn = defaultOn }, null);
        var answer = ConfirmAnswer.Default(extras);
        Assert.True(answer.Proceed);
        Assert.Equal(expected, answer.CreateScript);
        Assert.Null(answer.BackupSql);
        Assert.False(ConfirmAnswer.Default(null).CreateScript);
    }

    [Fact]
    public void Settings_without_rollback_key_load_defaults()
    {
        var settings = JsonSerializer.Deserialize("""{ "schemaVersion": 1 }""", OrandaJsonContext.Default.AppSettings)!;
        Assert.Equal(new RollbackSettings(), settings.Rollback);
        Assert.Equal(RollbackSettings.DefaultMaxRows, settings.Rollback.MaxRowsPerStatement);

        var nulled = JsonSerializer.Deserialize("""{ "rollback": null }""", OrandaJsonContext.Default.AppSettings)!;
        Assert.NotNull(nulled.Rollback);

        var json = JsonSerializer.Serialize(new AppSettings { Rollback = new RollbackSettings { RetentionDays = 7 } }, OrandaJsonContext.Default.AppSettings);
        Assert.Contains("\"rollback\"", json, StringComparison.Ordinal);
        Assert.Equal(7, JsonSerializer.Deserialize(json, OrandaJsonContext.Default.AppSettings)!.Rollback.RetentionDays);
        Assert.Equal(RollbackSettings.MaxMaxRows, new RollbackSettings { MaxRowsPerStatement = 5_000_000 }.Normalized().MaxRowsPerStatement);
    }

    [Fact]
    public async Task Disabled_capture_passes_statement_and_binds_through()
    {
        var binds = new Dictionary<string, BindValue> { ["a"] = new(BindDataType.Number, 1m) };
        var request = new RollbackRequest(new FakeSession(), Update, binds, ExecutionOptions.Default);
        var channel = new FakeRollbackChannel();
        var dialect = new FakeRollbackDialect();

        Assert.Same(ConfirmExtras.None, await RollbackCapture.Disabled.PrepareAsync(request, channel, dialect, CancellationToken.None));
        var cap = await RollbackCapture.Disabled.BeginAsync(request, ConfirmExtras.None, ConfirmAnswer.Default(null), channel, dialect, CancellationToken.None);
        Assert.Same(Update, cap.StatementToSend);
        Assert.Same(binds, cap.BindsToSend);
        Assert.Same(RollbackStatementSummary.None, await cap.CompleteAsync(1, new Dictionary<string, object?>(), "1.2.3", CancellationToken.None));
        Assert.Empty(channel.SentSql);
        Assert.Null(DisabledRollbackLedger.Instance.Open(Guid.NewGuid()));
        Assert.Null(NullDmlAnalyzer.Instance.Analyze(Update.Text));
        Assert.Equal(Oranda.Core.Rollback.ChangeRecording.Normal, ExecutionOptions.Default.Recording);
    }

    [Fact]
    public void Script_document_round_trips_as_camel_case_json()
    {
        var doc = new RollbackScriptDocument(RollbackScriptDocument.CurrentSchemaVersion, 318,
        [
            new RollbackScriptStatement(1, new DateTimeOffset(2026, 11, 12, 5, 3, 21, TimeSpan.Zero), "UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = 1",
                [new RollbackBind("a", BindDataType.Number, "1")],
                [new RollbackStep("UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = 4300 WHERE PRODUCT_ID = 1040 AND UNIT_PRICE = 4730") { Note = "n" }],
                [new RollbackProbe("SELECT COUNT(*) FROM DUAL", "SELECT COUNT(*) FROM DUAL")],
                Invalidated: false, IncompleteReason: null, RowsAffected: 12),
        ]);
        var json = JsonSerializer.Serialize(doc, RollbackJsonContext.Default.RollbackScriptDocument);
        Assert.Contains("\"schemaVersion\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"Number\"", json, StringComparison.Ordinal);

        var back = JsonSerializer.Deserialize(json, RollbackJsonContext.Default.RollbackScriptDocument)!;
        var s = Assert.Single(back.Statements);
        Assert.Equal(318, back.GroupId);
        Assert.Equal(doc.Statements[0].ExecutedAt, s.ExecutedAt);
        Assert.Equal(doc.Statements[0].Steps[0], Assert.Single(s.Steps));
        Assert.True(s.Steps[0].ExpectOneRow);
        Assert.Equal(12, s.RowsAffected);
    }

    [Fact]
    public async Task Fake_channel_records_order_truncates_and_fails()
    {
        var channel = new FakeRollbackChannel()
            .When("FOR UPDATE", [1], [2], [3])
            .Fail("ALL_TRIGGERS", FakeRollbackChannel.PrivilegeError());
        var none = new Dictionary<string, BindValue>();

        await channel.ExecuteAsync("SAVEPOINT ORANDA$RB", CancellationToken.None);
        var rows = await channel.QueryAsync("SELECT ROWID FROM T FOR UPDATE", none, 2, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<RollbackChannelException>(() => channel.QueryAsync("SELECT * FROM ALL_TRIGGERS", none, 10, CancellationToken.None));

        Assert.Equal(2, rows.Rows.Count);
        Assert.True(rows.Truncated);
        Assert.Equal(DbErrorKind.InsufficientPrivileges, ex.Error.Kind);
        Assert.Equal(["SAVEPOINT ORANDA$RB", "SELECT ROWID FROM T FOR UPDATE", "SELECT * FROM ALL_TRIGGERS"], channel.SentSql);
    }

    [Fact]
    public async Task Fake_vault_fails_on_other_associated_data()
    {
        var vault = new FakeRollbackVault();
        var cipher = await vault.ProtectAsync(Encoding.UTF8.GetBytes("평문"), Encoding.UTF8.GetBytes("orb1:1:k"), CancellationToken.None);
        Assert.Equal("평문", Encoding.UTF8.GetString(await vault.UnprotectAsync(cipher, Encoding.UTF8.GetBytes("orb1:1:k"), CancellationToken.None)));
        await Assert.ThrowsAsync<System.Security.Cryptography.CryptographicException>(
            () => vault.UnprotectAsync(cipher, Encoding.UTF8.GetBytes("orb1:2:k"), CancellationToken.None));
    }

    [Fact]
    public async Task InMemory_store_assigns_ids_and_searches_newest_first()
    {
        var store = new InMemoryRollbackStore();
        var t = new DbObjectRef("SAMPLE", "PRODUCTS", "TABLE");
        RollbackGroup Group(int minute, string key) => new()
        {
            ProfileId = Guid.Empty, ProfileName = "prod-example", DbKey = key, Tables = [t],
            StartedAt = new DateTimeOffset(2026, 11, 12, 5, minute, 0, TimeSpan.Zero), State = RollbackScriptState.Open,
        };

        var a = await store.SaveAsync(Group(1, "K1"), null, CancellationToken.None);
        var b = await store.SaveAsync(Group(2, "K1"), null, CancellationToken.None);
        await store.SaveAsync(Group(3, "K2"), null, CancellationToken.None);

        var found = await store.SearchAsync(new RollbackQuery { DbKey = "K1", Table = t }, CancellationToken.None);
        Assert.Equal([b, a], found.Select(g => g.Id));
        Assert.Equal(3, await store.MarkOrphansAsync(CancellationToken.None));
        Assert.Equal(RollbackScriptState.Unverified, (await store.GetAsync(a, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task InMemory_store_orders_by_end_time_and_keeps_open_groups_on_prune()
    {
        var store = new InMemoryRollbackStore();
        var old = DateTimeOffset.UtcNow.AddDays(-40);
        RollbackGroup G(DateTimeOffset started, DateTimeOffset? ended, RollbackScriptState state) => new()
        {
            ProfileId = Guid.Empty, ProfileName = "prod-example", DbKey = "K", StartedAt = started, EndedAt = ended, State = state,
        };

        var lateEnd = await store.SaveAsync(G(old, DateTimeOffset.UtcNow, RollbackScriptState.Confirmed), null, default);
        var open = await store.SaveAsync(G(old.AddDays(1), null, RollbackScriptState.Open), null, default);
        var expired = await store.SaveAsync(G(old, old.AddMinutes(1), RollbackScriptState.Confirmed), null, default);

        Assert.Equal([lateEnd, open, expired], (await store.SearchAsync(new RollbackQuery(), default)).Select(g => g.Id));
        await store.PruneAsync(30, default);
        Assert.Equal([lateEnd, open], store.Groups.Select(g => g.Id));
    }

    [Fact]
    public void Fake_dialect_follows_literal_table()
    {
        var d = new FakeRollbackDialect();
        Assert.Equal("UNISTR('\\C720\\C790')", d.Literal(new RollbackColumn("N", "NVARCHAR2(10)", RollbackValueKind.NationalText), "유자", out _));
        Assert.Equal("'it''s'", d.Literal(new RollbackColumn("V", "VARCHAR2(10)", RollbackValueKind.Text), "it's", out _));
        Assert.Equal("BINARY_DOUBLE_NAN", d.Literal(new RollbackColumn("D", "BINARY_DOUBLE", RollbackValueKind.BinaryDouble), double.NaN, out _));
        Assert.Null(d.Literal(new RollbackColumn("L", "CLOB", RollbackValueKind.Large), "x", out var reason));
        Assert.NotNull(reason);
        Assert.Equal("NULL", d.Literal(new RollbackColumn("V", "VARCHAR2(10)", RollbackValueKind.Text), null, out _));
        Assert.Equal(RollbackValueKind.TimestampLtz, d.KindOf(new ColumnInfo("C", 1, "TIMESTAMP(6) WITH LOCAL TIME ZONE")));
        Assert.Equal("AS OF SCN 48213907", d.AsOf(48213907));
    }
}
