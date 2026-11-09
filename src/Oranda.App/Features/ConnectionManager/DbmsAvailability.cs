using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Sessions;

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

    /// <summary>이 설치본에 공급자 어셈블리가 있는 DBMS(처음 한 번 계산, 구현 유무는 안 봄). 화면은 <see cref="Detect(string, IDbmsServiceSelector?)"/> 로 거른 목록을 쓴다.</summary>
    public static IReadOnlyList<Dbms> Current => Detected.Value;

    /// <summary>앱 폴더에 있는 공급자 어셈블리로 설치된 DBMS를 정한다(구현이 갖춰졌는지는 보지 않는다). Oracle 이 항상 맨 앞.</summary>
    public static IReadOnlyList<Dbms> Detect(string appDirectory)
    {
        List<Dbms> list = [Dbms.Oracle];
        if (!string.IsNullOrEmpty(appDirectory) && File.Exists(Path.Combine(appDirectory, TiberoAssembly + ".dll")))
        {
            list.Add(Dbms.Tibero);
        }

        return list;
    }

    /// <summary>
    /// 접속 대화 상자에서 고를 수 있는 DBMS: 공급자 어셈블리가 있고(<see cref="Detect(string)"/>) <b>선택기에 그 DBMS 의 <see cref="ISessionManager"/> 구현이 있을 때만</b>.
    /// 어셈블리만 있고 세션 관리자가 아직 없으면 보이지 않는다(연결해 봐야 「구현이 없다」만 뜬다). 세션 관리자가 들어오면 자동으로 보인다.
    /// 선택기가 없으면 Oracle 뿐.
    /// <para>
    /// 드라이버 지정(결정25) 이후: 세션 관리자는 Tibero 드라이버(dll) 유무와 상관없이 <c>Oranda.Tibero</c> 가 항상 등록하므로
    /// 이 규칙은 곧 「Oranda.Tibero 어셈블리가 있으면 Tibero 는 항상 보인다」 가 된다. 드라이버가 없을 때의 안내·접속 막기는 이 목록이 아니라
    /// 편집 영역의 드라이버 띠(<see cref="DriverBandRules"/>)가 맡는다.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Dbms> Detect(string appDirectory, IDbmsServiceSelector? selector) =>
        [.. Detect(appDirectory).Where(d => d == Dbms.Oracle || selector?.Find<ISessionManager>(d) is not null)];

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
