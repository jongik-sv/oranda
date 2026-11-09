using System.Text.Json.Nodes;
using Oranda.Core.Settings;

namespace Oranda.Core.Transfer.Copy;

/// <summary>복사 작업 파일의 상향 변환기 목록(파일 종류 <see cref="FileKind"/>). 저장소의 읽기·가져오기가 같은 목록을 쓴다.</summary>
public static class CopyJobMigrations
{
    /// <summary>변환기 선택용 파일 종류.</summary>
    public const string FileKind = "copy-job";

    public static IReadOnlyList<ISettingsMigration> All { get; } = [new CopyJobMigrationV1(), new CopyJobMigrationV2(), new CopyJobMigrationV3()];

    /// <summary>
    /// <paramref name="version"/>(0 이면 1 로 본다)에서 <see cref="CopyJobSchema.Current"/> 까지 한 단계씩 변환한다. 미래 버전은 부른 쪽이 먼저 거른다.
    /// 변환기가 빠졌으면 <see cref="InvalidOperationException"/>.
    /// </summary>
    public static JsonNode Upgrade(JsonNode document, int version)
    {
        ArgumentNullException.ThrowIfNull(document);
        var work = document;
        for (var v = Math.Max(version, 1); v < CopyJobSchema.Current; v++)
        {
            var step = All.FirstOrDefault(m => m.FromVersion == v)
                       ?? throw new InvalidOperationException($"{FileKind} 의 {v}→{v + 1} 변환기가 없다.");
            work = step.Apply(work);
            work["schemaVersion"] = v + 1;
        }

        return work;
    }
}

/// <summary>복사 작업 3 → 4: 표 규칙의 <c>createIfMissing</c>(대상에 표가 없으면 만들기)이 생겼다. 버전 3 파일에는 없으므로(= 꺼짐) 바꿀 것이 없다. 순수 함수다.</summary>
public sealed class CopyJobMigrationV3 : ISettingsMigration
{
    public string FileKind => CopyJobMigrations.FileKind;

    public int FromVersion => 3;

    public JsonNode Apply(JsonNode document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document;
    }
}

/// <summary>복사 작업 2 → 3: 작업 변수(<c>variables</c>)가 생겼다. 버전 2 파일에는 변수가 없으므로 바꿀 것이 없다. 순수 함수다.</summary>
public sealed class CopyJobMigrationV2 : ISettingsMigration
{
    public string FileKind => CopyJobMigrations.FileKind;

    public int FromVersion => 2;

    public JsonNode Apply(JsonNode document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document;
    }
}

/// <summary>
/// 복사 작업 1 → 2(묶음 B): 규칙마다 원본·대상 계정을 둔다. 버전 1 의 뜻(모든 표 = 작업 계정)을 그대로 옮겨
/// <c>tables[].sourceOwner</c>·<c>tables[].targetOwner</c> 를 작업의 <c>sourceOwner</c>·<c>targetOwner</c> 로,
/// <c>tables[].parent.parentOwner</c> 를 작업 <c>sourceOwner</c> 로, <c>sequences[].owner</c> 를 작업 <c>targetOwner</c> 로 채운다(이미 있으면 둔다).
/// <c>sequences[].table</c> 은 규칙 키 꼴로 바꾼다(점·큰따옴표 든 표 이름만 따옴표로 감싸고, 보통 이름은 그대로).
/// 버전을 올리는 까닭: 예전 앱이 새 파일을 읽으면 표마다의 계정을 버리고 작업 대상 계정의 같은 이름 표를 지운다(data-model 4.1 의 의미 변경).
/// 순수 함수다.
/// </summary>
public sealed class CopyJobMigrationV1 : ISettingsMigration
{
    public string FileKind => CopyJobMigrations.FileKind;

    public int FromVersion => 1;

    public JsonNode Apply(JsonNode document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document is not JsonObject job)
        {
            return document;
        }

        var source = Text(job["sourceOwner"]);
        var target = Text(job["targetOwner"]);
        if (job["tables"] is JsonArray tables)
        {
            foreach (var table in tables.OfType<JsonObject>())
            {
                Fill(table, "sourceOwner", source);
                Fill(table, "targetOwner", target);
                if (table["parent"] is JsonObject parent)
                {
                    Fill(parent, "parentOwner", source);
                }
            }
        }

        if (job["sequences"] is JsonArray sequences)
        {
            foreach (var sequence in sequences.OfType<JsonObject>())
            {
                Fill(sequence, "owner", target);
                // 버전 1 의 시퀀스 표는 표 이름 그대로다. 버전 2 규칙 키는 점·큰따옴표 든 이름을 따옴표로 감싼다(CopyOwners.Key).
                if (Text(sequence["table"]) is { Length: > 0 } table)
                {
                    sequence["table"] = CopyOwners.KeyPart(table);
                }
            }
        }

        return document;
    }

    private static void Fill(JsonObject node, string name, string? value)
    {
        if (value is { Length: > 0 } && Text(node[name]) is not { Length: > 0 })
        {
            node[name] = value;
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
