using Oranda.Core.Transfer.Copy;
using Oranda.Platform.Storage;
using Oranda.Platform.Storage.Copy;

namespace Oranda.Platform.Tests.Storage.Copy;

// 복사 작업 파일 저장소: 저장·목록·이름 바꾸기·복제·삭제·가져오기·내보내기.
public sealed class CopyJobStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-copyjobs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AppDataPaths Paths => new(_root);

    private CopyJobStore NewStore() => new(Paths);

    private static CopyJob Sample(string name = "월간 복사", string? id = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N"),
        Name = name,
        Source = new ConnectionRef("src.example", "SCOTT"),
        Target = new ConnectionRef("dev.example", "SCOTT_DEV"),
        SourceOwner = "SCOTT",
        TargetOwner = "SCOTT_DEV",
        Period = new CopyPeriod(PeriodKind.PreviousMonth),
        Tables =
        [
            new TableRule("ORDERS", RuleKind.Period, TargetAction.DeleteRangeInsert)
            {
                PeriodColumns = [new PeriodColumn("ORDER_DATE")],
                Masks = [new MaskRule("PHONE", MaskKind.Hash)],
            },
        ],
        MaskSalt = "c2FsdC1mb3ItdGVzdA==",
    };

    [Fact]
    public async Task Save_list_rename_duplicate_delete_round_trip()
    {
        var store = NewStore();
        var job = Sample();
        await store.SaveAsync(job, CancellationToken.None);

        var listed = await store.ListAsync(CancellationToken.None);
        var entry = Assert.Single(listed);
        Assert.False(entry.IsReadOnly);
        Assert.Equal(job.Id, entry.Job.Id);
        Assert.Equal(job.Tables[0].Masks[0].Column, entry.Job.Tables[0].Masks[0].Column);

        await store.RenameAsync(job.Id, "분기 복사", CancellationToken.None);
        entry = Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.Equal("분기 복사", entry.Job.Name);
        Assert.Equal("분기 복사.json", Path.GetFileName(entry.FilePath));
        Assert.Single(Directory.GetFiles(Paths.CopyJobsDirectory, "*.json"));

        var copy = await store.DuplicateAsync(job.Id, "분기 복사 사본", CancellationToken.None);
        Assert.NotEqual(job.Id, copy.Id);
        Assert.Equal(job.MaskSalt, copy.MaskSalt);
        Assert.Equal(2, (await store.ListAsync(CancellationToken.None)).Count);

        await store.DeleteAsync(job.Id, CancellationToken.None);
        var left = Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.Equal(copy.Id, left.Job.Id);
    }

    [Fact]
    public async Task File_name_comes_from_job_name_and_collisions_get_number()
    {
        var store = NewStore();
        await store.SaveAsync(Sample("월간/테스트"), CancellationToken.None);
        await store.SaveAsync(Sample("월간/테스트"), CancellationToken.None);

        var names = Directory.GetFiles(Paths.CopyJobsDirectory, "*.json").Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(["월간_테스트 (2).json", "월간_테스트.json"], names);
    }

    [Fact]
    public async Task Saving_existing_job_overwrites_its_own_file()
    {
        var store = NewStore();
        var job = Sample();
        await store.SaveAsync(job, CancellationToken.None);
        await store.SaveAsync(job with { LastRun = new LastRunInfo(DateTimeOffset.UnixEpoch, CopyOutcome.Succeeded, 10) }, CancellationToken.None);

        var entry = Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.Equal(10, entry.Job.LastRun!.Rows);
        Assert.Single(Directory.GetFiles(Paths.CopyJobsDirectory, "*.json"));
    }

    [Fact]
    public async Task Import_assigns_new_id_on_collision_and_keeps_salt()
    {
        var store = NewStore();
        var job = Sample();
        await store.SaveAsync(job, CancellationToken.None);
        var exported = Path.Combine(_root, "shared.json");
        await store.ExportAsync(job.Id, exported, CancellationToken.None);

        var imported = await store.ImportAsync(exported, CancellationToken.None);

        Assert.NotEqual(job.Id, imported.Id);
        Assert.Equal("월간 복사 (2)", imported.Name);
        Assert.Equal(job.MaskSalt, imported.MaskSalt);
        Assert.Equal(2, (await store.ListAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Import_into_empty_store_keeps_id()
    {
        var store = NewStore();
        var job = Sample(id: "abc123");
        var exporter = Path.Combine(_root, "exp");
        Directory.CreateDirectory(exporter);
        var file = Path.Combine(exporter, "job.json");
        await File.WriteAllTextAsync(file, System.Text.Json.JsonSerializer.Serialize(job, CopyJobJsonContext.Default.CopyJob));

        var imported = await store.ImportAsync(file, CancellationToken.None);

        Assert.Equal("abc123", imported.Id);
        Assert.Equal("월간 복사", imported.Name);
    }

    [Fact]
    public async Task Import_rejects_broken_file_without_moving_it()
    {
        var store = NewStore();
        var file = Path.Combine(_root, "broken.json");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(file, "{ not json");

        await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportAsync(file, CancellationToken.None));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Future_schema_version_is_read_only()
    {
        var store = NewStore();
        var job = Sample();
        await store.SaveAsync(job, CancellationToken.None);
        var path = Assert.Single(await store.ListAsync(CancellationToken.None)).FilePath;
        var text = (await File.ReadAllTextAsync(path)).Replace($"\"schemaVersion\": {CopyJobSchema.Current}", "\"schemaVersion\": 99", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, text);

        var entry = Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.True(entry.IsReadOnly);
        Assert.False(string.IsNullOrEmpty(entry.Notice));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(job, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RenameAsync(job.Id, "새 이름", CancellationToken.None));
        Assert.Contains("\"schemaVersion\": 99", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }

    /// <summary>버전 1 파일(모든 표 = 작업 계정): 백업하고 규칙마다 작업 계정을 채워 버전 2 로 다시 쓴다.</summary>
    [Fact]
    public async Task Version_1_file_is_migrated_with_job_owners_on_each_rule()
    {
        var store = NewStore();
        Directory.CreateDirectory(Paths.CopyJobsDirectory);
        var path = Path.Combine(Paths.CopyJobsDirectory, "옛 작업.json");
        await File.WriteAllTextAsync(path, V1Json("v1-job"));

        var entry = Assert.Single(await store.ListAsync(CancellationToken.None));

        Assert.False(entry.IsReadOnly);
        var rule = entry.Job.Tables[1];
        Assert.Equal(("SCOTT", "SCOTT_DEV"), (rule.SourceOwner, rule.TargetOwner));
        Assert.Equal("SCOTT", rule.Parent!.ParentOwner);
        Assert.Equal("SCOTT_DEV", Assert.Single(entry.Job.Sequences).Owner);
        Assert.Contains($"\"schemaVersion\": {CopyJobSchema.Current}", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(Paths.BackupDirectory, "옛 작업.json.v1.*.bak"));
    }

    [Fact]
    public async Task Version_1_import_is_migrated()
    {
        var store = NewStore();
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "peer-v1.json");
        await File.WriteAllTextAsync(file, V1Json("peer1"));

        var imported = await store.ImportAsync(file, CancellationToken.None);

        Assert.Equal(CopyJobSchema.Current, imported.SchemaVersion);
        Assert.All(imported.Tables, r => Assert.Equal(("SCOTT", "SCOTT_DEV"), (r.SourceOwner, r.TargetOwner)));
    }

    /// <summary>버전 2 파일에 계정 칸이 일부만 있다: 빈 칸은 그대로 비워 읽고(변환하지 않음), 뜻은 CopyOwners 의 기본값 규칙을 따른다.</summary>
    [Fact]
    public async Task Version_2_file_with_some_owner_fields_missing_reads_with_defaults()
    {
        var store = NewStore();
        Directory.CreateDirectory(Paths.CopyJobsDirectory);
        var path = Path.Combine(Paths.CopyJobsDirectory, "일부 계정.json");
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 2, "id": "v2-part", "name": "일부 계정",
              "source": { "name": "src", "user": "SCOTT" }, "target": { "name": "dev", "user": "SCOTT_DEV" },
              "sourceOwner": "SCOTT", "targetOwner": "SCOTT_DEV",
              "tables": [
                { "table": "ORDERS", "kind": "All", "action": "MergeByPk", "sourceOwner": "APP2" },
                { "table": "CODES", "kind": "All", "action": "MergeByPk", "targetOwner": "CODES_DEV" },
                { "table": "ITEM", "kind": "ParentKey", "action": "DeleteRangeInsert",
                  "parent": { "parentTable": "ORDERS", "childColumns": ["ORDER_ID"], "parentColumns": ["ID"], "parentOwner": "APP2" } }
              ],
              "sequences": [ { "sequence": "SEQ_ITEM", "table": "ITEM", "column": "ID" } ],
              "maskSalt": "c2FsdA=="
            }
            """);

        var entry = Assert.Single(await store.ListAsync(CancellationToken.None));

        Assert.False(entry.IsReadOnly);
        var job = entry.Job;
        Assert.Equal([("APP2", null), (null, "CODES_DEV"), (null, null)], job.Tables.Select(r => (r.SourceOwner, r.TargetOwner)));
        Assert.Null(job.Sequences[0].Owner);

        // 대상 계정이 빈 다른 계정 규칙은 원본 계정 그대로, 원본 계정이 빈 규칙은 작업 기본 원본 계정.
        Assert.Equal("APP2.ORDERS", CopyOwners.TargetName(job, job.Tables[0]));
        Assert.Equal(("SCOTT.CODES", "CODES_DEV.CODES"), (CopyOwners.SourceName(job, job.Tables[1]), CopyOwners.TargetName(job, job.Tables[1])));
        Assert.Equal(("SCOTT.ITEM", "SCOTT_DEV.ITEM"), (CopyOwners.SourceName(job, job.Tables[2]), CopyOwners.TargetName(job, job.Tables[2])));
        Assert.Same(job.Tables[0], CopyOwners.Parent(job, job.Tables[2]));
        Assert.Equal("APP2", CopyOwners.ParentTarget(job, job.Tables[2]));
        Assert.Equal("SCOTT_DEV", CopyOwners.SequenceOwner(job, job.Sequences[0]));
        Assert.Empty(CopyOwners.Problems(job));

        // 버전 2 → 3 은 바꿀 것이 없다(작업 변수가 생겼을 뿐): 백업하고 버전만 올려 다시 쓰며, 빈 계정 칸은 채우지 않는다.
        Assert.Single(Directory.GetFiles(Paths.BackupDirectory, "일부 계정.json.v2.*.bak"));
        var rewritten = await File.ReadAllTextAsync(path);
        Assert.Contains($"\"schemaVersion\": {CopyJobSchema.Current}", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("\"targetOwner\": \"APP2\"", rewritten, StringComparison.Ordinal);
        Assert.Empty(job.Variables);
    }

    private static string V1Json(string id) => """
        {
          "schemaVersion": 1, "id": "ID_PLACE", "name": "옛 작업",
          "source": { "name": "src", "user": "SCOTT" }, "target": { "name": "dev", "user": "SCOTT_DEV" },
          "sourceOwner": "SCOTT", "targetOwner": "SCOTT_DEV",
          "tables": [
            { "table": "ORDERS", "kind": "All", "action": "MergeByPk" },
            { "table": "ITEM", "kind": "ParentKey", "action": "DeleteRangeInsert",
              "parent": { "parentTable": "ORDERS", "childColumns": ["ORDER_ID"], "parentColumns": ["ID"] } }
          ],
          "sequences": [ { "sequence": "SEQ_ITEM", "table": "ITEM", "column": "ID" } ],
          "maskSalt": "c2FsdA=="
        }
        """.Replace("ID_PLACE", id, StringComparison.Ordinal);

    [Fact]
    public async Task Future_schema_version_cannot_be_duplicated_or_exported()
    {
        var store = NewStore();
        var job = Sample();
        await store.SaveAsync(job, CancellationToken.None);
        var path = Assert.Single(await store.ListAsync(CancellationToken.None)).FilePath;
        await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace($"\"schemaVersion\": {CopyJobSchema.Current}", "\"schemaVersion\": 99", StringComparison.Ordinal));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DuplicateAsync(job.Id, "사본", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ExportAsync(job.Id, Path.Combine(_root, "x.json"), CancellationToken.None));
    }

    [Fact]
    public async Task Import_clears_last_run_and_replaces_unsafe_id()
    {
        var store = NewStore();
        var job = Sample(id: new string('a', 300)) with { LastRun = new LastRunInfo(DateTimeOffset.UnixEpoch, CopyOutcome.Succeeded, 5) };
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "peer.json");
        await File.WriteAllTextAsync(file, System.Text.Json.JsonSerializer.Serialize(job, CopyJobJsonContext.Default.CopyJob));

        var imported = await store.ImportAsync(file, CancellationToken.None);

        Assert.Null(imported.LastRun);
        Assert.Equal(32, imported.Id.Length);
    }

    [Fact]
    public async Task Long_korean_name_still_saves()
    {
        var store = NewStore();
        var name = new string('가', 200);
        await store.SaveAsync(Sample(name), CancellationToken.None);
        await store.SaveAsync(Sample(name), CancellationToken.None);

        Assert.Equal(2, (await store.ListAsync(CancellationToken.None)).Count);
        Assert.All(Directory.GetFiles(Paths.CopyJobsDirectory), f => Assert.True(System.Text.Encoding.UTF8.GetByteCount(Path.GetFileName(f)) <= 255));
    }

    [Fact]
    public async Task Saved_file_never_contains_password()
    {
        var store = NewStore();
        await store.SaveAsync(Sample(), CancellationToken.None);

        var path = Assert.Single(Directory.GetFiles(Paths.CopyJobsDirectory, "*.json"));
        var text = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("월간/테스트", "월간_테스트")]
    [InlineData("a:b*c?d", "a_b_c_d")]
    [InlineData("  끝 점..  ", "끝 점")]
    [InlineData("", "_")]
    [InlineData("CON", "_CON")]
    public void File_stem_replaces_invalid_characters(string name, string expected)
        => Assert.Equal(expected, CopyJobStore.ToFileStem(name));

    [Fact]
    public async Task Corrupt_file_is_skipped_and_moved_to_backup()
    {
        var store = NewStore();
        await store.SaveAsync(Sample("정상"), CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(Paths.CopyJobsDirectory, "깨짐.json"), "{ nope");

        var entry = Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.Equal("정상", entry.Job.Name);
        Assert.False(File.Exists(Path.Combine(Paths.CopyJobsDirectory, "깨짐.json")));
        Assert.True(Directory.Exists(Paths.BackupDirectory));
    }
}
