//! The C ABI (`include/runic_server.h`). Every function is safe to call from any thread. Callbacks run on server
//! threads, never while the caller of a `runic_server_*` function waits, and none starts after `runic_server_stop`
//! returned. Pointers passed in are read before the function returns; pointers passed to callbacks are valid for the
//! callback, except message data, which stays valid until `runic_server_ws_consume` or `runic_server_ws_release`.
//!
//! # Safety
//!
//! Every function shares one contract, so none repeats it: pointer arguments are null or valid for the given length,
//! a server pointer comes from `runic_server_start` and is not used after `runic_server_stop`, and the callbacks and
//! context of `RunicServerOptions` stay valid until `runic_server_stop` returns.
#![allow(clippy::missing_safety_doc)]

use std::cell::RefCell;
use std::ffi::c_void;
use std::path::PathBuf;
use std::sync::Arc;

use axum::http::{HeaderMap, HeaderName, HeaderValue, StatusCode};
use bytes::Bytes;

use crate::server::{
    Body, Handler, MessageKind, RequestHead, Server, ServerConfig, ServerError, WebSocketClose, WriteOutcome,
};

/// Bumped on every incompatible change of this file's types or functions.
pub const ABI_VERSION: u32 = 2;

pub const OK: i32 = 0;
pub const PENDING: i32 = 1;
pub const FLUSH: i32 = 2;
pub const INVALID_ARGUMENT: i32 = -1;
pub const UNKNOWN_ID: i32 = -2;
pub const CLOSED: i32 = -3;
pub const BIND_FAILED: i32 = -4;
pub const ABI_MISMATCH: i32 = -5;
pub const INTERNAL: i32 = -6;

pub const FLAG_ALL_INTERFACES: u32 = 1;
pub const REQUEST_WEBSOCKET: u32 = 1;

pub const BODY_EMPTY: u32 = 0;
pub const BODY_STREAM: u32 = 1;
pub const BODY_FILE: u32 = 2;

#[repr(C)]
#[derive(Clone, Copy)]
pub struct RunicStr {
    pub ptr: *const u8,
    pub len: usize,
}

impl RunicStr {
    fn of(value: &[u8]) -> Self {
        Self { ptr: value.as_ptr(), len: value.len() }
    }

