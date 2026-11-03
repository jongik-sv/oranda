import fs from 'node:fs'; import readline from 'node:readline';
const log=process.argv[2]; const rec=o=>fs.appendFileSync(log,JSON.stringify(o)+'\n');
rec({ev:'start',argv:process.argv.slice(3),cwd:process.cwd(),probeEnv:process.env.ORANDA_RUN_TOKEN?'set':'unset'});
const rl=readline.createInterface({input:process.stdin});
const send=o=>process.stdout.write(JSON.stringify(o)+'\n');
rl.on('line',l=>{let m;try{m=JSON.parse(l)}catch{return}
 rec({ev:'rpc',method:m.method,name:m.params?.name,args:m.params?.arguments});
 if(m.method==='initialize')send({jsonrpc:'2.0',id:m.id,result:{protocolVersion:m.params?.protocolVersion||'2025-03-26',capabilities:{tools:{}},serverInfo:{name:'probe',version:'1.0.0'}}});
 else if(m.method==='tools/list')send({jsonrpc:'2.0',id:m.id,result:{tools:[{name:'echo_probe',description:'Echo the given text back.',inputSchema:{type:'object',properties:{text:{type:'string'}},required:['text']}},{name:'image_probe',description:'Returns a small image.',inputSchema:{type:'object',properties:{}}}]}});
 else if(m.method==='tools/call'&&m.params?.name==='image_probe')send({jsonrpc:'2.0',id:m.id,result:{content:[{type:'text',text:'img-follows'},{type:'image',mimeType:'image/png',data:'iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAAeUlEQVR4nO3PQQkAMAzAwIqof2UTMxF7HINABFzm7H7dcEEDWtCAFjSgBQ1oQQNa0IAWNKAFDWhBA1rQgBY0oAUNaEEDWtCAFjSgBQ1oQQNa0IAWNKAFDWhBA1rQgBY0oAUNaEEDWtCAFjSgBQ1oQQNa0IAWNKAFj12qxUDxeFqrFAAAAABJRU5ErkJggg=='}]}});
 else if(m.method==='tools/call')send({jsonrpc:'2.0',id:m.id,result:{content:[{type:'text',text:'probe-echo:'+(m.params?.arguments?.text??'')}]}});
 else if(m.id!==undefined)send({jsonrpc:'2.0',id:m.id,error:{code:-32601,message:'nf'}});
});
