//! The server against real sockets: a raw HTTP/1.1 client and a tokio-tungstenite WebSocket client.

use std::io::Write as _;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use axum::http::{HeaderMap, HeaderValue, StatusCode, header};
use bytes::Bytes;
use futures_util::{SinkExt, StreamExt};
use runic_server::{Body, Handler, MessageKind, RequestHead, Server, ServerConfig, WebSocketClose, WriteOutcome};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpStream;
use tokio::sync::mpsc;
use tokio_tungstenite::tungstenite::Message;
use tokio_tungstenite::tungstenite::client::IntoClientRequest;

#[derive(Debug)]
enum Event {
    Request {
        id: u64,
        method: String,
        target: String,
        headers: HeaderMap,
        websocket: bool,
    },
    #[allow(dead_code)]
    Aborted(u64),
    Message {
        connection: u64,
        message: u64,
        kind: MessageKind,
        data: Vec<u8>,
    },
    Closed {
        connection: u64,
        code: Option<u16>,
        #[allow(dead_code)]
        reason: String,
    },
    Completed {
        token: u64,
        accepted: bool,
    },
}

struct Recorder(Mutex<mpsc::UnboundedSender<Event>>, Mutex<std::collections::HashSet<u64>>);

impl Handler for Recorder {
    fn request(&self, head: &RequestHead<'_>) {
        // Other processes on the machine may probe a freshly bound loopback port; only the tests' requests count.
        if !head.headers.contains_key("x-runic-test") {
            return;
        }
        self.1.lock().unwrap().insert(head.id);
        let _ = self.0.lock().unwrap().send(Event::Request {
            id: head.id,
            method: head.method.to_string(),
            target: head.target.to_owned(),
            headers: head.headers.clone(),
            websocket: head.websocket,
        });
    }
    fn request_aborted(&self, id: u64) {
        if !self.1.lock().unwrap().contains(&id) {
            return;
        }
        let _ = self.0.lock().unwrap().send(Event::Aborted(id));
    }
    fn message(&self, connection: u64, message: u64, kind: MessageKind, data: &[u8]) {
        let _ = self.0.lock().unwrap().send(Event::Message { connection, message, kind, data: data.to_vec() });
    }
    fn closed(&self, connection: u64, close: Option<WebSocketClose<'_>>) {
        let _ = self.0.lock().unwrap().send(Event::Closed {
            connection,
            code: close.as_ref().map(|close| close.code),
            reason: close.map(|close| close.reason.to_owned()).unwrap_or_default(),
        });
    }
    fn completed(&self, token: u64, accepted: bool) {
        let _ = self.0.lock().unwrap().send(Event::Completed { token, accepted });
    }
}

fn start(config: ServerConfig) -> (Server, mpsc::UnboundedReceiver<Event>) {
    let (sender, receiver) = mpsc::unbounded_channel();
    let server =
        Server::start(config, Arc::new(Recorder(Mutex::new(sender), Mutex::default()))).expect("server starts");
    (server, receiver)
}

async fn next(events: &mut mpsc::UnboundedReceiver<Event>) -> Event {
    tokio::time::timeout(Duration::from_secs(5), events.recv()).await.expect("an event in time").expect("events open")
}

async fn exchange(port: u16, request: String) -> String {
    let mut stream = TcpStream::connect(("127.0.0.1", port)).await.unwrap();
    stream.write_all(request.as_bytes()).await.unwrap();
    let mut response = Vec::new();
    tokio::time::timeout(Duration::from_secs(5), stream.read_to_end(&mut response))
        .await
        .expect("response in time")
        .unwrap();
    String::from_utf8_lossy(&response).into_owned()
}

fn get(port: u16, path: &str, extra: &str) -> String {
    format!("GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\nX-Runic-Test: 1\r\n{extra}\r\n")
}

