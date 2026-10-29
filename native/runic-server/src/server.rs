use std::collections::HashMap;
use std::io;
use std::net::{IpAddr, Ipv4Addr, SocketAddr, TcpListener as StdListener};
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, RwLock};
use std::time::Duration;

use axum::Router;
use axum::body::Body as AxumBody;
use axum::extract::connect_info::Connected;
use axum::extract::ws::WebSocketUpgrade;
use axum::extract::{ConnectInfo, FromRequestParts, Request, State};
use axum::http::{HeaderMap, HeaderName, HeaderValue, Method, StatusCode, header};
use axum::response::Response;
use axum::serve::IncomingStream;
use bytes::Bytes;
use futures_util::stream;
use tokio::net::TcpListener;
use tokio::runtime::Runtime;
use tokio::sync::{mpsc, oneshot, watch};

use crate::files;
use crate::host;
use crate::websocket::{self, Socket};

/// What the embedding host sees of a request before it answers. Borrowed for the duration of [`Handler::request`].
pub struct RequestHead<'a> {
    /// The request's id. A WebSocket accepted for it keeps the id as its connection id.
    pub id: u64,
    pub method: &'a Method,
    /// The request target as sent: the path and query, still percent-encoded.
    pub target: &'a str,
    pub headers: &'a HeaderMap,
    pub remote: SocketAddr,
    pub local: SocketAddr,
    /// Whether the request is a WebSocket upgrade the server can accept with [`Server::accept_websocket`].
    pub websocket: bool,
}

/// The kind of a WebSocket data message.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u32)]
pub enum MessageKind {
    Text = 1,
    Binary = 2,
}

/// A close frame the peer sent.
pub struct WebSocketClose<'a> {
    pub code: u16,
    pub reason: &'a str,
}

/// The embedding host. Every method is called on a server thread and must return without waiting on the server: a
/// host that does work queues it.
pub trait Handler: Send + Sync + 'static {
    /// A request arrived. Answer it by id with [`Server::respond`] or [`Server::accept_websocket`], from any thread.
    fn request(&self, head: &RequestHead<'_>);
    /// The requester disconnected before the response completed, or the server stopped. A later answer is ignored.
    fn request_aborted(&self, id: u64);
    /// A WebSocket message. `data` stays valid until the host calls [`Server::consume`] for `message`, or
    /// [`Server::release_websocket`]; until then the message counts against the connection's receive window.
    fn message(&self, connection: u64, message: u64, kind: MessageKind, data: &[u8]);
    /// The connection ended: the peer's close frame, or `None` when the transport ended without one. Called once.
    fn closed(&self, connection: u64, close: Option<WebSocketClose<'_>>);
    /// A write the server answered with [`WriteOutcome::Pending`] was accepted (`true`) or failed (`false`).
    fn completed(&self, token: u64, accepted: bool);
}

/// The listener and its limits.
#[derive(Clone, Debug)]
pub struct ServerConfig {
    /// Listen on every interface instead of loopback. The `Host` check applies only to loopback listeners.
    pub all_interfaces: bool,
    /// The port, or 0 for one the system chooses.
    pub port: u16,
    /// Worker threads of the server's runtime.
    pub worker_threads: usize,
    /// The largest WebSocket message the server reassembles; a larger one closes the connection with 1009.
    pub max_message_bytes: usize,
    /// Messages a connection may deliver before the host consumed earlier ones.
    pub receive_window: usize,
}

impl Default for ServerConfig {
    fn default() -> Self {
        Self { all_interfaces: false, port: 0, worker_threads: 1, max_message_bytes: 64 << 20, receive_window: 64 }
    }
}

/// How the body of a response is supplied.
pub enum Body {
    /// No body; the headers say what a HEAD request would have received.
    Empty,
    /// Chunks follow through [`Server::write`] and end with [`Server::end`].
    Stream,
    /// The file at this path, which the host resolved and admitted. The server answers a single byte range.
    File(PathBuf),
}

/// The result of a write.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum WriteOutcome {
    /// The data is queued within the granted capacity.
    Accepted,
    /// The queue is full: [`Handler::completed`] reports the token once the data is queued or failed.
    Pending,
}

