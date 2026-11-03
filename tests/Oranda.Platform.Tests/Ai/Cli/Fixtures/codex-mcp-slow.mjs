// 시험용 느린 MCP 서버(orandactl 자리): tools/call 에 <지연 초> 뒤에 답한다. 사용: node codex-mcp-slow.mjs <log> <지연 초> [무시되는 인자...]
import fs from 'node:fs'; import readline from 'node:readline';
const log=process.argv[2]; const delay=+process.argv[3]; const t0=Date.now();
const rec=o=>fs.appendFileSync(log,JSON.stringify({t:Math.round((Date.now()-t0)/1000),...o})+'\n');
rec({ev:'start'});
const send=o=>process.stdout.write(JSON.stringify(o)+'\n');
readline.createInterface({input:process.stdin}).on('line',l=>{let m;try{m=JSON.parse(l)}catch{return}
 if(m.method==='initialize')send({jsonrpc:'2.0',id:m.id,result:{protocolVersion:m.params?.protocolVersion||'2025-03-26',capabilities:{tools:{}},serverInfo:{name:'slow',version:'1'}}});
 else if(m.method==='tools/list')send({jsonrpc:'2.0',id:m.id,result:{tools:[{name:'slow_tool',description:'slow',inputSchema:{type:'object',properties:{}}}]}});
 else if(m.method==='tools/call'){rec({ev:'call-received'});setTimeout(()=>{rec({ev:'call-answering'});send({jsonrpc:'2.0',id:m.id,result:{content:[{type:'text',text:'done after '+delay}]}})},delay*1000);}
 else if(m.method==='notifications/cancelled'||m.method==='$/cancelRequest'){rec({ev:'cancel-notification',m:m.method});}
 else if(m.id!==undefined)send({jsonrpc:'2.0',id:m.id,error:{code:-32601,message:'nf'}});
});