#[tokio::test]
async fn a_foreign_host_is_refused_before_the_handler() {
    let (server, mut events) = start(ServerConfig::default());
    let port = server.port();
    let response = exchange(
        port,
        format!("GET / HTTP/1.1\r\nHost: rebound.example:{port}\r\nConnection: close\r\nX-Runic-Test: 1\r\n\r\n"),
    )
    .await;
    assert!(response.starts_with("HTTP/1.1 421"), "{response}");
    assert!(response.to_ascii_lowercase().contains("x-content-type-options: nosniff"), "{response}");
    let response = exchange(
        port,
        format!(
            "GET / HTTP/1.1\r\nHost: localhost:{}\r\nConnection: close\r\nX-Runic-Test: 1\r\n\r\n",
            port.wrapping_add(1)
        ),
    )
    .await;
    assert!(response.starts_with("HTTP/1.1 421"), "{response}");
    assert!(events.try_recv().is_err(), "the handler saw no request");
}

#[tokio::test]
async fn a_streamed_body_arrives_in_order_and_the_host_headers_are_kept() {
    let (server, mut events) = start(ServerConfig::default());
    let port = server.port();
    let client = tokio::spawn(exchange(port, get(port, "/a%20b?x=1", "")));
    let Event::Request { id, method, target, websocket, .. } = next(&mut events).await else { panic!("a request") };
    assert_eq!((method.as_str(), target.as_str(), websocket), ("GET", "/a%20b?x=1", false));
    let mut headers = HeaderMap::new();
    headers.insert(header::CONTENT_TYPE, HeaderValue::from_static("text/plain"));
    headers.insert(header::X_FRAME_OPTIONS, HeaderValue::from_static("DENY"));
    headers.append(header::SET_COOKIE, HeaderValue::from_static("a=1"));
    headers.append(header::SET_COOKIE, HeaderValue::from_static("b=2"));
    server.respond(id, StatusCode::OK, headers, Body::Stream).unwrap();
    for chunk in ["one ", "two ", "three"] {
        assert_eq!(server.write(id, Bytes::from(chunk), 0).unwrap(), WriteOutcome::Accepted);
    }
    server.end(id).unwrap();
    let response = client.await.unwrap();
    let lower = response.to_ascii_lowercase();
    assert!(response.starts_with("HTTP/1.1 200"), "{response}");
    assert!(lower.contains("x-frame-options: deny"), "the host's value wins: {response}");
    assert!(lower.contains("set-cookie: a=1") && lower.contains("set-cookie: b=2"), "{response}");
    assert!(response.contains("one ") && response.ends_with("three") || response.contains("three\r\n"), "{response}");
}

#[tokio::test]
async fn a_full_body_queue_reports_pending_writes() {
    let (server, mut events) = start(ServerConfig::default());
    let port = server.port();
    // A client that sends the request and reads nothing until told to.
    let mut stream = TcpStream::connect(("127.0.0.1", port)).await.unwrap();
    stream.write_all(get(port, "/big", "").as_bytes()).await.unwrap();
    let Event::Request { id, .. } = next(&mut events).await else { panic!("a request") };
    server.respond(id, StatusCode::OK, HeaderMap::new(), Body::Stream).unwrap();
    let chunk = Bytes::from(vec![b'x'; 1 << 20]);
    let mut pending = None;
    for token in 1..=64u64 {
        if server.write(id, chunk.clone(), token).unwrap() == WriteOutcome::Pending {
            pending = Some(token);
            break;
        }
    }
    let token = pending.expect("the queue fills while the client reads nothing");
    let reader = tokio::spawn(async move {
        let mut sink = Vec::new();
        let _ = stream.read_to_end(&mut sink).await;
        sink.len()
    });
    let Event::Completed { token: completed, accepted } = next(&mut events).await else { panic!("a completion") };
    assert_eq!((completed, accepted), (token, true));
    server.end(id).unwrap();
    assert!(reader.await.unwrap() > (token as usize) << 20);
}

