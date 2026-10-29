//! Experimental native HTTP and WebSocket engine for Runic Desktop surfaces (X1).
//!
//! The server owns the network side of `runic.desktop.presentation/1` (specs/desktop/presentation-host.md): the
//! loopback listener, the `Host` check against DNS rebinding, HTTP/1.1 framing, WebSocket framing, file streaming with
//! byte ranges, response backpressure and default security headers. Everything that decides what a request means stays
//! with the embedding host: which content a path serves, the bootstrap scripts, origin and credential admission, the
//! client cookie and the Views protocol. The host receives every request through [`Handler::request`] and answers it by
//! id with [`Server::respond`], [`Server::accept_websocket`] and the body and message calls.
//!
//! The C ABI over this API is in [`ffi`]; the .NET host binds it in `Runic.Desktop` (`Internal/NativeServer`).

pub mod ffi;
mod files;
mod host;
mod server;
mod websocket;

pub use server::{
    Body, Handler, MessageKind, RequestHead, Server, ServerConfig, ServerError, WebSocketClose, WriteOutcome,
};