#[derive(Debug)]
pub enum ServerError {
    InvalidArgument(&'static str),
    /// No request or connection with this id waits for this call.
    UnknownId,
    /// The requester or connection is gone.
    Closed,
    Bind(io::Error),
    Runtime(io::Error),
}

impl std::fmt::Display for ServerError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::InvalidArgument(what) => write!(f, "invalid argument: {what}"),
            Self::UnknownId => f.write_str("no request or connection with this id is waiting"),
            Self::Closed => f.write_str("the requester or connection is closed"),
            Self::Bind(error) => write!(f, "the listener could not be bound: {error}"),
            Self::Runtime(error) => write!(f, "the server runtime could not start: {error}"),
        }
    }
}

impl std::error::Error for ServerError {}

pub(crate) enum Payload {
    Empty,
    Stream(mpsc::Receiver<Result<Bytes, io::Error>>),
    File(PathBuf),
}

pub(crate) enum Decision {
    Respond { status: StatusCode, headers: HeaderMap, payload: Payload },
    Accept { protocol: Option<String>, headers: HeaderMap, socket: Arc<Socket> },
}

type BodySender = mpsc::Sender<Result<Bytes, io::Error>>;

pub(crate) struct Inner {
    handler: Arc<dyn Handler>,
    pub(crate) config: ServerConfig,
    port: u16,
    next_id: AtomicU64,
    pending: Mutex<HashMap<u64, oneshot::Sender<Decision>>>,
    bodies: Mutex<HashMap<u64, BodySender>>,
    pub(crate) sockets: Mutex<HashMap<u64, Arc<Socket>>>,
    // true once the server stopped: no handler call starts after stop took the write lock.
    stopped: RwLock<bool>,
    runtime: tokio::runtime::Handle,
}

impl Inner {
    // Calls the handler unless the server stopped. A stop waits for calls in progress.
    pub(crate) fn notify(&self, call: impl FnOnce(&dyn Handler)) {
        let stopped = self.stopped.read().unwrap_or_else(|poisoned| poisoned.into_inner());
        if !*stopped {
            call(self.handler.as_ref());
        }
    }

    pub(crate) fn spawn<F>(&self, future: F)
    where
        F: std::future::Future<Output = ()> + Send + 'static,
    {
        self.runtime.spawn(future);
    }

    fn is_stopped(&self) -> bool {
        *self.stopped.read().unwrap_or_else(|poisoned| poisoned.into_inner())
    }
}

/// A running server. Dropping it stops it.
pub struct Server {
    inner: Arc<Inner>,
    runtime: Option<Runtime>,
    shutdown: watch::Sender<bool>,
}

#[derive(Clone, Copy, Debug)]
pub(crate) struct Peer {
    remote: SocketAddr,
    local: SocketAddr,
}

impl Connected<IncomingStream<'_, TcpListener>> for Peer {
    fn connect_info(stream: IncomingStream<'_, TcpListener>) -> Self {
        // Called once per accepted connection, before it is served. Without TCP_NODELAY a response written as head and
        // body waits for the client's delayed acknowledgement (about 40 ms on Linux); Kestrel sets it too.
        let _ = stream.io().set_nodelay(true);
        let remote = *stream.remote_addr();
        let local = stream.io().local_addr().unwrap_or_else(|_| SocketAddr::new(IpAddr::V4(Ipv4Addr::LOCALHOST), 0));
        Self { remote, local }
    }
}

