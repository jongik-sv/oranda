using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Oranda.App.Features.PlsqlWorkspace;

/// <summary>
/// PL/SQL 작업 공간 하나의 저장 형식(<c>{data}/plsql-workspaces/{id}.json</c>, 결정 71). Core 의 작업 공간 계약과 따로 둔다.
/// 탭 글은 늘 담는다: 앱을 켤 때는 접속이 없어 서버 소스를 바로 다시 읽을 수 없기 때문이다(접속이 오면 <see cref="PlsqlTabFile.LastDdlTime"/> 으로 서버 변경을 확인한다).
/// </summary>
public sealed record PlsqlWorkspaceFile
{
    /// <summary>이 형식의 판. 더 큰 판(새 버전 앱이 쓴 파일)은 읽기만 한다.</summary>
    public const int CurrentVersion = 1;

    public int SchemaVersion { get; init; } = CurrentVersion;

    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>보이던 탭의 차례(<see cref="Tabs"/> 안). 없으면 -1.</summary>
    public int ActiveTab { get; init; } = -1;

    /// <summary>이 작업 공간을 읽어 온(또는 쓴) <c>.plsql-workspace.md</c> 경로. 「작업 공간 파일로 저장」이 여기에 덮어쓴다. 없으면 null.</summary>
    public string? SourcePath { get; init; }

    /// <summary>파일에 적힌 원래 이름(열린 작업 공간과 겹쳐 <see cref="Name"/> 이 바뀐 경우만). 없으면 null.</summary>
    public string? SourceName { get; init; }

    public IReadOnlyList<PlsqlTabFile> Tabs { get; init; } = [];
}

/// <summary>PL/SQL 탭 하나. 서버 객체를 연 탭이면 소유자·이름·유형이 있고, 새 문서면 셋 다 null.</summary>
public sealed record PlsqlTabFile
{
    public string? Owner { get; init; }

    public string? Name { get; init; }

    public string? Type { get; init; }

    /// <summary>탭을 연 접속 프로필. 복원한 탭은 이 프로필의 세션이 열려야 서버와 비교·컴파일한다.</summary>
    public Guid? ConnectionProfileId { get; init; }

    /// <summary>열 때(또는 마지막 컴파일 때) 읽은 <c>last_ddl_time</c>(충돌 확인 기준).</summary>
    public DateTime? LastDdlTime { get; init; }

    public string? Title { get; init; }

    public string Text { get; init; } = string.Empty;

    /// <summary>저장 여부를 물어야 하는 글(서버·파일과 다르게 고침, 새 문서에 사용자가 씀).</summary>
    public bool IsDirty { get; init; }

    public string? FilePath { get; init; }

    /// <summary>파일에 묶인 탭의 인코딩 이름(<c>FileEncoding</c>: utf-8·utf-8-bom·cp949 …). 다시 켠 뒤 저장해도 BOM·코드 페이지를 지키려고 적는다. 없으면 utf-8.</summary>
    public string? Encoding { get; init; }

    public int Caret { get; init; }

    /// <summary>서버 소스 첫 줄에 앞에 붙인 글만큼의 열 차이(컴파일 오류 열 맞추기).</summary>
    public int Line1Shift { get; init; }
}

/// <summary>열린 작업 공간 색인(<c>open.json</c>): 다음 시작 때 이 차례로 연다. 설정 파일은 건드리지 않는다(결정 71).</summary>
public sealed record PlsqlWorkspaceIndex
{
    public int SchemaVersion { get; init; } = PlsqlWorkspaceFile.CurrentVersion;

    public IReadOnlyList<string> OpenIds { get; init; } = [];

    public string? LastId { get; init; }
}

/// <summary>저장 형식 직렬화. 소스 생성기라 리플렉션 직렬화를 하지 않는다(옵션은 data-model.md 1.3 공통 규칙과 같다).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(PlsqlWorkspaceFile))]
[JsonSerializable(typeof(PlsqlWorkspaceIndex))]
internal sealed partial class PlsqlWorkspaceJsonContext : JsonSerializerContext
{
}
