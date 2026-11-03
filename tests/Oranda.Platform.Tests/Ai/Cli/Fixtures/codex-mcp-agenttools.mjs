// 시험용 MCP 서버(orandactl 자리). 실제 AgentTools 의 도구 이름·스키마를 tools.json 에서 읽어 내고, 호출을 로그에 한 줄씩 적는다.
// 사용: node codex-mcp-agenttools.mjs <log> <tools.json> [무시되는 인자...]
import fs from 'node:fs'; import readline from 'node:readline';
const log = process.argv[2]; const tools = JSON.parse(fs.readFileSync(process.argv[3], 'utf8'));
const rec = o => fs.appendFileSync(log, JSON.stringify(o) + '\n');
rec({ ev: 'start', tokenEnv: process.env.ORANDA_RUN_TOKEN ? 'set' : 'unset' });
const send = o => process.stdout.write(JSON.stringify(o) + '\n');
const rl = readline.createInterface({ input: process.stdin });
rl.on('line', l => {
  let m; try { m = JSON.parse(l); } catch { return; }
  if (m.method === 'initialize') send({ jsonrpc: '2.0', id: m.id, result: { protocolVersion: m.params?.protocolVersion || '2025-03-26', capabilities: { tools: {} }, serverInfo: { name: 'orandactl-test', version: '1.0.0' } } });
  else if (m.method === 'tools/list') send({ jsonrpc: '2.0', id: m.id, result: { tools: tools.map(t => ({ name: t.name, description: t.description, inputSchema: t.inputSchema })) } });
  else if (m.method === 'tools/call') {
    const known = tools.some(t => t.name === m.params?.name);
    rec({ ev: 'call', name: m.params?.name, ok: known });
    send({ jsonrpc: '2.0', id: m.id, result: known
      ? { content: [{ type: 'text', text: JSON.stringify({ object: 'EMP', num_rows: 1234, columns: ['EMPNO', 'ENAME', 'DEPTNO'], note: 'canned test data' }) }] }
      : { isError: true, content: [{ type: 'text', text: 'unknown tool' }] } });
  } else if (m.id !== undefined) send({ jsonrpc: '2.0', id: m.id, error: { code: -32601, message: 'not found' } });
});
