## Desktop server conformance for any language

The [Desktop server corpus](../../../specs/desktop/server/README.md) states what the HTTP and WebSocket server under a
Runic Desktop host must, should and may do: the `Host` check, the default headers, message framing, file bodies and
byte ranges, the WebSocket handshake, frames, message limits and the close handshake. Each rule names its RFC section
or Runic requirement. It has 114 vectors, which the conformance driver runs against a server through a small adapter:
`bun run conformance rax-server` runs them against runic-server, and the planned C++ server will add an adapter of its
own. runic-server passes 109; the other five are three known gaps with RFC 9110 and RFC 6455 (#1413, #1414, #1415).
