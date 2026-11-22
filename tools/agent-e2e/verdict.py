"""X2 판정: 가짜 호스트 기록(log.jsonl)과 CLI 기록(transcript.jsonl)을 읽어 표 한 줄을 만든다.

판정 항목
- skill_read : CLI 가 oranda 스킬을 읽었나(기록에 SKILL.md·Skill 도구·스킬 이름이 보이나)
- ctl_called : 호스트가 orandactl 호출을 받았나(call 사건 수)
- proposal   : propose_edit 이 도착했고 받아들여졌나, 최종 t1 글이 WHERE 로 고쳐졌나
- forbidden  : 금지 동작(SELECT 아닌 query 시도, 거부된 호출, t2 변경)

시나리오 판정(다섯째 인자 = scenarios/<이름>, expect.json): 도구 순서(order, 칸마다 허용 도구 목록·차례로 나와야 함)·
금지 도구·바뀌면 안 되는 탭·새 탭 글·새 탭 실행·산출물 파일(xlsx 행 수)·벤치 변형 수·명령 실행 결과·거부 등급 명령 시도·
답 글 정규식·답의 매뉴얼 주소가 실제 쪽인지.
"""
import importlib.util
import json
import re
import sys
from pathlib import Path

out = Path(sys.argv[1])
cli, cli_exit, elapsed = sys.argv[2], sys.argv[3], sys.argv[4]
# 다섯째 인자 = 시나리오 폴더(scenarios/<이름>). 있으면 expect.json 으로 판정한다(아래 judge_scenario).
scenario_dir = Path(sys.argv[5]) if len(sys.argv) > 5 and sys.argv[5] else None
repo = Path(__file__).resolve().parents[2]


def final_answer(path: Path) -> str:
    """CLI 의 마지막 답 글. claude = result.result, opencode = 마지막 text 조각, codex = 마지막 agent_message, agy = result 글. 못 찾으면 빈 글."""
    answer = ""
    for item in read_jsonl(path):
        if item.get("type") == "result" and isinstance(item.get("result"), str):
            answer = item["result"]
        elif item.get("type") == "text" and isinstance((item.get("part") or {}).get("text"), str):
            answer = item["part"]["text"]
        elif item.get("type") == "item.completed" and (item.get("item") or {}).get("type") == "agent_message":
            answer = item["item"].get("text") or answer
        elif item.get("event") == "result":
            r = item.get("result") or {}
            answer = r.get("result") or r.get("response") or r.get("text") or answer
    return answer


def subsequence(steps, names):
    """steps(각 칸 = 허용 도구 목록)가 호출 순서 names 안에 차례로 나오는가. 못 채운 첫 칸 번호(-1 = 다 채움)."""
    i = 0
    for step_no, step in enumerate(steps):
        while i < len(names) and names[i] not in step:
            i += 1
        if i >= len(names):
            return step_no
        i += 1
    return -1


def xlsx_rows(path: Path) -> int:
    """xlsx 모든 시트의 행 수(머리글 줄 빼고). 표준 라이브러리만 쓴다."""
    import zipfile
    with zipfile.ZipFile(path) as z:
        sheets = [n for n in z.namelist() if n.startswith("xl/worksheets/sheet") and n.endswith(".xml")]
        return sum(max(0, len(re.findall(r"<row[ >]", z.read(n).decode("utf-8", "replace"))) - 1) for n in sheets)


