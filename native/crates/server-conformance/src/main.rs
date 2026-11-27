//! runic-server's conformance adapter (eng/conformance/PROTOCOL.md, "Desktop server"). It starts the server with the
//! corpus's test application (specs/desktop/server/README.md, "Test application"), records what the application sees
//! and answers the driver's requests, one JSON object per line on stdin and stdout. The driver plays the client over
//! real sockets; this program never checks the spec itself.

mod json;

use std::collections::HashMap;
use std::io::{BufRead, Write};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex, OnceLock, RwLock, mpsc};
use std::time::{Duration, Instant};

use axum::http::{HeaderMap, HeaderValue, StatusCode, header};
use base64::Engine as _;
use bytes::Bytes;
use json::Json;
use rax_server::{Body, BodyRead, Handler, MessageKind, RequestHead, Server, ServerConfig, WebSocketClose};
use sha1::{Digest, Sha1};

const PROTOCOL: &str = "rax.conformance-adapter/1";
// The header that names the driver's connection; requests without it (other processes probing a fresh loopback port)
// are answered but not recorded.
const TAG: &str = "x-rax-conformance";
const FILE_CONTENT: &[u8] = b"0123456789";

type Job = Box<dyn FnOnce(&Server) + Send>;

// The running server and its generation: a job queued for one server never runs against the next one, whose request
// ids start at 1 again.
static SLOT: RwLock<Option<(u64, Server)>> = RwLock::new(None);
static WORKER: OnceLock<Mutex<mpsc::Sender<(u64, Job)>>> = OnceLock::new();

// The application thread: the host's work, queued by the handler, which must return without waiting on the server.
fn queue(generation: u64, job: Job) {
    let worker = WORKER.get_or_init(|| {
        let (sender, receiver) = mpsc::channel::<(u64, Job)>();
        std::thread::spawn(move || {
            for (generation, job) in receiver {
                let slot = SLOT.read().unwrap_or_else(|poisoned| poisoned.into_inner());
                if let Some((current, server)) = slot.as_ref()
                    && *current == generation
                {
                    job(server);
                }
            }
        });
        Mutex::new(sender)
    });
    let _ = worker.lock().unwrap().send((generation, job));
}

struct Files {
    file: PathBuf,
    missing: PathBuf,
}

// The test application of one server: its routes, and what it records for the driver.
struct App {
    generation: u64,
    admit: bool,
    files: Arc<Files>,
    events: Mutex<Vec<Json>>,
    arrived: Condvar,
    // Request and connection ids of the driver's requests, and their tags.
    tags: Mutex<HashMap<u64, String>>,
    // Uploads being read, by read token: the request id and the bytes so far.
    uploads: Mutex<HashMap<u64, (u64, Vec<u8>)>>,
    next_token: AtomicU64,
    stopped: AtomicBool,
}

impl App {
    fn record(&self, event: Json) {
        if self.stopped.load(Ordering::SeqCst) {
            return;
        }
        self.events.lock().unwrap().push(event);
        self.arrived.notify_all();
    }

    fn tag(&self, id: u64) -> Option<String> {
        self.tags.lock().unwrap().get(&id).cloned()
    }

    fn respond(&self, id: u64, status: StatusCode, headers: HeaderMap, body: Body) {
        queue(
            self.generation,
            Box::new(move |server| {
                let _ = server.respond(id, status, headers, body);
            }),
        );
    }
}