impl Server {
    /// Binds the listener and starts serving. Returns once the port is bound.
    pub fn start(config: ServerConfig, handler: Arc<dyn Handler>) -> Result<Self, ServerError> {
        if config.worker_threads == 0 || config.worker_threads > 64 {
            return Err(ServerError::InvalidArgument("worker_threads must be between 1 and 64"));
        }
        if config.max_message_bytes == 0 || config.receive_window == 0 {
            return Err(ServerError::InvalidArgument("max_message_bytes and receive_window must be positive"));
        }
        let address =
            if config.all_interfaces { IpAddr::V4(Ipv4Addr::UNSPECIFIED) } else { IpAddr::V4(Ipv4Addr::LOCALHOST) };
        let listener = StdListener::bind(SocketAddr::new(address, config.port)).map_err(ServerError::Bind)?;
        listener.set_nonblocking(true).map_err(ServerError::Bind)?;
        let port = listener.local_addr().map_err(ServerError::Bind)?.port();
        let runtime = tokio::runtime::Builder::new_multi_thread()
            .worker_threads(config.worker_threads)
            .max_blocking_threads(4)
            .thread_name("runic-server")
            .enable_io()
            .enable_time()
            .build()
            .map_err(ServerError::Runtime)?;
        let inner = Arc::new(Inner {
            handler,
            config,
            port,
            next_id: AtomicU64::new(1),
            pending: Mutex::new(HashMap::new()),
            bodies: Mutex::new(HashMap::new()),
            sockets: Mutex::new(HashMap::new()),
            stopped: RwLock::new(false),
            runtime: runtime.handle().clone(),
        });
        let (shutdown, mut stopping) = watch::channel(false);
        let listener = {
            let _entered = runtime.enter();
            TcpListener::from_std(listener).map_err(ServerError::Bind)?
        };
        let app = Router::new().fallback(handle).with_state(inner.clone());
        runtime.spawn(async move {
            let service = app.into_make_service_with_connect_info::<Peer>();
            let _ = axum::serve(listener, service)
                .with_graceful_shutdown(async move {
                    let _ = stopping.wait_for(|stopped| *stopped).await;
                })
                .await;
        });
        Ok(Self { inner, runtime: Some(runtime), shutdown })
    }

    /// The bound port.
    pub fn port(&self) -> u16 {
        self.inner.port
    }

    /// Answers request `id` with a response. Headers are sent as given, after the server's defaults for the ones
    /// missing (`X-Content-Type-Options: nosniff`, `X-Frame-Options: SAMEORIGIN`).
    pub fn respond(&self, id: u64, status: StatusCode, headers: HeaderMap, body: Body) -> Result<(), ServerError> {
        self.inner.respond(id, status, headers, body)
    }

    /// Writes the next chunk of request `id`'s streamed body.
    pub fn write(&self, id: u64, data: Bytes, token: u64) -> Result<WriteOutcome, ServerError> {
        let sender = self.inner.bodies.lock().unwrap().get(&id).cloned().ok_or(ServerError::UnknownId)?;
        offer(&self.inner, sender, Ok(data), token)
    }

    /// Ends request `id`'s streamed body.
    pub fn end(&self, id: u64) -> Result<(), ServerError> {
        self.inner.bodies.lock().unwrap().remove(&id).map(drop).ok_or(ServerError::UnknownId)
    }

    /// Aborts request `id`: before the response, the requester's connection closes; after it, the body ends with an
    /// error, which closes the connection.
    pub fn abort(&self, id: u64) {
        if self.inner.pending.lock().unwrap().remove(&id).is_some() {
            return;
        }
        if let Some(sender) = self.inner.bodies.lock().unwrap().remove(&id) {
            let _ = sender.try_send(Err(io::Error::other("the host aborted the response")));
        }
    }

    /// Accepts WebSocket request `id` with the chosen subprotocol. The connection id is `id`; messages may be sent at
    /// once and are delivered after the handshake.
    pub fn accept_websocket(&self, id: u64, protocol: Option<String>, headers: HeaderMap) -> Result<(), ServerError> {
        let decision = self.inner.pending.lock().unwrap().remove(&id).ok_or(ServerError::UnknownId)?;
        let socket = Socket::new(id, self.inner.config.receive_window);
        self.inner.sockets.lock().unwrap().insert(id, socket.clone());
        // The decision carries the socket, so a release before the handshake still completes it and then closes.
        if decision.send(Decision::Accept { protocol, headers, socket }).is_err() {
            self.inner.sockets.lock().unwrap().remove(&id);
            return Err(ServerError::Closed);
        }
        Ok(())
    }

    /// Sends a message on connection `connection`.
    pub fn send(
        &self,
        connection: u64,
        kind: MessageKind,
        data: Bytes,
        token: u64,
    ) -> Result<WriteOutcome, ServerError> {
        websocket::send(&self.inner, connection, websocket::Outgoing::Data(kind, data), token)
    }

    /// Sends a close frame after the queued messages. Later sends fail.
    pub fn close_websocket(
        &self,
        connection: u64,
        code: u16,
        reason: String,
        token: u64,
    ) -> Result<WriteOutcome, ServerError> {
        if reason.len() > 123 {
            return Err(ServerError::InvalidArgument("a close reason has at most 123 bytes"));
        }
        websocket::send(&self.inner, connection, websocket::Outgoing::Close(code, reason), token)
    }

