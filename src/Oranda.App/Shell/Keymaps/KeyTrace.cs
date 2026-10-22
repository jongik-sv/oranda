using System;
using System.Globalization;
using Avalonia.Input;

namespace Oranda.App.Shell.Keymaps;

/// <summary>
/// 키 입력 진단 기록. 환경 변수 <c>ORANDA_KEYTRACE=1</c> 일 때만 stderr 에 <c>[keytrace]</c> 한 줄씩 쓴다(기본 꺼짐).
/// 입력기·플랫폼이 KeyDown·TextInput 을 어떤 값과 순서로 보내는지, 키맵 디스패처가 무엇과 맞췄는지 확인하는 용도다.
/// </summary>
public static class KeyTrace
{
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("ORANDA_KEYTRACE") == "1";

    public static void KeyDown(string stage, KeyEventArgs e, object? focused)
    {
        if (!Enabled)
        {
            return;
        }

        Write(string.Create(CultureInfo.InvariantCulture,
            $"KeyDown {stage} Key={e.Key} Physical={e.PhysicalKey} Mods={e.KeyModifiers} Symbol={Quote(e.KeySymbol)} Handled={e.Handled} Source={Name(e.Source)} Focus={Name(focused)}"));
    }

    public static void TextInput(string stage, TextInputEventArgs e, object? focused)
    {
        if (!Enabled)
        {
            return;
        }

        Write(string.Create(CultureInfo.InvariantCulture,
            $"TextInput {stage} Text={Quote(e.Text)} Handled={e.Handled} Source={Name(e.Source)} Focus={Name(focused)}"));
    }

    public static void Dispatch(Key key, KeyModifiers modifiers, int pending, KeyMatch match, bool editorFocus)
    {
        if (!Enabled)
        {
            return;
        }

        Write(string.Create(CultureInfo.InvariantCulture,
            $"Dispatch Typed={Describe(modifiers, key)} Pending={pending} Match={match.Kind} Command={match.CommandId ?? "-"} editorFocus={editorFocus}"));
    }

    public static void Ignored(Key key, KeyModifiers modifiers)
    {
        if (Enabled)
        {
            Write(string.Create(CultureInfo.InvariantCulture, $"Dispatch Ignored Typed={Describe(modifiers, key)} (수식키만 또는 None)"));
        }
    }

    private static string Describe(KeyModifiers modifiers, Key key) => modifiers == KeyModifiers.None ? key.ToString() : $"{modifiers}+{key}";

    private static string Quote(string? text) => text is null ? "null" : "\"" + text.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string Name(object? o) => o?.GetType().Name ?? "null";

    private static void Write(string line) => Console.Error.WriteLine("[keytrace] " + line);
}