    unsafe fn bytes<'a>(self) -> &'a [u8] {
        if self.len == 0 || self.ptr.is_null() {
            &[]
        } else {
            unsafe { std::slice::from_raw_parts(self.ptr, self.len) }
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct RunicHeader {
    pub name: RunicStr,
    pub value: RunicStr,
}

#[repr(C)]
pub struct RunicRequest {
    pub id: u64,
    pub flags: u32,
    pub remote_port: u16,
    pub local_port: u16,
    pub method: RunicStr,
    pub target: RunicStr,
    pub remote_address: RunicStr,
    pub local_address: RunicStr,
    pub headers: *const RunicHeader,
    pub header_count: usize,
}

pub type RequestCallback = unsafe extern "C" fn(context: *mut c_void, request: *const RunicRequest);
pub type AbortedCallback = unsafe extern "C" fn(context: *mut c_void, id: u64);
pub type MessageCallback = unsafe extern "C" fn(
    context: *mut c_void,
    connection: u64,
    message: u64,
    kind: u32,
    data: *const u8,
    length: usize,
);
pub type ClosedCallback =
    unsafe extern "C" fn(context: *mut c_void, connection: u64, code: i32, reason: *const u8, length: usize);
pub type CompletedCallback = unsafe extern "C" fn(context: *mut c_void, token: u64, accepted: i32);

#[repr(C)]
pub struct RunicServerOptions {
    pub abi_version: u32,
    pub flags: u32,
    pub port: u16,
    pub reserved: u16,
    pub worker_threads: u32,
    pub max_message_bytes: u64,
    pub receive_window: u32,
    pub context: *mut c_void,
    pub on_request: Option<RequestCallback>,
    pub on_aborted: Option<AbortedCallback>,
    pub on_message: Option<MessageCallback>,
    pub on_closed: Option<ClosedCallback>,
    pub on_completed: Option<CompletedCallback>,
}

struct Callbacks {
    context: *mut c_void,
    on_request: RequestCallback,
    on_aborted: AbortedCallback,
    on_message: MessageCallback,
    on_closed: ClosedCallback,
    on_completed: CompletedCallback,
}

// The host's context pointer is opaque here; the host makes its callbacks thread-safe.
unsafe impl Send for Callbacks {}
unsafe impl Sync for Callbacks {}

impl Handler for Callbacks {
    fn request(&self, head: &RequestHead<'_>) {
        let headers: Vec<RunicHeader> = head
            .headers
            .iter()
            .map(|(name, value)| RunicHeader {
                name: RunicStr::of(name.as_str().as_bytes()),
                value: RunicStr::of(value.as_bytes()),
            })
            .collect();
        let remote = head.remote.ip().to_string();
        let local = head.local.ip().to_string();
        let request = RunicRequest {
            id: head.id,
            flags: if head.websocket { REQUEST_WEBSOCKET } else { 0 },
            remote_port: head.remote.port(),
            local_port: head.local.port(),
            method: RunicStr::of(head.method.as_str().as_bytes()),
            target: RunicStr::of(head.target.as_bytes()),
            remote_address: RunicStr::of(remote.as_bytes()),
            local_address: RunicStr::of(local.as_bytes()),
            headers: headers.as_ptr(),
            header_count: headers.len(),
        };
        unsafe { (self.on_request)(self.context, &request) };
    }

    fn request_aborted(&self, id: u64) {
        unsafe { (self.on_aborted)(self.context, id) };
    }

    fn message(&self, connection: u64, message: u64, kind: MessageKind, data: &[u8]) {
        unsafe { (self.on_message)(self.context, connection, message, kind as u32, data.as_ptr(), data.len()) };
    }

    fn closed(&self, connection: u64, close: Option<WebSocketClose<'_>>) {
        match close {
            Some(close) => unsafe {
                (self.on_closed)(
                    self.context,
                    connection,
                    i32::from(close.code),
                    close.reason.as_ptr(),
                    close.reason.len(),
                );
            },
            None => unsafe { (self.on_closed)(self.context, connection, -1, std::ptr::null(), 0) },
        }
    }

    fn completed(&self, token: u64, accepted: bool) {
        unsafe { (self.on_completed)(self.context, token, i32::from(accepted)) };
    }
}

thread_local! {
    static LAST_ERROR: RefCell<String> = const { RefCell::new(String::new()) };
}

fn fail(code: i32, message: impl Into<String>) -> i32 {
    LAST_ERROR.with(|last| *last.borrow_mut() = message.into());
    code
}

fn code(error: &ServerError) -> i32 {
    let code = match error {
        ServerError::InvalidArgument(_) => INVALID_ARGUMENT,
        ServerError::UnknownId => UNKNOWN_ID,
        ServerError::Closed => CLOSED,
        ServerError::Bind(_) => BIND_FAILED,
        ServerError::Runtime(_) => INTERNAL,
    };
    fail(code, error.to_string())
}

fn outcome(result: Result<WriteOutcome, ServerError>) -> i32 {
    match result {
        Ok(WriteOutcome::Accepted) => OK,
        Ok(WriteOutcome::Pending) => PENDING,
        Ok(WriteOutcome::Flush) => FLUSH,
        Err(error) => code(&error),
    }
}

fn unit(result: Result<(), ServerError>) -> i32 {
    result.map_or_else(|error| code(&error), |()| OK)
}

unsafe fn headers(headers: *const RunicHeader, count: usize) -> Result<HeaderMap, i32> {
    let mut map = HeaderMap::with_capacity(count);
    if count == 0 {
        return Ok(map);
    }
    if headers.is_null() {
        return Err(fail(INVALID_ARGUMENT, "headers is null"));
    }
    for header in unsafe { std::slice::from_raw_parts(headers, count) } {
        let name = HeaderName::from_bytes(unsafe { header.name.bytes() })
            .map_err(|_| fail(INVALID_ARGUMENT, "a header name is not a valid token"))?;
        let value = HeaderValue::from_bytes(unsafe { header.value.bytes() })
            .map_err(|_| fail(INVALID_ARGUMENT, format!("the value of header {name} is not valid")))?;
        map.append(name, value);
    }
    Ok(map)
}

unsafe fn server<'a>(server: *const Server) -> Option<&'a Server> {
    unsafe { server.as_ref() }
}

/// The ABI version this library implements.
#[unsafe(no_mangle)]
pub extern "C" fn runic_server_abi_version() -> u32 {
    ABI_VERSION
}

/// Copies the calling thread's last error message into `buffer` and returns its full length in bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn runic_server_last_error(buffer: *mut u8, capacity: usize) -> usize {
    LAST_ERROR.with(|last| {
        let last = last.borrow();
        let length = last.len().min(capacity);
        if length > 0 && !buffer.is_null() {
            unsafe { std::ptr::copy_nonoverlapping(last.as_ptr(), buffer, length) };
        }
        last.len()
    })
}

/// Starts a server. On success writes the server and its bound port.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn runic_server_start(
    options: *const RunicServerOptions,
    out_server: *mut *mut Server,
    out_port: *mut u16,
) -> i32 {
    let Some(options) = (unsafe { options.as_ref() }) else {
        return fail(INVALID_ARGUMENT, "options is null");
    };
    if options.abi_version != ABI_VERSION {
        return fail(ABI_MISMATCH, format!("the caller uses ABI {}, the library {ABI_VERSION}", options.abi_version));
    }
    if out_server.is_null() || out_port.is_null() {
        return fail(INVALID_ARGUMENT, "an output pointer is null");
    }
    let (Some(on_request), Some(on_aborted), Some(on_message), Some(on_closed), Some(on_completed)) =
        (options.on_request, options.on_aborted, options.on_message, options.on_closed, options.on_completed)
    else {
        return fail(INVALID_ARGUMENT, "every callback is required");
    };
    let Ok(max_message_bytes) = usize::try_from(options.max_message_bytes) else {
        return fail(INVALID_ARGUMENT, "max_message_bytes is too large");
    };
    let config = ServerConfig {
        all_interfaces: options.flags & FLAG_ALL_INTERFACES != 0,
        port: options.port,
        worker_threads: options.worker_threads as usize,
        max_message_bytes,
        receive_window: options.receive_window as usize,
    };
    let callbacks = Callbacks { context: options.context, on_request, on_aborted, on_message, on_closed, on_completed };
    match Server::start(config, Arc::new(callbacks)) {
        Ok(server) => {
            unsafe {
                *out_port = server.port();
                *out_server = Box::into_raw(Box::new(server));
            }
            OK
        }
        Err(error) => code(&error),
    }
}

/// Stops and frees a server. No callback runs after this returns; the server pointer is invalid.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn runic_server_stop(server: *mut Server) {
    if !server.is_null() {
        unsafe { Box::from_raw(server) }.stop();
    }
}