    /// Releases a delivered message, which frees its place in the receive window.
    pub fn consume(&self, connection: u64, message: u64) {
        if let Some(socket) = self.inner.sockets.lock().unwrap().get(&connection).cloned() {
            socket.consume(message);
        }
    }

    /// Ends connection `connection` without a close handshake.
    pub fn abort_websocket(&self, connection: u64) {
        if let Some(socket) = self.inner.sockets.lock().unwrap().get(&connection).cloned() {
            socket.abort();
        }
    }

    /// The host is done with connection `connection`: its undelivered messages are freed, and it is aborted unless the
    /// host queued a close, which is still written (the close handshake then ends within a grace period). No pointer
    /// from [`Handler::message`] for it may be used afterwards.
    pub fn release_websocket(&self, connection: u64) {
        if let Some(socket) = self.inner.sockets.lock().unwrap().remove(&connection) {
            if !socket.is_close_queued() {
                socket.abort();
            }
            socket.release();
        }
    }

    /// Stops the server: no handler call starts after this returns, pending requests fail and connections close.
    pub fn stop(mut self) {
        self.stop_core();
    }

    fn stop_core(&mut self) {
        let Some(runtime) = self.runtime.take() else {
            return;
        };
        *self.inner.stopped.write().unwrap_or_else(|poisoned| poisoned.into_inner()) = true;
        let _ = self.shutdown.send(true);
        self.inner.pending.lock().unwrap().clear();
        self.inner.bodies.lock().unwrap().clear();
        for (_, socket) in self.inner.sockets.lock().unwrap().drain() {
            socket.abort();
            socket.release();
        }
        // A runtime cannot be shut down from one of its own threads.
        if tokio::runtime::Handle::try_current().is_ok() {
            std::thread::spawn(move || runtime.shutdown_timeout(Duration::from_secs(2)));
        } else {
            runtime.shutdown_timeout(Duration::from_secs(2));
        }
    }
}

impl Drop for Server {
    fn drop(&mut self) {
        self.stop_core();
    }
}

impl Inner {
    fn respond(&self, id: u64, status: StatusCode, headers: HeaderMap, body: Body) -> Result<(), ServerError> {
        let decision = self.pending.lock().unwrap().remove(&id).ok_or(ServerError::UnknownId)?;
        let payload = match body {
            Body::Empty => Payload::Empty,
            Body::File(path) => Payload::File(path),
            Body::Stream => {
                // Registered before the decision is sent, so the first write never misses the body.
                let (sender, receiver) = mpsc::channel(8);
                self.bodies.lock().unwrap().insert(id, sender);
                Payload::Stream(receiver)
            }
        };
        decision.send(Decision::Respond { status, headers, payload }).map_err(|_| {
            self.bodies.lock().unwrap().remove(&id);
            ServerError::Closed
        })
    }
}

pub(crate) fn offer<T: Send + 'static>(
    inner: &Arc<Inner>,
    sender: mpsc::Sender<T>,
    item: T,
    token: u64,
) -> Result<WriteOutcome, ServerError> {
    match sender.try_send(item) {
        Ok(()) => Ok(WriteOutcome::Accepted),
        Err(mpsc::error::TrySendError::Closed(_)) => Err(ServerError::Closed),
        Err(mpsc::error::TrySendError::Full(item)) => {
            let notify = inner.clone();
            inner.spawn(async move {
                let accepted = sender.send(item).await.is_ok();
                notify.notify(|handler| handler.completed(token, accepted));
            });
            Ok(WriteOutcome::Pending)
        }
    }
}

// Reports an abort for a request whose response did not complete, exactly once.
struct RequestGuard {
    inner: Arc<Inner>,
    id: u64,
    armed: bool,
}

impl RequestGuard {
    fn disarm(&mut self) {
        self.armed = false;
    }
}

impl Drop for RequestGuard {
    fn drop(&mut self) {
        self.inner.pending.lock().unwrap().remove(&self.id);
        self.inner.bodies.lock().unwrap().remove(&self.id);
        if self.armed {
            let id = self.id;
            self.inner.notify(|handler| handler.request_aborted(id));
        }
    }
}

