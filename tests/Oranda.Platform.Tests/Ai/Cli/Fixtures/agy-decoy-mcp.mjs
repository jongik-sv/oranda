// 시험용 미끼 프로세스: 사용자 전역 설정(MCP 서버·훅·사이드카)에 있는 제3자 프로세스를 흉내 낸다. 시작할 때 기록 파일에 한 줄만 남긴다.
// 환경 변수 값은 기록하지 않고 실행 토큰 변수가 있는지만 적는다.
// 사용: node agy-decoy-mcp.mjs <기록 파일> <이름표> <mcp|once>   (mcp = MCP 서버로 계속 응답, once = 기록 뒤 바로 끝)
import fs from 'node:fs';
import readline from 'node:readline';
const [, , log, label, mode] = process.argv;
fs.appendFileSync(log, JSON.stringify({ label, started: true, hasTokenVariable: Object.keys(process.env).some((n) => n.includes('ORANDA_RUN_TOKEN')) }) + '\n');
if (mode !== 'mcp') {
  process.exit(0);
}
const rl = readline.createInterface({ input: process.stdin });
const send = (o) => process.stdout.write(JSON.stringify(o) + '\n');
rl.on('line', (line) => {
  let m;
  try { m = JSON.parse(line); } catch { return; }
  if (m.method === 'initialize') {
    send({ jsonrpc: '2.0', id: m.id, result: { protocolVersion: m.params?.protocolVersion || '2025-03-26', capabilities: { tools: {} }, serverInfo: { name: 'decoy', version: '1.0.0' } } });
  } else if (m.method === 'tools/list') {
    send({ jsonrpc: '2.0', id: m.id, result: { tools: [] } });
  } else if (m.id !== undefined) {
    send({ jsonrpc: '2.0', id: m.id, error: { code: -32601, message: 'not found' } });
  }
});