#[tokio::test]
async fn a_requester_that_leaves_before_the_answer_is_reported() {
    let (server, mut events) = start(ServerConfig::default());
    let port = server.port();
    {
        let mut stream = TcpStream::connect(("127.0.0.1", port)).await.unwrap();
        stream.write_all(get(port, "/slow", "").as_bytes()).await.unwrap();
        let Event::Request { .. } = next(&mut events).await else { panic!("a request") };
    }
    let Event::Aborted(_) = next(&mut events).await else { panic!("an abort") };
    drop(server);
}

#[tokio::test]
async fn a_file_body_answers_one_byte_range() {
    let directory = std::env::temp_dir().join(format!("runic-server-test-{}", std::process::id()));
    std::fs::create_dir_all(&directory).unwrap();
    let path = directory.join("media.bin");
    std::fs::File::create(&path).unwrap().write_all(b"0123456789").unwrap();
    let (server, mut events) = start(ServerConfig::default());
    let port = server.port();
    for (range, status, body, content_range) in [
        ("Range: bytes=2-4\r\n", "206", "234", Some("bytes 2-4/10")),
        ("Range: bytes=-3\r\n", "206", "789", Some("bytes 7-9/10")),
        ("Range: bytes=20-\r\n", "416", "", Some("bytes */10")),
        ("", "200", "0123456789", None),
    ] {
        let client = tokio::spawn(exchange(port, get(port, "/media.bin", range)));
        let Event::Request { id, .. } = next(&mut events).await else { panic!("a request") };
        let mut headers = HeaderMap::new();
        headers.insert(header::CONTENT_TYPE, HeaderValue::from_static("application/octet-stream"));
        headers.insert(header::CONTENT_LENGTH, HeaderValue::from_static("10"));
        server.respond(id, StatusCode::OK, headers, Body::File(path.clone())).unwrap();
        let response = client.await.unwrap();
        assert!(response.starts_with(&format!("HTTP/1.1 {status}")), "{range}: {response}");
        let (head, received) = response.split_once("\r\n\r\n").unwrap();
        assert_eq!(received, body, "{range}");
        if let Some(content_range) = content_range {
            assert!(head.to_ascii_lowercase().contains(&format!("content-range: {content_range}")), "{head}");
        }
        assert!(head.to_ascii_lowercase().contains("accept-ranges: bytes"), "{head}");
    }
    let _ = std::fs::remove_dir_all(directory);
}

#[tokio::test]
async fn a_websocket_exchanges_messages_within_its_receive_window_and_closes() {
    let (server, mut events) = start(ServerConfig { receive_window: 2, ..ServerConfig::default() });
    let port = server.port();
    let mut request = format!("ws://127.0.0.1:{port}/_webui_ws_connect").into_client_request().unwrap();
    request.headers_mut().insert("X-Runic-Test", HeaderValue::from_static("1"));
    request.headers_mut().insert("Sec-WebSocket-Protocol", HeaderValue::from_static("other, runic-desktop.secret"));
    request.headers_mut().insert("Origin", HeaderValue::from_str(&format!("http://127.0.0.1:{port}")).unwrap());
    let client = tokio::spawn(async move {
        let stream = TcpStream::connect(("127.0.0.1", port)).await.unwrap();
        tokio_tungstenite::client_async(request, stream).await
    });
    let Event::Request { id, websocket, headers, .. } = next(&mut events).await else { panic!("a request") };
    assert!(websocket);
    assert!(headers.get("origin").is_some());
    server.accept_websocket(id, Some("runic-desktop.secret".to_owned()), HeaderMap::new()).unwrap();
    // A message sent right after accepting is delivered after the handshake.
    assert_eq!(server.send(id, MessageKind::Binary, Bytes::from_static(b"hello"), 0).unwrap(), WriteOutcome::Accepted);
    let (mut socket, response) = client.await.unwrap().expect("handshake");
    assert_eq!(response.headers().get("sec-websocket-protocol").unwrap(), "runic-desktop.secret");
    assert_eq!(socket.next().await.unwrap().unwrap(), Message::Binary(Bytes::from_static(b"hello")));

    for index in 0..3u8 {
        socket.send(Message::Binary(Bytes::from(vec![index]))).await.unwrap();
    }
    let mut delivered = Vec::new();
    for _ in 0..2 {
        let Event::Message { connection, message, kind, data } = next(&mut events).await else { panic!("a message") };
        assert_eq!((connection, kind), (id, MessageKind::Binary));
        delivered.push((message, data));
    }
    // The window of two is full: the third message waits for a consume.
    assert!(tokio::time::timeout(Duration::from_millis(200), events.recv()).await.is_err());
    server.consume(id, delivered[0].0);
    let Event::Message { data, .. } = next(&mut events).await else { panic!("the third message") };
    assert_eq!(data, vec![2]);
    assert_eq!(delivered.iter().map(|(_, data)| data[0]).collect::<Vec<_>>(), vec![0, 1]);

    socket.send(Message::Close(None)).await.unwrap();
    // The window must have room for the close frame to be read.
    server.consume(id, delivered[1].0);
    let Event::Closed { connection, code, .. } = next(&mut events).await else { panic!("closed") };
    assert_eq!((connection, code), (id, Some(1005)));
    server.close_websocket(id, 1000, "bye".to_owned(), 0).unwrap();
    server.release_websocket(id);
    drop(server);
}