fn defaults(headers: &mut HeaderMap) {
    headers.entry(header::X_CONTENT_TYPE_OPTIONS).or_insert(HeaderValue::from_static("nosniff"));
    headers.entry(header::X_FRAME_OPTIONS).or_insert(HeaderValue::from_static("SAMEORIGIN"));
}

fn status_only(status: StatusCode) -> Response {
    let mut response = Response::new(AxumBody::empty());
    *response.status_mut() = status;
    defaults(response.headers_mut());
    response.headers_mut().insert(header::CACHE_CONTROL, HeaderValue::from_static("no-store"));
    response.headers_mut().insert(header::CONTENT_LENGTH, HeaderValue::from(0u64));
    response
}

fn is_upgrade(headers: &HeaderMap) -> bool {
    let has = |name: HeaderName, token: &str| {
        headers.get_all(name).iter().any(|value| {
            value.to_str().is_ok_and(|value| value.split(',').any(|part| part.trim().eq_ignore_ascii_case(token)))
        })
    };
    has(header::CONNECTION, "upgrade") && has(header::UPGRADE, "websocket")
}

async fn handle(State(inner): State<Arc<Inner>>, ConnectInfo(peer): ConnectInfo<Peer>, request: Request) -> Response {
    if inner.is_stopped() {
        return status_only(StatusCode::SERVICE_UNAVAILABLE);
    }
    let (mut parts, _body) = request.into_parts();
    if !inner.config.all_interfaces
        && !host::admits(parts.headers.get(header::HOST).map(HeaderValue::as_bytes), inner.port)
    {
        return status_only(StatusCode::MISDIRECTED_REQUEST);
    }
    let upgrade = if parts.method == Method::GET && is_upgrade(&parts.headers) {
        WebSocketUpgrade::from_request_parts(&mut parts, &()).await.ok()
    } else {
        None
    };
    let id = inner.next_id.fetch_add(1, Ordering::Relaxed);
    let (decide, decision) = oneshot::channel();
    inner.pending.lock().unwrap().insert(id, decide);
    let mut guard = RequestGuard { inner: inner.clone(), id, armed: true };
    let target = parts.uri.path_and_query().map_or("/", |target| target.as_str());
    inner.notify(|handler| {
        handler.request(&RequestHead {
            id,
            method: &parts.method,
            target,
            headers: &parts.headers,
            remote: peer.remote,
            local: peer.local,
            websocket: upgrade.is_some(),
        });
    });
    let Ok(decision) = decision.await else {
        // The host dropped the request: the server stopped or the host aborted it.
        guard.disarm();
        return status_only(StatusCode::SERVICE_UNAVAILABLE);
    };
    match decision {
        Decision::Respond { status, mut headers, payload } => {
            defaults(&mut headers);
            match payload {
                Payload::Empty => {
                    guard.disarm();
                    let mut response = Response::new(AxumBody::empty());
                    *response.status_mut() = status;
                    *response.headers_mut() = headers;
                    response
                }
                Payload::File(path) => {
                    guard.disarm();
                    files::respond(path, status, headers, &parts.headers, parts.method == Method::HEAD).await
                }
                Payload::Stream(receiver) => {
                    // The guard moves into the body: dropping an unfinished body reports the abort.
                    let body = AxumBody::from_stream(stream::unfold(
                        (receiver, guard),
                        |(mut receiver, mut guard)| async move {
                            match receiver.recv().await {
                                Some(Ok(chunk)) => Some((Ok::<Bytes, io::Error>(chunk), (receiver, guard))),
                                Some(Err(error)) => {
                                    guard.disarm();
                                    Some((Err(error), (receiver, guard)))
                                }
                                None => {
                                    guard.disarm();
                                    None
                                }
                            }
                        },
                    ));
                    let mut response = Response::new(body);
                    *response.status_mut() = status;
                    *response.headers_mut() = headers;
                    response
                }
            }
        }
        Decision::Accept { protocol, mut headers, socket } => {
            guard.disarm();
            let Some(upgrade) = upgrade else {
                inner.sockets.lock().unwrap().remove(&id);
                return status_only(StatusCode::BAD_REQUEST);
            };
            defaults(&mut headers);
            let mut response = websocket::upgrade(&inner, socket, upgrade, protocol);
            for (name, value) in &headers {
                response.headers_mut().entry(name).or_insert_with(|| value.clone());
            }
            response
        }
    }
}
