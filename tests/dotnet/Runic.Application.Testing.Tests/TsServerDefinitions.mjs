// Drives tsserver over its stdio protocol as an editor does, for the generated clients' declaration maps (#952).
// Usage: bun TsServerDefinitions.mjs <tsserver.js> <file> <line>:<offset>:<definition|typeDefinition>...
// Prints one JSON array per position: the "path:line:offset" (1-based) of each definition tsserver returns.
import { spawn } from "node:child_process";
import { readFile } from "node:fs/promises";

const [tsserver, file, ...positions] = process.argv.slice(2);
const server = spawn(process.execPath, [tsserver, "--disableAutomaticTypingAcquisition"], { stdio: ["pipe", "pipe", "inherit"] });
let buffer = Buffer.alloc(0), sequence = 0;
const pending = new Map();
server.stdout.on("data", chunk => {
  buffer = Buffer.concat([buffer, chunk]);
  for (;;) {
    const header = /Content-Length: (\d+)\r\n\r\n/.exec(buffer.toString("latin1"));
    if (!header) break;
    const start = header.index + header[0].length, length = Number(header[1]);
    if (buffer.length < start + length) break;
    const message = JSON.parse(buffer.subarray(start, start + length).toString("utf8"));
    buffer = buffer.subarray(start + length);
    if (message.type === "response" && pending.has(message.request_seq)) {
      pending.get(message.request_seq)(message);
      pending.delete(message.request_seq);
    }
  }
});
const request = (command, args) => new Promise(resolve => {
  const id = ++sequence;
  pending.set(id, resolve);
  server.stdin.write(JSON.stringify({ seq: id, type: "request", command, arguments: args }) + "\n");
});
try {
  // open has no response; the requests that follow it are answered in order.
  server.stdin.write(JSON.stringify({ seq: ++sequence, type: "request", command: "open", arguments: { file, fileContent: await readFile(file, "utf8") } }) + "\n");
  const results = [];
  for (const position of positions) {
    const [line, offset, command] = position.split(":");
    const response = await request(command, { file, line: Number(line), offset: Number(offset) });
    if (!response.success) throw new Error(`tsserver ${command} ${line}:${offset}: ${response.message}`);
    results.push(response.body.map(item => `${item.file}:${item.start.line}:${item.start.offset}`));
  }
  console.log(JSON.stringify(results));
} finally {
  server.kill();
}