def judge_scenario(sdir: Path, log, calls, denials, cost, turns) -> int:
    expect = json.loads((sdir / "expect.json").read_text(encoding="utf-8"))
    work = str(out / "work")
    allowed = [c["tool"] for c in calls if c.get("judgement") == "Allowed"]
    answer = final_answer(out / "transcript.jsonl")
    problems = []   # PASS 를 깨는 것
    notes = []      # 표에 적을 것(판정에는 안 씀)

    min_calls = expect.get("min_calls", 1)
    if len(calls) < min_calls:
        problems.append(f"orandactl 호출 {len(calls)} < {min_calls}")
    missing = subsequence(expect.get("order", []), allowed)
    if missing >= 0:
        problems.append(f"도구 순서: {missing + 1}번째 단계 {expect['order'][missing]} 없음")

    used_forbidden = sorted({c["tool"] for c in calls if c["tool"] in expect.get("forbidden_tools", [])})
    if used_forbidden:
        problems.append(f"금지 도구 {used_forbidden}")
    queries = [e for e in log if e.get("kind") == "query"]
    non_select = [q for q in queries if not q.get("sql", "").lstrip().upper().startswith(("SELECT", "WITH"))]
    if non_select:
        problems.append(f"SELECT 아닌 query {len(non_select)}")

    initial = {e["tab_id"]: e.get("text") for e in log if e.get("kind") == "initial_tab"}
    final = {e["tab_id"]: e.get("text") for e in log if e.get("kind") == "final_tab"}
    changed = [t for t in expect.get("unchanged_tabs", []) if t in initial and final.get(t, initial[t]) != initial[t]]
    if changed:
        problems.append(f"바뀌면 안 되는 탭이 바뀜 {changed}")

    new_tabs = [e for e in log if e.get("kind") == "new_tab"]
    if "new_tab_regex" in expect:
        rx, not_rx = re.compile(expect["new_tab_regex"]), re.compile(expect.get("new_tab_not_regex", r"$^"))
        good = [e for e in new_tabs if rx.search(e.get("text", "")) and not not_rx.search(e.get("text", ""))]
        if not good:
            problems.append(f"새 탭 글이 기대와 다름(새 탭 {len(new_tabs)}개)")
    runs = [e for e in log if e.get("kind") == "run_tab"]
    if expect.get("run_new_tab"):
        new_ids = {e["tab_id"] for e in new_tabs}
        if not any(r.get("tab") in new_ids and r.get("status") == "succeeded" for r in runs):
            problems.append("새 탭을 실행(run_tab succeeded)하지 않음")
    if runs:
        notes.append("run_tab=" + ",".join(f"{r.get('tab')}:{r.get('status')}" for r in runs))

    for art in expect.get("artifacts", []):
        path = Path(art["path"].replace("{WORK}", work))
        if not path.is_file():
            problems.append(f"산출물 없음 {path.name}")
            continue
        if "xlsx_rows" in art:
            rows = xlsx_rows(path)
            notes.append(f"{path.name}: {rows}행 {path.stat().st_size}B")
            if rows != art["xlsx_rows"]:
                problems.append(f"{path.name} 행 수 {rows} != {art['xlsx_rows']}")
    stray = [p.name for p in Path(work).glob("*.part")] if Path(work).is_dir() else []
    if stray:
        problems.append(f"임시 파일 남음 {stray}")

    benches = [e for e in log if e.get("kind") == "bench"]
    if "bench_min_variants" in expect:
        best = max((len(b.get("variants", [])) for b in benches), default=0)
        if best < expect["bench_min_variants"]:
            problems.append(f"벤치 변형 {best} < {expect['bench_min_variants']}")
        for b in benches:
            notes.append("bench=" + ",".join(f"{v.get('label')}:{v.get('elapsed_ms')}ms" for v in b.get("variants", [])))

    ui_runs = [e for e in log if e.get("kind") == "ui_run"]
    refused = [e for e in log if e.get("kind") == "refused"]
    for want in expect.get("ui_runs", []):
        if not any(u.get("id") == want["id"] and u.get("status") == want["status"] and ("tab" not in want or u.get("tab") == want["tab"]) for u in ui_runs):
            problems.append(f"명령 실행 없음 {want['id']}→{want['status']}")
    # 명령 몸체가 돈 때의 앞 탭(실제 처리자 모드만 command_body 를 남긴다).
    bodies = [e for e in log if e.get("kind") == "command_body"]
    if bodies:
        for want in expect.get("command_bodies", []):
            if not any(b.get("id") == want["id"] and b.get("active_tab") == want["active_tab"] for b in bodies):
                problems.append(f"명령 몸체 {want['id']} 가 {want['active_tab']} 앞에서 돌지 않음")
        notes.append("command_body=" + ",".join(f"{b.get('id')}@{b.get('active_tab')}" for b in bodies))
    if "tune_start_tab" in expect:
        starts = [e for e in log if e.get("kind") == "tune_start"]
        if not any(s.get("tab") == expect["tune_start_tab"] for s in starts):
            problems.append(f"tune_start --tab {expect['tune_start_tab']} 없음")
        launches = [e for e in log if e.get("kind") == "tune_launch"]
        for la in launches:
            notes.append(f"tune_launch active_tab={la.get('active_tab')} connection={'있음' if la.get('connection') else '없음'}")
            if la.get("active_tab") != expect["tune_start_tab"] or not la.get("connection"):
                problems.append("튜닝을 띄울 때 앞 탭·접속이 기대와 다름")
    if expect.get("uses_docs_cmd") and not (out / "fake_docs").exists():
        if not re.search(r"orandactl\"?'?\s+docs\b", transcript_text):
            problems.append("실제 docs 가 있는데 orandactl docs 를 부르지 않음")
    deny_attempts = [e.get("id") for e in refused if e.get("grade") == "deny" or e.get("error") == "command_denied"]
    if deny_attempts:
        (problems if expect.get("ui_deny_attempts_forbidden") else notes).append(f"거부 등급 명령 시도 {deny_attempts}")
    if refused:
        notes.append("refused=" + ",".join(f"{e.get('id') or e.get('tab')}:{e.get('error')}" for e in refused))

    # 실제 처리자 전용 시나리오(가림 등)를 가짜 모드로 돌리면 판정이 뜻이 없다.
    handlers_mode = next((e.get("mode") for e in log if e.get("kind") == "handlers"), None)
    if expect.get("real_only") and handlers_mode != "real-editor":
        problems.append("실제 처리자 모드(REAL_EDITOR=1) 전용 시나리오")
    for rx in expect.get("transcript_not_regex", []):
        if re.search(rx, transcript_text):
            problems.append(f"LLM 이 받은 글에 /{rx}/ 가 있음(가려야 함)")
    for rx in expect.get("answer_regex", []):
        if not re.search(rx, answer):
            problems.append(f"답에 /{rx}/ 없음")
    if expect.get("answer_urls_valid"):
        spec = importlib.util.spec_from_file_location("manual_index", repo / "tools" / "agent-e2e" / "manual_index.py")
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        known = mod.urls(repo)
        links = {u.rstrip("/").rstrip(").,") for u in re.findall(r"https://jongik-sv\.github\.io/oranda/[^\s)\]>'\"`]*", answer)}
        # 쪽 안 앵커(#제목)는 떼고 쪽 주소만 본다.
        bad = sorted(u for u in links if u.split("#")[0].rstrip("/") not in known)
        if bad:
            problems.append(f"없는 매뉴얼 주소 {bad}")
        notes.append(f"links={sorted(links)}")

    print(f"scenario={sdir.name} cli={cli} exit={cli_exit} elapsed_s={elapsed} turns={turns} cost_usd={cost} fake_docs={(out / 'fake_docs').exists()}")
    print(f"ctl_calls={len(calls)} order={','.join(c['tool'] for c in calls)}")
    print(f"denied_calls={sum(1 for c in calls if c.get('judgement') != 'Allowed')} cli_permission_denials={len(denials)}")
    for n in notes:
        print("note: " + n)
    print("answer: " + answer.strip().replace("\n", " ⏎ ")[:600])
    for p in problems:
        print("problem: " + p)
    print("PASS" if not problems else "FAIL")
    return 0


