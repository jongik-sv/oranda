using Oranda.Core.Ai.Access;
using Oranda.Core.Connection;

namespace Oranda.Core.Tests.Connection;

// 소유: TSK-13-13. 접속 내보내기 파일(쓰기·읽기·형식 판정·같은 접속 판정).

public class ConnectionExportTests
{
    private static readonly ConnectionProfile Orders = new(
        "운영-주문DB", "app_ro", "tcps://db01:1522/ORDERS", Id: Guid.NewGuid(), Environment: ConnectionEnvironment.Production,
        Group: "주문/운영", CredentialRef: "oranda:conn:secret-ref", LastConnectedAt: DateTimeOffset.UtcNow, Comment: "조회 전용\n배치 때 쓰지 않음",
        Nls: new NlsSettings { DateFormat = "YYYY-MM-DD" });

    private static readonly ConnectionProfile Local = new(
        "개발-로컬", "scott", "localhost:1521/FREEPDB1", Color: "#0063B1", ReadOnly: true, TnsAdmin: "/opt/tns");

    [Fact]
    public void Ai_access_is_not_exported_and_an_imported_profile_falls_back_to_the_environment_default()
    {
        var turnedOn = Orders with { AiAccess = AiAccessLevel.Plan, AllowExplainPlan = true };

        var json = ConnectionExport.Write([turnedOn]);
        Assert.DoesNotContain("aiAccess", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("allowExplainPlan", json, StringComparison.OrdinalIgnoreCase);

        // 손으로 넣은 파일이어도 가져오면 지운다.
        var handMade = json.Replace("\"user\"", "\"aiAccess\": \"Plan\", \"user\"", StringComparison.Ordinal);
        Assert.Contains("aiAccess", handMade, StringComparison.Ordinal);
        var back = Assert.Single(ConnectionExport.Read(handMade));
        Assert.Null(back.AiAccess);
        Assert.Equal(AiAccessLevel.Query, AiAccessPolicy.Effective(back)); // 운영 환경 기본값(2026-10-29 결정)
    }

    [Fact]
    public void Written_file_round_trips_profile_values_without_secrets()
    {
        var json = ConnectionExport.Write([Orders, Local], new DateTimeOffset(2026, 10, 22, 1, 2, 3, TimeSpan.Zero));

        Assert.Contains("\"format\": \"oranda.connections\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("credentialRef", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lastConnectedAt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-ref", json, StringComparison.Ordinal);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(ConnectionExport.LooksLikeExport(json));

        var back = ConnectionExport.Read(json);
        Assert.Equal(2, back.Count);
        Assert.Equal(Orders with { CredentialRef = null, LastConnectedAt = null }, back[0]);
        Assert.Equal(Local, back[1]);
        Assert.Equal("조회 전용\n배치 때 쓰지 않음", back[0].Comment);
        Assert.Equal("주문/운영", back[0].Group);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"connections":[{"name":"x","type":"jdbc","info":{}}]}""")] // SQL Developer
    [InlineData("""{"schemaVersion":1,"profiles":[]}""")] // connections.json 그대로
    [InlineData("""{"format":"oranda.connections","schemaVersion":99,"profiles":[]}""")] // 더 새 판
    public void Foreign_or_newer_files_are_rejected(string json)
    {
        Assert.Throws<InvalidDataException>(() => ConnectionExport.Read(json));
    }

    [Fact]
    public void Sniffing_tells_oranda_files_from_other_json()
    {
        Assert.True(ConnectionExport.LooksLikeExport("""{ "format": "oranda.connections", "schemaVersion": 1, "profiles": [] }"""));
        Assert.False(ConnectionExport.LooksLikeExport("""{"connections":[]}"""));
        Assert.False(ConnectionExport.LooksLikeExport("[1,2]"));
        Assert.False(ConnectionExport.LooksLikeExport("{"));
    }

    [Fact]
    public void Missing_fields_read_as_empty_text_so_validation_can_flag_them()
    {
        var back = ConnectionExport.Read("""{"format":"oranda.connections","schemaVersion":1,"profiles":[{"name":"a"}]}""");
        var only = Assert.Single(back);
        Assert.Equal("a", only.Name);
        Assert.Equal(string.Empty, only.User);
        Assert.Equal(string.Empty, only.DataSource);
    }

    [Fact]
    public void Same_connection_means_same_name_user_and_connect_string_ignoring_case_and_spaces()
    {
        Assert.True(ConnectionExport.SameConnection(Local, Local with { Name = " 개발-로컬 ", User = "SCOTT", DataSource = "LOCALHOST:1521/freepdb1", Id = Guid.NewGuid() }));
        Assert.False(ConnectionExport.SameConnection(Local, Local with { User = "hr" }));
        Assert.False(ConnectionExport.SameConnection(Local, Local with { Name = "개발-로컬 2" }));
        Assert.False(ConnectionExport.SameConnection(Local, Local with { DataSource = "localhost:1522/FREEPDB1" }));
    }
}
