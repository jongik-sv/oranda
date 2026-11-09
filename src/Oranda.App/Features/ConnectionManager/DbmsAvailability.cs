using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Oranda.Core.Connection;

namespace Oranda.App.Features.ConnectionManager;

/// <summary>
/// 접속 대화 상자에서 고를 수 있는 DBMS(D-046, docs/design/tibero.md 1장). Oracle 은 항상, Tibero 는 <c>Oranda.Tibero</c> 어셈블리가
/// 앱 폴더에 있을 때만 보인다(없으면 목록에 안 나오고 Oracle 동작은 그대로). 어셈블리를 읽어 올리지는 않고 파일 유무만 본다.
/// </summary>
public static class DbmsAvailability
{
    /// <summary>Tibero 공급자 어셈블리 이름.</summary>
    public const string TiberoAssembly = "Oranda.Tibero";

    private static readonly Lazy<IReadOnlyList<Dbms>> Detected = new(() => Detect(AppContext.BaseDirectory));

    /// <summary>이 설치본에서 고를 수 있는 DBMS(처음 한 번 계산).</summary>
    public static IReadOnlyList<Dbms> Current => Detected.Value;

    /// <summary>앱 폴더에 있는 공급자 어셈블리로 고를 수 있는 DBMS를 정한다. Oracle 이 항상 맨 앞.</summary>
    public static IReadOnlyList<Dbms> Detect(string appDirectory)
    {
        List<Dbms> list = [Dbms.Oracle];
        if (!string.IsNullOrEmpty(appDirectory) && File.Exists(Path.Combine(appDirectory, TiberoAssembly + ".dll")))
        {
            list.Add(Dbms.Tibero);
        }

        return list;
    }

    /// <summary>목록에 <paramref name="dbms"/> 가 없으면 덧붙인다(이미 저장된 프로필을 보여 줄 수 있게).</summary>
    public static IReadOnlyList<Dbms> With(IReadOnlyList<Dbms> available, Dbms dbms) =>
        available.Contains(dbms) ? available : [.. available, dbms];

    /// <summary>화면에 보일 DBMS 이름.</summary>
    public static string Label(Dbms dbms) => dbms switch
    {
        Dbms.Oracle => "Oracle",
        Dbms.Tibero => "Tibero",
        _ => dbms.ToString(),
    };

    /// <summary>DBMS 별 기본 포트(글자).</summary>
    public static string DefaultPort(Dbms dbms) => dbms == Dbms.Tibero ? "8629" : "1521";
}