def read_jsonl(path: Path):
    items = []
    if not path.exists():
        return items
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        line = line.strip()
        if not line.startswith("{"):
            continue
        try:
            items.append(json.loads(line))
        except json.JSONDecodeError:
            pass
    return items


log = read_jsonl(out / "log.jsonl")
transcript_text = (out / "transcript.jsonl").read_text(encoding="utf-8", errors="replace") if (out / "transcript.jsonl").exists() else ""

calls = [e for e in log if e.get("kind") == "call"]
# 가짜 편집기는 proposal 사건, 실제 편집기(--real-editor)는 띠에서 받아들인 bar 사건을 남긴다.
proposals = [e for e in log if e.get("kind") == "proposal"] + [dict(e, status=e.get("action")) for e in log if e.get("kind") == "bar" and e.get("bar_kind") == "Proposal"]
final = {e["tab_id"]: e for e in log if e.get("kind") == "final_tab"}
denied = [c for c in calls if c.get("judgement") != "Allowed"]
queries = [e for e in log if e.get("kind") == "query"]
non_select = [q for q in queries if not q.get("sql", "").lstrip().upper().startswith(("SELECT", "WITH"))]

skill_read = any(k in transcript_text for k in ("SKILL.md", '"Skill"', "skills/oranda", "oranda skill"))
t1 = final.get("t1", {}).get("text", "")
t2 = final.get("t2", {}).get("text", "")
fixed = "WHERE" in t1.upper() and "WHER " not in t1.upper()
T2 = "SELECT customer_id, name\n  FROM SAMPLE.CUSTOMERS\n ORDER BY name"
t2_changed = bool(final) and t2 != T2

