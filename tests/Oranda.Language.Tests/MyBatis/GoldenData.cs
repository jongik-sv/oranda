using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Oranda.Language.Tests.MyBatis;

/// <summary>Golden 폴더의 MyBatis 3.5.19 실측 JSON 을 읽는 도우미. 경로는 이 파일 위치 기준이라 csproj 를 건드리지 않는다.</summary>
internal static class GoldenData
{
    /// <summary>Golden 폴더의 파일 경로.</summary>
    public static string Path(string fileName, [CallerFilePath] string here = "") =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(here)!, "Golden", fileName);

    /// <summary>Golden 폴더의 JSON 파일을 읽는다.</summary>
    public static JsonDocument Load(string fileName, [CallerFilePath] string here = "") =>
        JsonDocument.Parse(File.ReadAllText(Path(fileName, here)));

    /// <summary><c>{"t": 형, "v": 값}</c> 을 C# 값으로 바꾼다. 형 = null·string·int·long·short·byte·decimal(글자)·double·float·bool·char·list·map.</summary>
    public static object? Decode(JsonElement typed)
    {
        var type = typed.GetProperty("t").GetString();
        if (type == "null")
        {
            return null;
        }

        var v = typed.GetProperty("v");
        switch (type)
        {
            case "string":
                return v.GetString();
            case "int":
                return v.GetInt32();
            case "long":
                return v.GetInt64();
            case "decimal":
                return decimal.Parse(v.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);
            case "double":
                return v.GetDouble();
            case "float":
                return v.GetSingle();
            case "short":
                return v.GetInt16();
            case "byte":
                return v.GetSByte();
            case "bool":
                return v.GetBoolean();
            case "char":
                return v.GetString()![0];
            case "list":
                var list = new List<object?>();
                foreach (var item in v.EnumerateArray())
                {
                    list.Add(Decode(item));
                }

                return list;
            case "map":
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var p in v.EnumerateObject())
                {
                    map[p.Name] = Decode(p.Value);
                }

                return map;
            default:
                throw new InvalidDataException($"알 수 없는 골든 값 형: {type}");
        }
    }
}