/// Answers request `id`. `body` is BODY_EMPTY, BODY_STREAM (then write and end) or BODY_FILE with `file_path`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn runic_server_respond(
    server: *const Server,
    id: u64,
    status: u16,
    header_list: *const RunicHeader,
    header_count: usize,
    body: u32,
    file_path: RunicStr,
) -> i32 {
    let Some(server) = (unsafe { self::server(server) }) else {
        return fail(INVALID_ARGUMENT, "server is null");
    };
    let Ok(status) = StatusCode::from_u16(status) else {
        return fail(INVALID_ARGUMENT, "status is not an HTTP status code");
    };
    let headers = match unsafe { headers(header_list, header_count) } {
        Ok(headers) => headers,
        Err(code) => return code,
    };
    let body = match body {
        BODY_EMPTY => Body::Empty,
        BODY_STREAM => Body::Stream,
        BODY_FILE => match std::str::from_utf8(unsafe { file_path.bytes() }) {
            Ok(path) if !path.is_empty() => Body::File(PathBuf::from(path)),
            _ => return fail(INVALID_ARGUMENT, "file_path must be a UTF-8 path"),
        },
        _ => return fail(INVALID_ARGUMENT, "unknown body kind"),
    };
    unit(server.respond(id, status, headers, body))
}

/// Writes a chunk of a streamed body. Returns OK when queued, PENDING when on_completed reports `token` later.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn runic_server_write(
    server: *const Server,
    id: u64,
    data: *const u8,
    length: usize,
    token: u64,
) -> i32 {
    let Some(server) = (unsafe { self::server(server) }) else {
        return fail(INVALID_ARGUMENT, "server is null");
    };
    let data = Bytes::copy_from_slice(unsafe { RunicStr { ptr: data, len: length }.bytes() });
    outcome(server.write(id, data, token))
}

