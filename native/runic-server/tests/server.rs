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
    // The host answers the close and releases the connection at once; the reply still completes the handshake.
    server.close_websocket(id, 1000, "bye".to_owned(), 0).unwrap();
    server.release_websocket(id);
    let reply = tokio::time::timeout(Duration::from_secs(5), socket.next()).await.expect("the close reply in time");
    assert!(matches!(reply, Some(Ok(Message::Close(_)))), "{reply:?}");
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
    // A host that releases the connection right after closing it, as a .NET WebSocket disposed after CloseOutputAsync
    // does, still gets its close frame written.
    server.release_websocket(id);
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

// A WebSocket client written against RFC 6455 directly, for frames tokio-tungstenite does not send.
async fn raw_websocket(server: &Server, events: &mut mpsc::UnboundedReceiver<Event>, port: u16) -> (TcpStream, u64) {
    let mut stream = TcpStream::connect(("127.0.0.1", port)).await.unwrap();
    let request = format!(
        "GET /socket HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n\
         Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nX-Runic-Test: 1\r\n\r\n"
    );
    stream.write_all(request.as_bytes()).await.unwrap();
    let Event::Request { id, websocket, .. } = next(events).await else { panic!("a request") };
    assert!(websocket);
    server.accept_websocket(id, None, HeaderMap::new()).unwrap();
    let mut head = Vec::new();
    while !head.ends_with(b"\r\n\r\n") {
        head.push(stream.read_u8().await.unwrap());
    }
    let head = String::from_utf8(head).unwrap().to_ascii_lowercase();
    assert!(head.starts_with("http/1.1 101"), "{head}");
    // RFC 6455 section 1.3's example key and its accept value.
    assert!(head.contains("sec-websocket-accept: s3pplmbitxaq9kygzzhzrbk+xoo="), "{head}");
    (stream, id)
}

fn client_frame(fin: bool, opcode: u8, payload: &[u8]) -> Vec<u8> {
    let mask = [0x37, 0xfa, 0x21, 0x3d];
    let mut frame = vec![if fin { 0x80 } else { 0 } | opcode];
    match payload.len() {
        length @ 0..=125 => frame.push(0x80 | length as u8),
        length @ 126..=0xffff => {
            frame.push(0x80 | 126);
            frame.extend_from_slice(&(length as u16).to_be_bytes());
        }
        length => {
            frame.push(0x80 | 127);
            frame.extend_from_slice(&(length as u64).to_be_bytes());
        }
    }
    frame.extend_from_slice(&mask);
    frame.extend(payload.iter().enumerate().map(|(index, byte)| byte ^ mask[index % 4]));
    frame
}

// The next server frame: its opcode and payload. Server frames are unmasked and, from this server, never fragmented.
async fn server_frame(stream: &mut TcpStream) -> (u8, Vec<u8>) {
    let read = async {
        let first = stream.read_u8().await.unwrap();
        assert_eq!(first & 0x80, 0x80, "a final frame");
        let length = match stream.read_u8().await.unwrap() {
            126 => u64::from(stream.read_u16().await.unwrap()),
            127 => stream.read_u64().await.unwrap(),
            length => {
                assert_eq!(length & 0x80, 0, "an unmasked frame");
                u64::from(length)
            }
        };
        let mut payload = vec![0; usize::try_from(length).unwrap()];
        stream.read_exact(&mut payload).await.unwrap();
        (first & 0x0f, payload)
    };
    tokio::time::timeout(Duration::from_secs(5), read).await.expect("a frame in time")
}

fn close_code(payload: &[u8]) -> u16 {
    u16::from_be_bytes([payload[0], payload[1]])
}

#[tokio::test]
async fn fragments_are_joined_pings_answered_and_an_unmasked_frame_closes_with_1002() {
    let (server, mut events) = start(ServerConfig::default());
    let (mut stream, id) = raw_websocket(&server, &mut events, server.port()).await;
    let mut frames = client_frame(false, 0x2, b"ab");
    frames.extend(client_frame(true, 0x9, b"ping"));
    frames.extend(client_frame(false, 0x0, b"cd"));
    frames.extend(client_frame(true, 0x0, b"ef"));
    stream.write_all(&frames).await.unwrap();
    assert_eq!(server_frame(&mut stream).await, (0xA, b"ping".to_vec()));
    let Event::Message { connection, kind, data, .. } = next(&mut events).await else { panic!("a message") };
    assert_eq!((connection, kind, data.as_slice()), (id, MessageKind::Binary, b"abcdef".as_slice()));

    stream.write_all(&[0x82, 0x01, b'x']).await.unwrap();
    let (opcode, payload) = server_frame(&mut stream).await;
    assert_eq!((opcode, close_code(&payload)), (0x8, 1002));
    let Event::Closed { connection, code, .. } = next(&mut events).await else { panic!("closed") };
    assert_eq!((connection, code), (id, None));
}

