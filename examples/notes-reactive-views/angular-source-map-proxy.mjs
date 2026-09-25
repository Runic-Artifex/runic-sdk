import { createServer as createHttpServer, request as httpRequest } from "node:http";
import { connect, createServer as createTcpServer } from "node:net";

export async function pickFreeLoopbackPort() {
  const listener = createTcpServer();
  try {
    await new Promise((resolve, reject) => {
      listener.once("error", reject);
      listener.listen(0, "127.0.0.1", resolve);
    });
    return listener.address().port;
  } finally {
    await new Promise(resolve => listener.close(resolve));
  }
}

// Angular's dev bundle currently carries a Debug ID that makes this Chrome
// build skip its otherwise valid source map. Rewrite only that comment in the
// IDE's loopback development response; leave the map and build output intact.
export async function startAngularSourceMapProxy(visiblePort, angularPort) {
  const sockets = new Set();
  const server = createHttpServer((incoming, outgoing) => {
    const headers = { ...incoming.headers, host: `127.0.0.1:${angularPort}`, "accept-encoding": "identity" };
    const upstream = httpRequest({
      hostname: "127.0.0.1", port: angularPort, method: incoming.method,
      path: incoming.url, headers,
    }, response => {
      const pathname = new URL(incoming.url, "http://127.0.0.1").pathname;
      const javascript = incoming.method === "GET" && pathname.endsWith(".js") &&
        response.statusCode === 200 && /javascript/.test(response.headers["content-type"] ?? "") &&
        !response.headers["content-encoding"];
      if (!javascript) {
        outgoing.writeHead(response.statusCode, response.headers);
        response.pipe(outgoing);
        return;
      }

      const chunks = [];
      response.on("data", chunk => chunks.push(chunk));
      response.on("end", () => {
        if (outgoing.destroyed) return;
        const original = Buffer.concat(chunks);
        const rewritten = Buffer.from(original.toString("utf8").replace(/^\/\/# debugId=[^\r\n]*(?:\r?\n|$)/gm, ""));
        const responseHeaders = { ...response.headers };
        delete responseHeaders["content-length"];
        delete responseHeaders["transfer-encoding"];
        delete responseHeaders.etag;
        outgoing.writeHead(response.statusCode, responseHeaders);
        outgoing.end(rewritten);
      });
      response.on("error", error => outgoing.destroy(error));
    });
    upstream.on("error", error => {
      if (!outgoing.headersSent) outgoing.writeHead(502, { "content-type": "text/plain" });
      outgoing.end(`Angular dev server is starting: ${error.message}`);
    });
    incoming.pipe(upstream);
  });

  server.on("connection", socket => {
    sockets.add(socket);
    socket.once("close", () => sockets.delete(socket));
  });
  server.on("upgrade", (incoming, browser, head) => {
    const angular = connect(angularPort, "127.0.0.1", () => {
      const headers = { ...incoming.headers, host: `127.0.0.1:${angularPort}` };
      angular.write(`${incoming.method} ${incoming.url} HTTP/1.1\r\n`);
      for (const [name, value] of Object.entries(headers))
        angular.write(`${name}: ${value}\r\n`);
      angular.write("\r\n");
      if (head.length) angular.write(head);
      browser.pipe(angular).pipe(browser);
    });
    angular.on("error", () => browser.destroy());
    browser.on("error", () => angular.destroy());
  });

  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(visiblePort, "127.0.0.1", resolve);
  });
  let closed = false;
  return () => {
    if (closed) return;
    closed = true;
    for (const socket of sockets) socket.destroy();
    server.close();
  };
}