/// Ends a streamed body.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn runic_server_end(server: *const Server, id: u64) -> i32 {
    let Some(server) = (unsafe { self::server(server) }) else {
        return fail(INVALID_ARGUMENT, "server is null");
    };
    unit(server.end(id))
}

/// Aborts a request or its streamed body.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn runic_server_abort(server: *const Server, id: u64) {
    if let Some(server) = unsafe { self::server(server) } {
        server.abort(id);
    }
}

/// Accepts WebSocket request `id` with `protocol` (empty for none) and extra handshake response headers.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn runic_server_ws_accept(
    server: *const Server,
    id: u64,
    protocol: RunicStr,
    header_list: *const RunicHeader,
    header_count: usize,
) -> i32 {
    let Some(server) = (unsafe { self::server(server) }) else {
        return fail(INVALID_ARGUMENT, "server is null");
    };
    let protocol = match std::str::from_utf8(unsafe { protocol.bytes() }) {
        Ok("") => None,
        Ok(protocol) => Some(protocol.to_owned()),
        Err(_) => return fail(INVALID_ARGUMENT, "protocol is not UTF-8"),
    };
    let headers = match unsafe { headers(header_list, header_count) } {
        Ok(headers) => headers,
        Err(code) => return code,
    };
    unit(server.accept_websocket(id, protocol, headers))
}

/// Sends a message (`kind` 1 text, 2 binary). Returns OK when queued, PENDING when on_completed reports `token`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn runic_server_ws_send(
    server: *const Server,
    connection: u64,
    kind: u32,
    data: *const u8,
    length: usize,
    token: u64,
) -> i32 {
    let Some(server) = (unsafe { self::server(server) }) else {
        return fail(INVALID_ARGUMENT, "server is null");
    };
    let kind = match kind {
        1 => MessageKind::Text,
        2 => MessageKind::Binary,
        _ => return fail(INVALID_ARGUMENT, "unknown message kind"),
    };
    let data = unsafe { RunicStr { ptr: data, len: length }.bytes() };
    outcome(server.send(connection, kind, data, token))
}

/// Queues a message without writing it (`kind` 1 text, 2 binary). Returns OK when queued behind a write in progress,
/// FLUSH when queued and the caller now holds the connection's write and must call `runic_server_ws_flush`, or PENDING
/// when on_completed reports `token`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn runic_server_ws_queue(
    server: *const Server,
    connection: u64,
    kind: u32,
    data: *const u8,
    length: usize,
    token: u64,
) -> i32 {
    let Some(server) = (unsafe { self::server(server) }) else {
        return fail(INVALID_ARGUMENT, "server is null");
    };
    let kind = match kind {
        1 => MessageKind::Text,
        2 => MessageKind::Binary,
        _ => return fail(INVALID_ARGUMENT, "unknown message kind"),
    };
    let data = unsafe { RunicStr { ptr: data, len: length }.bytes() };
    outcome(server.queue(connection, kind, data, token))
}

/// Writes the connection's queue from the calling thread after `runic_server_ws_queue` returned FLUSH.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn runic_server_ws_flush(server: *const Server, connection: u64) {
    if let Some(server) = unsafe { self::server(server) } {
        server.flush(connection);
    }
}

/// Queues a close frame after the queued messages.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn runic_server_ws_close(
    server: *const Server,
    connection: u64,
    code: u16,
    reason: RunicStr,
    token: u64,
) -> i32 {
    let Some(server) = (unsafe { self::server(server) }) else {
        return fail(INVALID_ARGUMENT, "server is null");
    };
    let Ok(reason) = std::str::from_utf8(unsafe { reason.bytes() }) else {
        return fail(INVALID_ARGUMENT, "reason is not UTF-8");
    };
    outcome(server.close_websocket(connection, code, reason.to_owned(), token))
}

/// Frees a delivered message's data and its place in the receive window.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn runic_server_ws_consume(server: *const Server, connection: u64, message: u64) {
    if let Some(server) = unsafe { self::server(server) } {
        server.consume(connection, message);
    }
}

/// Ends a connection without a close handshake.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn runic_server_ws_abort(server: *const Server, connection: u64) {
    if let Some(server) = unsafe { self::server(server) } {
        server.abort_websocket(connection);
    }
}

/// Aborts the connection if open and frees its undelivered message data.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn runic_server_ws_release(server: *const Server, connection: u64) {
    if let Some(server) = unsafe { self::server(server) } {
        server.release_websocket(connection);
    }
}