#[tokio::test]
async fn an_oversized_message_closes_with_1009_and_invalid_text_with_1007() {
    let (server, mut events) = start(ServerConfig { max_message_bytes: 8, ..ServerConfig::default() });
    let port = server.port();
    let (mut stream, id) = raw_websocket(&server, &mut events, port).await;
    // Within the limit per frame, over it as a message.
    let mut frames = client_frame(false, 0x2, b"12345");
    frames.extend(client_frame(true, 0x0, b"6789"));
    stream.write_all(&frames).await.unwrap();
    let (opcode, payload) = server_frame(&mut stream).await;
    assert_eq!((opcode, close_code(&payload)), (0x8, 1009));
    let Event::Closed { connection, code, .. } = next(&mut events).await else { panic!("closed") };
    assert_eq!((connection, code), (id, None));

    let (mut stream, id) = raw_websocket(&server, &mut events, port).await;
    stream.write_all(&client_frame(true, 0x1, &[0xff, 0xfe])).await.unwrap();
    let (opcode, payload) = server_frame(&mut stream).await;
    assert_eq!((opcode, close_code(&payload)), (0x8, 1007));
    let Event::Closed { connection, code, .. } = next(&mut events).await else { panic!("closed") };
    assert_eq!((connection, code), (id, None));
}

#[tokio::test]
async fn queued_messages_wait_for_the_flush_and_go_out_in_order() {
    let (server, mut events) = start(ServerConfig::default());
    let (mut stream, id) = raw_websocket(&server, &mut events, server.port()).await;
    // The client reads the handshake response before the server's connection task has taken over the stream; until
    // then that task holds the write, and a queued message is answered Accepted and written by it.
    while server.queue(id, MessageKind::Binary, b"one", 0).unwrap() != WriteOutcome::Flush {
        assert_eq!(server_frame(&mut stream).await, (0x2, b"one".to_vec()));
    }
    assert_eq!(server.queue(id, MessageKind::Text, b"two", 0).unwrap(), WriteOutcome::Accepted);
    // A send while the flush is outstanding queues behind it instead of writing first.
    assert_eq!(server.send(id, MessageKind::Binary, b"three", 0).unwrap(), WriteOutcome::Accepted);
    let mut byte = [0u8; 1];
    assert!(tokio::time::timeout(Duration::from_millis(200), stream.peek(&mut byte)).await.is_err(), "nothing yet");
    server.flush(id);
    assert_eq!(server_frame(&mut stream).await, (0x2, b"one".to_vec()));
    assert_eq!(server_frame(&mut stream).await, (0x1, b"two".to_vec()));
    assert_eq!(server_frame(&mut stream).await, (0x2, b"three".to_vec()));
    // With the write given back, the next send is written at once.
    assert_eq!(server.send(id, MessageKind::Binary, b"four", 0).unwrap(), WriteOutcome::Accepted);
    assert_eq!(server_frame(&mut stream).await, (0x2, b"four".to_vec()));
    // A release while a flush is outstanding leaves the queued close to the server.
    assert_eq!(server.queue(id, MessageKind::Binary, b"five", 0).unwrap(), WriteOutcome::Flush);
    server.close_websocket(id, 1000, String::new(), 0).unwrap();
    server.release_websocket(id);
    assert_eq!(server_frame(&mut stream).await, (0x2, b"five".to_vec()));
    let (opcode, payload) = server_frame(&mut stream).await;
    assert_eq!((opcode, close_code(&payload)), (0x8, 1000));
}

#[tokio::test]
async fn a_websocket_peer_that_reads_nothing_makes_sends_pending() {
    let (server, mut events) = start(ServerConfig::default());
    let (mut stream, id) = raw_websocket(&server, &mut events, server.port()).await;
    let message = vec![b'x'; 1 << 20];
    let mut sent = 0u64;
    let mut pending = None;
    for token in 1..=64u64 {
        let outcome = server.send(id, MessageKind::Binary, &message, token).unwrap();
        sent += 1;
        if outcome == WriteOutcome::Pending {
            pending = Some(token);
            break;
        }
    }
    let token = pending.expect("the queue fills while the peer reads nothing");
    let reader = tokio::spawn(async move {
        let mut received = 0u64;
        loop {
            match server_frame(&mut stream).await {
                (0x2, payload) => {
                    assert_eq!(payload.len(), 1 << 20);
                    received += 1;
                }
                (0x8, _) => return received,
                (opcode, _) => panic!("opcode {opcode}"),
            }
        }
    });
    let Event::Completed { token: completed, accepted } = next(&mut events).await else { panic!("a completion") };
    assert_eq!((completed, accepted), (token, true));
    server.close_websocket(id, 1000, String::new(), 0).unwrap();
    assert_eq!(reader.await.unwrap(), sent);
}

#[tokio::test]
async fn large_messages_arriving_in_pieces_are_unmasked_and_their_buffers_reused() {
    let (server, mut events) = start(ServerConfig::default());
    let (mut stream, id) = raw_websocket(&server, &mut events, server.port()).await;
    for round in 0..3u8 {
        let length = 100_003 - usize::from(round) * 7;
        let original: Vec<u8> = (0..length).map(|index| (index % 251) as u8 ^ round).collect();
        let frame = client_frame(true, 0x2, &original);
        // Pieces at odd offsets, so the payload arrives in reads that do not start on the mask's period.
        for piece in frame.chunks(33_331) {
            stream.write_all(piece).await.unwrap();
            stream.flush().await.unwrap();
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
        let Event::Message { connection, message, data, .. } = next(&mut events).await else { panic!("a message") };
        assert_eq!(connection, id);
        assert!(data == original, "round {round}: the payload differs");
        server.consume(id, message);
    }
    // Large sends reuse their buffers too.
    for round in 0..3u8 {
        let payload = vec![round; 70_000];
        server.queue(id, MessageKind::Binary, &payload, 0).unwrap();
        server.flush(id);
        assert_eq!(server_frame(&mut stream).await, (0x2, payload));
    }
}
