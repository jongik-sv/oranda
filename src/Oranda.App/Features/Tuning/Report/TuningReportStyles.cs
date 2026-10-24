namespace Oranda.App.Features.Tuning.Report;

/// <summary>
/// 보고서 HTML 의 인라인 CSS(시안 tuning-report.html 그대로). 색 = 앱 테마 값(라이트·다크, prefers-color-scheme), 인쇄 = 흰 바탕·목차 숨김.
/// 글꼴은 이름만 적고 파일에 넣지 않는다(없으면 시스템 글꼴). 외부 자원은 없다.
/// </summary>
internal static class TuningReportStyles
{
    public const string Css = """
:root{
  --bg:#FFFFFF;--fg:#1B1B1B;--dim:rgba(0,0,0,.6);--chrome:#F3F3F3;--hdr:#EAEAEA;--sep:#D0D0D0;--code:#F7F7F8;
  --accent:#0A64C8;--warn:#B65A00;--danger:#C42B1C;--ok:#107C10;--rowalt:rgba(0,0,0,.031);--track:rgba(0,0,0,.08);
  --hot:rgba(232,113,10,.12);--i-blue:#1F6FD0;--i-teal:#0F7B8A;--i-orange:#E8710A;--i-amber:#D99A16;--i-purple:#8250DF;--i-green:#1E8E3E;--i-red:#D13438;--i-neutral:#5F6368;
  --changed:rgba(217,154,22,.2);
  --ui:"Inter","Segoe UI","Malgun Gothic","Apple SD Gothic Neo",sans-serif;--mono:"Cascadia Mono",Consolas,Menlo,monospace;
  color-scheme:light;
}
@media (prefers-color-scheme:dark){:root:not([data-theme="light"]){
  --bg:#1C1C1C;--fg:#F0F0F0;--dim:rgba(255,255,255,.62);--chrome:#2B2B2B;--hdr:#333333;--sep:#404040;--code:#161616;
  --accent:#6CB4FF;--warn:#F0A04B;--danger:#FF7B72;--ok:#6CCB5F;--rowalt:rgba(255,255,255,.043);--track:rgba(255,255,255,.12);
  --hot:rgba(255,162,92,.2);--i-blue:#6CB4FF;--i-teal:#4FC3C9;--i-orange:#FFA25C;--i-amber:#F2C14E;--i-purple:#B794F6;--i-green:#5CC27A;--i-red:#FF6B6B;--i-neutral:#B0B5BA;
  --changed:rgba(242,193,78,.2);color-scheme:dark;}}
:root[data-theme="dark"]{
  --bg:#1C1C1C;--fg:#F0F0F0;--dim:rgba(255,255,255,.62);--chrome:#2B2B2B;--hdr:#333333;--sep:#404040;--code:#161616;
  --accent:#6CB4FF;--warn:#F0A04B;--danger:#FF7B72;--ok:#6CCB5F;--rowalt:rgba(255,255,255,.043);--track:rgba(255,255,255,.12);
  --hot:rgba(255,162,92,.2);--i-blue:#6CB4FF;--i-teal:#4FC3C9;--i-orange:#FFA25C;--i-amber:#F2C14E;--i-purple:#B794F6;--i-green:#5CC27A;--i-red:#FF6B6B;--i-neutral:#B0B5BA;
  --changed:rgba(242,193,78,.2);color-scheme:dark;}
*{box-sizing:border-box}
html,body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.5 var(--ui)}
.wrap{max-width:1180px;margin:0 auto;padding:24px 20px 60px;display:grid;grid-template-columns:200px minmax(0,1fr);gap:28px}
@media (max-width:900px){.wrap{grid-template-columns:minmax(0,1fr);gap:12px}.toc{position:static!important}}
header.top{grid-column:1/-1;border-bottom:1px solid var(--sep);padding-bottom:14px}
.kicker{font-size:12px;color:var(--dim);letter-spacing:.02em}
h1{font-size:22px;margin:2px 0 8px;font-weight:600}
h1 code{font-family:var(--mono);font-size:20px;color:var(--accent)}
.meta{display:flex;flex-wrap:wrap;gap:6px 18px;font-size:12px;color:var(--dim)}
.meta b{color:var(--fg);font-weight:600}
.chip{display:inline-flex;align-items:center;gap:4px;border:1px solid var(--sep);background:var(--chrome);border-radius:10px;padding:0 8px;font-size:12px;line-height:20px;white-space:nowrap}
.chip.warn{color:var(--warn);border-color:currentColor;background:transparent}
.chip.danger{color:var(--danger);border-color:currentColor;background:transparent}
.chip.ok{color:var(--ok);border-color:currentColor;background:transparent}
.toc{position:sticky;top:16px;align-self:start;font-size:13px}
.toc .lbl{font-size:12px;font-weight:600;color:var(--dim);margin-bottom:6px}
.toc ol{margin:0;padding:0;list-style:none;counter-reset:t}
.toc li{counter-increment:t}
.toc a{display:flex;gap:8px;padding:3px 8px;border-radius:4px;color:var(--fg);text-decoration:none}
.toc a::before{content:counter(t);color:var(--dim);font-family:var(--mono);font-size:12px;min-width:16px}
.toc a:hover{background:var(--chrome)}
.toc .off a{color:var(--dim)}
main{min-width:0}
details.sec{border:1px solid var(--sep);border-radius:6px;margin-bottom:14px;background:var(--bg)}
details.sec>summary{list-style:none;cursor:pointer;display:flex;align-items:center;gap:10px;padding:9px 14px;background:var(--chrome);border-radius:6px}
details.sec[open]>summary{border-bottom:1px solid var(--sep);border-radius:6px 6px 0 0}
details.sec>summary::-webkit-details-marker{display:none}
details.sec>summary::before{content:"";width:0;height:0;border-left:5px solid var(--dim);border-top:4px solid transparent;border-bottom:4px solid transparent;transition:transform .12s}
details.sec[open]>summary::before{transform:rotate(90deg)}
summary h2{font-size:15px;margin:0;font-weight:600}
summary .num{font-family:var(--mono);color:var(--dim);font-size:12px}
summary .right{margin-left:auto;display:flex;gap:6px;flex-wrap:wrap;justify-content:flex-end}
.body{padding:12px 14px}
.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:10px}
.card{border:1px solid var(--sep);border-left:4px solid var(--i-neutral);border-radius:5px;padding:9px 12px}
.card.danger{border-left-color:var(--danger)}.card.warn{border-left-color:var(--i-amber)}.card.info{border-left-color:var(--i-blue)}
.card .t{font-weight:600;font-size:13px;display:flex;gap:6px;align-items:center}
.card .t .sev{font-size:11px;font-weight:600;padding:0 6px;border-radius:8px;border:1px solid currentColor}
.card.danger .sev{color:var(--danger)}.card.warn .sev{color:var(--warn)}.card.info .sev{color:var(--i-blue)}
.card p{margin:4px 0 0;font-size:13px;color:var(--dim)}
.card p code{color:var(--fg)}
.kpis{display:grid;grid-template-columns:repeat(auto-fit,minmax(130px,1fr));gap:8px;margin-bottom:12px}
.kpi{background:var(--chrome);border-radius:5px;padding:8px 10px}
.kpi .k{font-size:12px;color:var(--dim)}
.kpi .v{font-family:var(--mono);font-size:18px;font-weight:600}
.kpi .v small{font-size:12px;font-weight:400;color:var(--dim)}
code,.mono{font-family:var(--mono);font-size:13px}
pre.code{background:var(--code);border:1px solid var(--sep);border-radius:4px;padding:10px 12px;margin:0;overflow:auto;font:13px/1.55 var(--mono);white-space:pre-wrap;word-break:break-word}
.kw{color:var(--i-blue);font-weight:600}.str{color:var(--i-green)}.bind{color:var(--i-purple)}.cm{color:var(--dim);font-style:italic}.num-t{color:var(--i-teal)}.hint{color:var(--i-orange);font-style:italic}
.tw{overflow-x:auto;border:1px solid var(--sep);border-radius:4px}
table{border-collapse:collapse;width:100%;font-size:13px}
th{background:var(--hdr);font-size:12px;font-weight:600;text-align:left;padding:4px 8px;white-space:nowrap;border-bottom:1px solid var(--sep)}
td{padding:3px 8px;white-space:nowrap;border-bottom:1px solid var(--sep);vertical-align:middle}
td.wrap{white-space:normal;min-width:220px}
tbody tr:nth-child(2n) td{background:var(--rowalt)}
tr.hot td{background:var(--hot)!important}
tr.hot td:first-child{box-shadow:inset 3px 0 0 var(--i-orange)}
tr.chg td{background:var(--changed)!important}
td.n,th.n{text-align:right;font-family:var(--mono)}
th.n{font-family:var(--ui)}
td.op{font-family:var(--mono)}
.ind{display:inline-block;width:14px}
.bar{display:inline-block;width:70px;height:6px;border-radius:3px;background:var(--track);position:relative;vertical-align:middle;margin-right:6px}
.bar i{position:absolute;left:0;top:0;bottom:0;border-radius:3px;background:var(--i-teal)}
.bar i.t{opacity:.35}
tr.hot .bar i:not(.t){background:var(--i-orange)}
.pct{font-size:12px;color:var(--dim);font-family:var(--mono)}
.warn-t{color:var(--warn)}.danger-t{color:var(--danger)}.ok-t{color:var(--ok)}.dim{color:var(--dim)}
.note{font-size:12px;color:var(--dim);margin:8px 0 0}
.pred{font-size:12px;color:var(--dim);margin-top:8px}
.pred div{font-family:var(--mono);font-size:12px;padding:1px 0;white-space:pre-wrap;word-break:break-word}
.grid2{display:grid;grid-template-columns:repeat(auto-fit,minmax(320px,1fr));gap:14px}
h3{font-size:13px;margin:14px 0 6px;font-weight:600}
h3:first-child{margin-top:0}
.hist{display:grid;grid-template-columns:auto 1fr auto;gap:3px 8px;align-items:center;font-size:12px}
.hist .b{height:12px;background:var(--i-blue);border-radius:2px;opacity:.85}
.stack{display:flex;height:14px;border-radius:3px;overflow:hidden;margin:4px 0 10px}
.stack span{display:block}
.legend{display:flex;flex-wrap:wrap;gap:4px 14px;font-size:12px;color:var(--dim)}
.legend i{display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:4px;vertical-align:-1px}
.excluded{display:flex;gap:10px;align-items:flex-start;padding:10px 12px;border:1px dashed var(--sep);border-radius:5px;color:var(--dim);font-size:13px}
.excluded.failed b{color:var(--danger)}
.excluded+.excluded,.excluded+h3{margin-top:10px}
.svgbox{border:1px solid var(--sep);border-radius:4px;background:var(--code);overflow:auto}
svg text{font-family:var(--ui)}
.n-box{fill:var(--bg);stroke:var(--sep)}
.n-edge{stroke:var(--i-neutral);stroke-width:1.2;fill:none;opacity:.6}
.n-t{fill:var(--fg);font-size:11px}.n-s{fill:var(--dim);font-size:10px}
.n-hot{fill:var(--i-orange);fill-opacity:.85;stroke:var(--i-orange);stroke-width:2}
.n-heat{fill:var(--i-teal);stroke:var(--i-teal)}
.n-ht{fill:#fff;font-size:11px;font-weight:600}.n-hs{fill:#fff;font-size:10px}
footer{grid-column:1/-1;border-top:1px solid var(--sep);padding-top:10px;font-size:12px;color:var(--dim);display:flex;flex-wrap:wrap;gap:6px 16px}
@media print{
  :root{--bg:#fff;--fg:#000;--dim:#555;--chrome:#f2f2f2;--hdr:#e6e6e6;--sep:#bbb;--code:#fafafa;color-scheme:light}
  html,body{font-size:11pt}
  .wrap{display:block;max-width:none;padding:0}
  .toc{display:none}
  details.sec{break-inside:auto;border:none;margin:0 0 12pt}
  details.sec>summary{background:none;border-bottom:2px solid #000!important;padding:0 0 3pt;border-radius:0}
  details.sec>summary::before{display:none}
  tr,.card,.kpi{break-inside:avoid}
  .tw,.svgbox{overflow:visible;border:none}
  a{color:inherit;text-decoration:none}
  @page{margin:14mm 12mm;}
}
""";
}
