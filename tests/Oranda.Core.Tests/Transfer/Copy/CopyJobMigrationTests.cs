using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.Core.Transfer.Copy;
using Xunit;

namespace Oranda.Core.Tests.Transfer.Copy;

/// <summary>
/// 복사 작업 1 → 2(묶음 B): 얼린 예제 <c>docs/design/examples/v1/copy-job.json</c> 을 올리면 규칙마다 작업 계정이 채워진다.
/// 새 버전 예제 <c>examples/copy-job.json</c> 은 같은 내용 + 다른 계정 규칙 하나다(data-model 4.1 의 예제 비교 시험).
/// </summary>
public class CopyJobMigrationTests
{
    [Fact]
    public void V1_example_upgrades_to_v2_example_with_job_owners_on_every_rule()
    {
        var v1 = JsonNode.Parse(File.ReadAllText(Path.Combine(ExamplesDirectory(), "v1", "copy-job.json")))!;
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(ExamplesDirectory(), "copy-job.json")))!;
        expected["tables"]!.AsArray().RemoveAt(4); // 새 예제에만 있는 다른 계정 규칙
        expected.AsObject().Remove("variables"); // 새 예제에만 있는 작업 변수(버전 3)

        var upgraded = CopyJobMigrations.Upgrade(v1, 1);

        AssertSame(expected, upgraded, "$");
        var job = upgraded.Deserialize(CopyJobJsonContext.Default.CopyJob)!;
        Assert.All(job.Tables, r => Assert.Equal(r.Table, CopyOwners.Key(job, r))); // 키는 예전 표 이름 그대로
        Assert.Equal("SRC", job.Tables[2].Parent!.ParentOwner);
        Assert.Equal("DST", job.Sequences[0].Owner);
    }

    [Fact]
    public void V2_example_round_trips()
    {
        var text = File.ReadAllText(Path.Combine(ExamplesDirectory(), "copy-job.json"));
        var job = JsonSerializer.Deserialize(text, CopyJobJsonContext.Default.CopyJob)!;
        Assert.Equal(CopyJobSchema.Current, job.SchemaVersion);
        Assert.Equal("APP2.ORDERS", CopyOwners.Key(job, job.Tables[4]));
        Assert.Equal(["C2611001", "C2611002"], job.Variables[0].Values);
        Assert.Equal(CopyVariableType.Number, job.Variables[1].Type);
        AssertSame(JsonNode.Parse(text)!, JsonNode.Parse(JsonSerializer.Serialize(job, CopyJobJsonContext.Default.CopyJob))!, "$");
    }

    [Fact]
    public void Migration_keeps_owners_already_written_and_ignores_missing_parts()
    {
        var node = JsonNode.Parse("""
            {"schemaVersion":1,"sourceOwner":"S","targetOwner":"T",
             "tables":[{"table":"A","kind":"All","action":"MergeByPk","targetOwner":"X"}]}
            """)!;
        var up = new CopyJobMigrationV1().Apply(node);
        Assert.Equal("S", (string?)up["tables"]![0]!["sourceOwner"]);
        Assert.Equal("X", (string?)up["tables"]![0]!["targetOwner"]);
        Assert.Null(up["sequences"]);

        // 점 든 표 이름의 시퀀스 연결은 버전 2 규칙 키(따옴표) 꼴로 바뀐다. 보통 이름은 그대로.
        var dotted = JsonNode.Parse("""
            {"schemaVersion":1,"id":"1","name":"n","source":{"name":"s","user":"S"},"target":{"name":"t","user":"T"},"maskSalt":"AA==",
             "sourceOwner":"S","targetOwner":"T",
             "tables":[{"table":"A.B","kind":"All","action":"MergeByPk"},{"table":"C","kind":"All","action":"MergeByPk"}],
             "sequences":[{"sequence":"Q1","table":"A.B","column":"ID"},{"sequence":"Q2","table":"C","column":"ID"}]}
            """)!;
        var job = CopyJobMigrations.Upgrade(dotted, 1).Deserialize(CopyJobJsonContext.Default.CopyJob)!;
        Assert.Equal(["\"A.B\"", "C"], job.Sequences.Select(s => s.Table));
        Assert.All(job.Sequences, s => Assert.NotNull(CopyOwners.Find(job, s.Table)));

        var empty = JsonNode.Parse("""{"schemaVersion":1}""")!;
        Assert.Equal(CopyJobSchema.Current, (int)CopyJobMigrations.Upgrade(empty, 1)["schemaVersion"]!);
    }

    private static void AssertSame(JsonNode expected, JsonNode actual, string path)
    {
        switch (expected)
        {
            case JsonObject obj:
                var other = actual.AsObject();
                Assert.True(obj.Select(p => p.Key).Order(StringComparer.Ordinal).SequenceEqual(other.Select(p => p.Key).Order(StringComparer.Ordinal)),
                    $"{path}: 필드가 다르다 ({string.Join(",", obj.Select(p => p.Key))} ≠ {string.Join(",", other.Select(p => p.Key))})");
                foreach (var (key, child) in obj)
                {
                    AssertSame(child!, other[key]!, $"{path}.{key}");
                }

                break;
            case JsonArray array:
                var otherArray = actual.AsArray();
                Assert.Equal(array.Count, otherArray.Count);
                for (var i = 0; i < array.Count; i++)
                {
                    AssertSame(array[i]!, otherArray[i]!, $"{path}[{i}]");
                }

                break;
            default:
                Assert.True(JsonNode.DeepEquals(expected, actual), $"{path}: {expected.ToJsonString()} ≠ {actual.ToJsonString()}");
                break;
        }
    }

    private static string ExamplesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Oranda.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Oranda.sln 을 찾지 못했다."),
            "docs", "design", "examples");
    }
}
