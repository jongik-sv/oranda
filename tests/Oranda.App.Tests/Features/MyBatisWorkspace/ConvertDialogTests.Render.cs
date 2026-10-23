using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.MyBatisWorkspace.Conversion;
using Oranda.App.Tests.Features.MyBatisWorkspace.Flow;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

// ORANDA_RENDER_DIR 가 있을 때만 미리보기의 MyBatis 색을 라이트·다크 PNG 로 뽑는다(렌더 점검용, 저장소 밖 폴더).
public sealed partial class ConvertDialogTests
{
    [Fact]
    public Task Render_preview_with_mybatis_colors() => HeadlessHost.Run(() =>
    {
        const string Xml = "<select id=\"selectSql7\" resultType=\"map\">\n" +
            "  SELECT /* 사원 */ e.empno, nvl(e.comm, 0) AS comm, 'Y' AS flag\n" +
            "    FROM emp e\n" +
            "   <where>\n" +
            "     <if test=\"name != null\">AND e.ename LIKE #{name, jdbcType=VARCHAR} || '%'</if>\n" +
            "     AND e.sal > 1000 AND e.deptno = ${dept}\n" +
            "   </where>\n" +
            "</select>\n";
        var vm = Make(
            [Cand(0, "selectSql7"), Cand(1, "updateSal", ConvertStatementKind.Update)],
            convert: (_, kind, id, _, _, _) => new ConvertedStatement(id, kind, Xml, new Dictionary<string, MyBatisScenarioValue>(), []));
        var window = new ConvertDialog { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        MyBatisFlowRig.Render(window, "convert-preview", 1080, 740);
        window.Close();
    });
}
