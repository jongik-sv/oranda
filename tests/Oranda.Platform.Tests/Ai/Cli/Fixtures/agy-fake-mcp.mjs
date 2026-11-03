// 받은 토큰은 응답에 되돌리지 않고 SHA-256 만 돌려준다(응답은 모델 요청에 실리므로 토큰 원문이 나가면 안 된다).
// 시험용 가짜 MCP 서버(표준 입출력 JSON-RPC). agy 가 에이전트 파일의 orandactl 자리에서 띄우는 것처럼 동작한다. 도구는 echo_probe 하나.
import { createHash } from 'node:crypto';
import readline from 'node:readline';
const rl = readline.createInterface({ input: process.stdin });
const send = (o) => process.stdout.write(JSON.stringify(o) + '\n');
rl.on('line', (line) => {
  let m;
  try { m = JSON.parse(line); } catch { return; }
  if (m.method === 'initialize') {
    send({ jsonrpc: '2.0', id: m.id, result: { protocolVersion: m.params?.protocolVersion || '2025-03-26', capabilities: { tools: {} }, serverInfo: { name: 'fake', version: '1.0.0' } } });
  } else if (m.method === 'tools/list') {
    send({ jsonrpc: '2.0', id: m.id, result: { tools: [{ name: 'slow_probe', description: 'Waits the given number of seconds, then answers.', inputSchema: { type: 'object', properties: { seconds: { type: 'number' } }, required: ['seconds'] } }, { name: 'error_probe', description: 'Always answers with an error result.', inputSchema: { type: 'object', properties: {} } }, { name: 'echo_probe', description: 'Echo the given text back.', inputSchema: { type: 'object', properties: { text: { type: 'string' } }, required: ['text'] } }] } });
  } else if (m.method === 'tools/call' && m.params?.name === 'slow_probe') {
    const seconds = Number(m.params?.arguments?.seconds ?? 0);
    setTimeout(() => send({ jsonrpc: '2.0', id: m.id, result: { content: [{ type: 'text', text: 'slept:' + seconds }] } }), seconds * 1000);
  } else if (m.method === 'tools/call' && m.params?.name === 'error_probe') {
    send({ jsonrpc: '2.0', id: m.id, result: { isError: true, content: [{ type: 'text', text: '{"error":"busy","code":"busy"}' }] } });
  } else if (m.method === 'tools/call') {
    send({ jsonrpc: '2.0', id: m.id, result: { content: [{ type: 'text', text: 'echo:' + (m.params?.arguments?.text ?? '') + ';tokenSha256=' + (process.env.ORANDA_RUN_TOKEN ? createHash('sha256').update(process.env.ORANDA_RUN_TOKEN).digest('hex') : 'unset') }] } });
  } else if (m.id !== undefined) {
    send({ jsonrpc: '2.0', id: m.id, error: { code: -32601, message: 'not found' } });
  }
});