fn headers<const N: usize>(pairs: [(header::HeaderName, &'static str); N]) -> HeaderMap {
    let mut map = HeaderMap::new();
    for (name, value) in pairs {
        map.append(name, HeaderValue::from_static(value));
    }
    map
}

fn text_plain() -> HeaderMap {
    headers([(header::CONTENT_TYPE, "text/plain; charset=utf-8")])
}

fn base64(data: &[u8]) -> String {
    let mut out = vec![0u8; data.len().div_ceil(3) * 4];
    let length = base64::engine::general_purpose::STANDARD.encode_slice(data, &mut out).expect("sized for the data");
    out.truncate(length);
    String::from_utf8(out).expect("base64 is ASCII")
}

fn unbase64(text: &str) -> Result<Vec<u8>, String> {
    let mut out = vec![0u8; text.len() / 4 * 3 + 3];
    let length =
        base64::engine::general_purpose::STANDARD.decode_slice(text, &mut out).map_err(|error| error.to_string())?;
    out.truncate(length);
    Ok(out)
}

impl Handler for App {
    fn request(&self, head: &RequestHead<'_>) {
        let id = head.id;
        if let Some(tag) = head.headers.get(TAG).and_then(|value| value.to_str().ok()) {
            self.tags.lock().unwrap().insert(id, tag.to_owned());
            let header_list = head
                .headers
                .iter()
                .map(|(name, value)| {
                    Json::Array(vec![Json::text(name.as_str()), Json::text(String::from_utf8_lossy(value.as_bytes()))])
                })
                .collect();
            self.record(Json::object([
                ("type", Json::text("request")),
                ("conn", Json::text(tag)),
                ("method", Json::text(head.method.as_str())),
                ("target", Json::text(head.target)),
                ("websocket", Json::Bool(head.websocket)),
                ("headers", Json::Array(header_list)),
            ]));
        }
        let (path, query) = head.target.split_once('?').unwrap_or((head.target, ""));
        let files = self.files.clone();
        match path {
            "/ws" if head.websocket => {
                let offered: Vec<String> = head
                    .headers
                    .get_all(header::SEC_WEBSOCKET_PROTOCOL)
                    .iter()
                    .filter_map(|value| value.to_str().ok())
                    .flat_map(|value| value.split(','))
                    .map(|name| name.trim().to_owned())
                    .collect();
                let protocol = offered.iter().any(|name| name == "rax.test").then(|| "rax.test".to_owned());
                let hello = query == "hello";
                queue(
                    self.generation,
                    Box::new(move |server| {
                        if server.accept_websocket(id, protocol, HeaderMap::new()).is_ok() && hello {
                            let _ = server.send(id, MessageKind::Text, b"hello", 0);
                        }
                    }),
                );
            }
            "/ws" => self.respond(id, StatusCode::BAD_REQUEST, HeaderMap::new(), Body::Empty),
            "/ws-reject" => self.respond(id, StatusCode::FORBIDDEN, HeaderMap::new(), Body::Empty),
            "/text" => self.respond(id, StatusCode::OK, text_plain(), Body::Bytes(Bytes::from_static(b"hello"))),
            "/stream" => queue(
                self.generation,
                Box::new(move |server| {
                    if server.respond(id, StatusCode::OK, text_plain(), Body::Stream).is_ok() {
                        for chunk in ["one ", "two ", "three"] {
                            let _ = server.write(id, Bytes::from_static(chunk.as_bytes()), 0);
                        }
                        let _ = server.end(id);
                    }
                }),
            ),
            "/empty" => self.respond(id, StatusCode::NO_CONTENT, HeaderMap::new(), Body::Empty),
            "/file" => self.respond(
                id,
                StatusCode::OK,
                headers([(header::CONTENT_TYPE, "application/octet-stream")]),
                Body::File(files.file.clone()),
            ),
            "/missing-file" => self.respond(
                id,
                StatusCode::OK,
                headers([(header::CONTENT_TYPE, "application/octet-stream")]),
                Body::File(files.missing.clone()),
            ),
            "/frame-deny" => {
                self.respond(id, StatusCode::NO_CONTENT, headers([(header::X_FRAME_OPTIONS, "DENY")]), Body::Empty)
            }
            "/csp-ancestors" => self.respond(
                id,
                StatusCode::NO_CONTENT,
                headers([(
                    header::CONTENT_SECURITY_POLICY,
                    "default-src 'self'; frame-ancestors http://127.0.0.1:8080",
                )]),
                Body::Empty,
            ),
            "/csp-ancestors-upper" => self.respond(
                id,
                StatusCode::NO_CONTENT,
                headers([(header::CONTENT_SECURITY_POLICY, "FRAME-ANCESTORS 'self'")]),
                Body::Empty,
            ),
            "/csp-other" => self.respond(
                id,
                StatusCode::NO_CONTENT,
                headers([(header::CONTENT_SECURITY_POLICY, "default-src 'self'")]),
                Body::Empty,
            ),
            "/cookies" => self.respond(
                id,
                StatusCode::NO_CONTENT,
                headers([(header::SET_COOKIE, "a=1"), (header::SET_COOKIE, "b=2")]),
                Body::Empty,
            ),
            "/upload" => {
                let token = self.next_token.fetch_add(1, Ordering::Relaxed);
                self.uploads.lock().unwrap().insert(token, (id, Vec::new()));
                queue(
                    self.generation,
                    Box::new(move |server| {
                        let _ = server.read(id, token);
                    }),
                );
            }
            // Never answered: the requester leaves, or the server stops.
            "/slow" => {}
            _ => self.respond(id, StatusCode::NOT_FOUND, HeaderMap::new(), Body::Empty),
        }
    }

    fn request_aborted(&self, id: u64) {
        if let Some(tag) = self.tag(id) {
            self.record(Json::object([("type", Json::text("aborted")), ("conn", Json::text(tag))]));
        }
    }

    fn message(&self, connection: u64, message: u64, kind: MessageKind, data: &[u8]) {
        if let Some(tag) = self.tag(connection) {
            let kind = if kind == MessageKind::Text { "text" } else { "binary" };
            self.record(Json::object([
                ("type", Json::text("message")),
                ("conn", Json::text(tag)),
                ("kind", Json::text(kind)),
                ("data", Json::text(base64(data))),
            ]));
        }
        queue(self.generation, Box::new(move |server| server.consume(connection, message)));
    }

    fn closed(&self, connection: u64, close: Option<WebSocketClose<'_>>) {
        if let Some(tag) = self.tag(connection) {
            let (code, reason) = match &close {
                Some(close) => (Json::Number(f64::from(close.code)), Json::text(close.reason)),
                None => (Json::Null, Json::Null),
            };
            self.record(Json::object([
                ("type", Json::text("closed")),
                ("conn", Json::text(tag)),
                ("code", code),
                ("reason", reason),
            ]));
        }
        queue(self.generation, Box::new(move |server| server.release_websocket(connection)));
    }

    fn completed(&self, _token: u64, _accepted: bool) {}

    fn received(&self, token: u64, read: BodyRead<'_>) {
        let mut uploads = self.uploads.lock().unwrap();
        let Some((id, data)) = uploads.get_mut(&token) else { return };
        let id = *id;
        match read {
            BodyRead::Data(chunk) => {
                data.extend_from_slice(chunk);
                queue(
                    self.generation,
                    Box::new(move |server| {
                        let _ = server.read(id, token);
                    }),
                );
            }
            BodyRead::End => {
                let (_, data) = uploads.remove(&token).expect("present");
                let digest: String = Sha1::digest(&data).iter().map(|byte| format!("{byte:02x}")).collect();
                let body = Bytes::from(format!("{} {digest}", data.len()));
                drop(uploads);
                self.respond(id, StatusCode::OK, text_plain(), Body::Bytes(body));
            }
            BodyRead::Failed => {
                uploads.remove(&token);
            }
        }
    }

    fn admit(&self, _remote: std::net::SocketAddr, _local: std::net::SocketAddr) -> bool {
        self.admit
    }
}

struct Adapter {
    files: Arc<Files>,
    app: Option<Arc<App>>,
    generation: u64,
}

impl Adapter {
    fn stop(&mut self) {
        if let Some(app) = self.app.take() {
            app.stopped.store(true, Ordering::SeqCst);
        }
        let taken = SLOT.write().unwrap_or_else(|poisoned| poisoned.into_inner()).take();
        if let Some((_, server)) = taken {
            server.stop();
        }
    }

    fn start(&mut self, args: &Json) -> Result<Json, String> {
        self.stop();
        self.generation += 1;
        let mut config = ServerConfig::default();
        if let Some(limit) = args.get("maxMessageBytes").and_then(Json::as_u64) {
            config.max_message_bytes = usize::try_from(limit).map_err(|_| "maxMessageBytes is too large")?;
        }
        config.ipv6_loopback = args.get("ipv6Loopback").and_then(Json::as_bool).unwrap_or(false);
        let admit = args.get("admit").and_then(Json::as_bool);
        config.check_peers = admit.is_some();
        let app = Arc::new(App {
            generation: self.generation,
            admit: admit.unwrap_or(true),
            files: self.files.clone(),
            events: Mutex::new(Vec::new()),
            arrived: Condvar::new(),
            tags: Mutex::new(HashMap::new()),
            uploads: Mutex::new(HashMap::new()),
            next_token: AtomicU64::new(1),
            stopped: AtomicBool::new(false),
        });
        let server = Server::start(config.clone(), app.clone()).map_err(|error| error.to_string())?;
        let port = server.port();
        *SLOT.write().unwrap_or_else(|poisoned| poisoned.into_inner()) = Some((self.generation, server));
        self.app = Some(app);
        let address = if config.ipv6_loopback { "::1" } else { "127.0.0.1" };
        Ok(Json::object([("port", Json::Number(f64::from(port))), ("address", Json::text(address))]))
    }

    fn events(&self, args: &Json) -> Result<Json, String> {
        let app = self.app.as_ref().ok_or("no server is running")?;
        let count = args.get("count").and_then(Json::as_u64).unwrap_or(0) as usize;
        let deadline = Instant::now() + Duration::from_millis(args.get("waitMs").and_then(Json::as_u64).unwrap_or(0));
        let mut events = app.events.lock().unwrap();
        while events.len() < count {
            let now = Instant::now();
            if now >= deadline {
                break;
            }
            events = app.arrived.wait_timeout(events, deadline - now).unwrap().0;
        }
        Ok(Json::object([("events", Json::Array(std::mem::take(&mut *events)))]))
    }

    // The connection id of the WebSocket the driver's connection `conn` opened.
    fn connection(&self, args: &Json) -> Result<u64, String> {
        let app = self.app.as_ref().ok_or("no server is running")?;
        let conn = args.get("conn").and_then(Json::as_str).ok_or("conn is missing")?;
        let tags = app.tags.lock().unwrap();
        tags.iter()
            .filter(|(_, tag)| *tag == conn)
            .map(|(id, _)| *id)
            .max()
            .ok_or_else(|| format!("no request of {conn}"))
    }

    fn with_server<T>(&self, call: impl FnOnce(&Server) -> T) -> Result<T, String> {
        let slot = SLOT.read().unwrap_or_else(|poisoned| poisoned.into_inner());
        slot.as_ref().map(|(_, server)| call(server)).ok_or_else(|| "no server is running".to_owned())
    }

    fn call(&mut self, op: &str, args: &Json) -> Result<Json, (&'static str, String)> {
        let failed = |message: String| ("failed", message);
        match op {
            "describe" => {
                let mut capabilities = vec![Json::text("peer-admission"), Json::text("stop")];
                if std::net::TcpListener::bind("[::1]:0").is_ok() {
                    capabilities.push(Json::text("ipv6-loopback"));
                }
                Ok(Json::object([
                    ("protocol", Json::text(PROTOCOL)),
                    (
                        "implementation",
                        Json::object([
                            ("id", Json::text("rax-server")),
                            ("name", Json::text("runic-server (rax_server)")),
                            ("version", Json::text(env!("CARGO_PKG_VERSION"))),
                        ]),
                    ),
                    ("corpora", Json::Array(vec![Json::text("desktop-server/1")])),
                    ("roles", Json::Array(vec![Json::text("server")])),
                    ("features", Json::Array(Vec::new())),
                    ("capabilities", Json::Array(capabilities)),
                ]))
            }
            "server.start" => self.start(args).map_err(failed),
            "server.stop" => {
                self.stop();
                Ok(Json::object([]))
            }
            "server.events" => self.events(args).map_err(failed),
            "server.wsSend" => {
                let connection = self.connection(args).map_err(failed)?;
                let kind = match args.get("kind").and_then(Json::as_str) {
                    Some("text") => MessageKind::Text,
                    _ => MessageKind::Binary,
                };
                let data = unbase64(args.get("data").and_then(Json::as_str).unwrap_or("")).map_err(failed)?;
                self.with_server(|server| server.send(connection, kind, data, 0))
                    .map_err(failed)?
                    .map_err(|error| failed(error.to_string()))?;
                Ok(Json::object([]))
            }
            "server.wsClose" => {
                let connection = self.connection(args).map_err(failed)?;
                let code = args.get("code").and_then(Json::as_u64).unwrap_or(1000) as u16;
                let reason = args.get("reason").and_then(Json::as_str).unwrap_or("").to_owned();
                self.with_server(|server| server.close_websocket(connection, code, reason, 0))
                    .map_err(failed)?
                    .map_err(|error| failed(error.to_string()))?;
                Ok(Json::object([]))
            }
            "shutdown" => {
                self.stop();
                Ok(Json::object([]))
            }
            other => Err(("unsupported", format!("unknown op {other}"))),
        }
    }
}

fn main() {
    let directory = std::env::temp_dir().join(format!("rax-server-conformance-{}", std::process::id()));
    std::fs::create_dir_all(&directory).expect("a temporary directory");
    let file = directory.join("file.bin");
    std::fs::write(&file, FILE_CONTENT).expect("the test file");
    let mut adapter =
        Adapter { files: Arc::new(Files { file, missing: directory.join("missing.bin") }), app: None, generation: 0 };
    let stdin = std::io::stdin();
    let mut stdout = std::io::stdout();
    for line in stdin.lock().lines() {
        let Ok(line) = line else { break };
        if line.trim().is_empty() {
            continue;
        }
        let request = match json::parse(&line) {
            Ok(request) => request,
            Err(error) => {
                eprintln!("not a request: {error}");
                continue;
            }
        };
        let seq = request.get("seq").cloned().unwrap_or(Json::Null);
        let op = request.get("op").and_then(Json::as_str).unwrap_or("").to_owned();
        let args = request.get("args").cloned().unwrap_or(Json::Object(Default::default()));
        let response = match adapter.call(&op, &args) {
            Ok(ok) => Json::object([("seq", seq), ("ok", ok)]),
            Err((code, message)) => {
                eprintln!("{op}: {code}: {message}");
                Json::object([
                    ("seq", seq),
                    ("error", Json::object([("code", Json::text(code)), ("message", Json::text(message))])),
                ])
            }
        };
        let mut out = String::new();
        response.write(&mut out);
        out.push('\n');
        let _ = stdout.write_all(out.as_bytes());
        let _ = stdout.flush();
        if op == "shutdown" {
            break;
        }
    }
    adapter.stop();
    let _ = std::fs::remove_dir_all(directory);
}