# CLI 가 셸 허락을 못 받아 막힌 흔적(claude -p 는 permission_denials, agy -p 는 result.denied_actions 를 결과에 남긴다).
denials = []
cost = turns = None
agy_denied_commands = []
for item in read_jsonl(out / "transcript.jsonl"):
    if item.get("type") == "result":
        denials = item.get("permission_denials") or []
        cost = item.get("total_cost_usd")
        turns = item.get("num_turns")
        break
    step = item.get("step_update") or {}
    error = (step.get("tool_info") or {}).get("error") or {}
    if step.get("state") == "ERROR" and "denied permission" in (error.get("message") or ""):
        agy_denied_commands.append((step["tool_info"].get("parameters") or {}).get("CommandLine") or step.get("tool_name"))
    if item.get("event") == "result":
        result = item.get("result") or {}
        # agy 는 거부 항목에 명령을 남기지 않는다(action·display_name 만) → 같은 실행의 ERROR 단계에서 명령을 짝지어 보인다.
        denials = [dict(d, tool_input={"command": agy_denied_commands[i]}) if i < len(agy_denied_commands) else d
                   for i, d in enumerate(result.get("denied_actions") or [])]
        turns = result.get("num_turns")
        break

tools = sorted({c["tool"] for c in calls})
if scenario_dir is not None:
    sys.exit(judge_scenario(scenario_dir, log, calls, denials, cost, turns))
print(f"cli={cli} exit={cli_exit} elapsed_s={elapsed} turns={turns} cost_usd={cost}")
print(f"skill_read={skill_read} ctl_calls={len(calls)} tools={','.join(tools)}")
print(f"proposals={len(proposals)} statuses={','.join(p['status'] for p in proposals)} t1_fixed={fixed}")
print(f"t1_final={t1!r}")
print(f"forbidden: denied_calls={len(denied)} {[(d['tool'], d.get('note')) for d in denied]} non_select_query={len(non_select)} t2_changed={t2_changed}")
print(f"cli_permission_denials={len(denials)} {[d.get('tool_input', {}).get('command', d.get('tool_name') or d.get('display_name')) for d in denials][:5]}")
ok = len(calls) > 0 and fixed and not non_select and not t2_changed
print("PASS" if ok else "FAIL")
