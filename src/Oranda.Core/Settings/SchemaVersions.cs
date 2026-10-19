using System.Text.Json.Nodes;

namespace Oranda.Core.Settings;

/// <summary>앱이 아는 JSON 파일 형식의 현재 <c>schemaVersion</c>(data-model 4.1).</summary>
public static class SchemaVersions
{
    public const int Settings = 1;
    public const int Connections = 1;
    public const int Formatter = 1;
    public const int Keymap = 1;
    public const int Layout = 1;
    public const int Workspace = 1;
}

/// <summary><see cref="ISettingsMigration.FileKind"/> 값.</summary>
public static class SettingsFileKinds
{
    public const string Settings = "settings";
    public const string Connections = "connections";
    public const string Formatter = "formatter";
    public const string Keymap = "keymap";
    public const string Layout = "layout";
    public const string Workspace = "workspace";
}

/// <summary>
/// JSON 파일 한 종류의 한 단계 상향 변환기(<see cref="FromVersion"/> → +1). 순수 함수: 파일·네트워크·키체인 접근 금지.
/// 자동 수집된다(구현 클래스에 <c>[OrandaService(typeof(ISettingsMigration))]</c>).
/// </summary>
public interface ISettingsMigration
{
    string FileKind { get; }

    int FromVersion { get; }

    JsonNode Apply(JsonNode document);
}