#[tokio::test]
async fn a_host_close_reaches_the_client_after_queued_messages() {
    let (server, mut events) = start(ServerConfig::default());
    let port = server.port();
    let mut request = format!("ws://127.0.0.1:{port}/socket").into_client_request().unwrap();
    request.headers_mut().insert("X-Runic-Test", HeaderValue::from_static("1"));
    let client = tokio::spawn(async move {
        let stream = TcpStream::connect(("127.0.0.1", port)).await.unwrap();
        tokio_tungstenite::client_async(request, stream).await
    });
    let Event::Request { id, .. } = next(&mut events).await else { panic!("a request") };
    server.accept_websocket(id, None, HeaderMap::new()).unwrap();
    server.send(id, MessageKind::Text, Bytes::from_static(b"last"), 0).unwrap();
    server.close_websocket(id, 1001, "going away".to_owned(), 0).unwrap();
    assert!(server.send(id, MessageKind::Text, Bytes::from_static(b"late"), 0).is_err());
    let (mut socket, _) = client.await.unwrap().expect("handshake");
    assert_eq!(socket.next().await.unwrap().unwrap(), Message::Text("last".into()));
    let Message::Close(Some(frame)) = socket.next().await.unwrap().unwrap() else { panic!("a close frame") };
    assert_eq!((u16::from(frame.code), frame.reason.as_str()), (1001, "going away"));
    // Reading on flushes the client's automatic close reply.
    let drained = tokio::spawn(async move { while socket.next().await.is_some() {} });
    let event = next(&mut events).await;
    let Event::Closed { connection, code, .. } = event else { panic!("closed: {event:?}") };
    assert_eq!(connection, id);
    // The client's automatic reply echoes the code.
    assert_eq!(code, Some(1001));
    drained.await.unwrap();
    server.release_websocket(id);
}

#[tokio::test]
async fn stopping_fails_waiting_requests_and_starts_no_callbacks() {
    let (server, mut events) = start(ServerConfig::default());
    let port = server.port();
    let client = tokio::spawn(exchange(port, get(port, "/", "")));
    let Event::Request { .. } = next(&mut events).await else { panic!("a request") };
    tokio::task::spawn_blocking(move || server.stop()).await.unwrap();
    let response = client.await.unwrap();
    assert!(response.is_empty() || response.starts_with("HTTP/1.1 503"), "{response}");
    while let Ok(event) = events.try_recv() {
        assert!(matches!(event, Event::Aborted(_)), "{event:?}");
    }
}
