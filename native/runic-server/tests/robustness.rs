//! The WebSocket frame parser against random and mutated frame streams (any local process reaches it before the host
//! authenticates anything). A seeded generator makes every run the same. For each stream the server must not panic,
//! must not hold more than `max_message_bytes` plus a small constant while it reads, must end the connection with a
//! close frame whose code RFC 6455 prescribes or by closing it, and must go on serving another connection.
//!
//! This is its own test binary: it counts the process's heap through its global allocator and its panics through the
//! panic hook, which other tests in the same process would disturb.

use std::alloc::{GlobalAlloc, Layout, System};
use std::sync::atomic::{AtomicIsize, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use axum::http::HeaderMap;
use runic_server::{Handler, MessageKind, RequestHead, Server, ServerConfig, WebSocketClose};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpStream;
use tokio::sync::mpsc;

const LIMIT: usize = 100_000;
// What a connection may hold beyond one message while it reads: its read buffer, a frame head, a queued close or pong,
// and the runtime's own bookkeeping.
const SLACK: isize = 64 << 10;
const STREAMS: u64 = 1000;
const SEED: u64 = 0x5eed_0f03_2026_1031;

struct Counting;

static LIVE: AtomicIsize = AtomicIsize::new(0);
static PEAK: AtomicIsize = AtomicIsize::new(0);
static PANICS: AtomicUsize = AtomicUsize::new(0);

fn grew(bytes: usize) {
    let now = LIVE.fetch_add(bytes as isize, Ordering::Relaxed) + bytes as isize;
    PEAK.fetch_max(now, Ordering::Relaxed);
}

// Live heap bytes and their peak. A reallocation counts as its size change.
unsafe impl GlobalAlloc for Counting {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        let pointer = unsafe { System.alloc(layout) };
        if !pointer.is_null() {
            grew(layout.size());
        }
        pointer
    }

    unsafe fn alloc_zeroed(&self, layout: Layout) -> *mut u8 {
        let pointer = unsafe { System.alloc_zeroed(layout) };
        if !pointer.is_null() {
            grew(layout.size());
        }
        pointer
    }

    unsafe fn dealloc(&self, pointer: *mut u8, layout: Layout) {
        unsafe { System.dealloc(pointer, layout) };
        LIVE.fetch_sub(layout.size() as isize, Ordering::Relaxed);
    }

    unsafe fn realloc(&self, pointer: *mut u8, layout: Layout, size: usize) -> *mut u8 {
        let moved = unsafe { System.realloc(pointer, layout, size) };
        if !moved.is_null() {
            if size >= layout.size() {
                grew(size - layout.size());
            } else {
                LIVE.fetch_sub((layout.size() - size) as isize, Ordering::Relaxed);
            }
        }
        moved
    }
}

#[global_allocator]
static ALLOCATOR: Counting = Counting;

// SplitMix64.
struct Random(u64);

impl Random {
    fn next(&mut self) -> u64 {
        self.0 = self.0.wrapping_add(0x9e37_79b9_7f4a_7c15);
        let mut value = self.0;
        value = (value ^ (value >> 30)).wrapping_mul(0xbf58_476d_1ce4_e5b9);
        value = (value ^ (value >> 27)).wrapping_mul(0x94d0_49bb_1331_11eb);
        value ^ (value >> 31)
    }

    fn below(&mut self, bound: u64) -> u64 {
        self.next() % bound
    }

    fn size(&mut self, bound: usize) -> usize {
        self.below(bound as u64) as usize
    }

    fn chance(&mut self, percent: u64) -> bool {
        self.below(100) < percent
    }

    fn some_bytes(&mut self, bound: usize) -> Vec<u8> {
        let length = self.size(bound);
        self.bytes(length)
    }

    fn bytes(&mut self, length: usize) -> Vec<u8> {
        (0..length).map(|_| self.next() as u8).collect()
    }

    // A small length most of the time, sometimes one near the limit.
    fn length(&mut self) -> usize {
        match self.below(10) {
            0 => LIMIT - self.size(LIMIT / 2),
            1..=3 => self.size(70_000),
            _ => self.size(300),
        }
    }
}

#[derive(Clone, Copy)]
enum Length {
    Shortest,
    Wide16,
    Wide64,
    // Declares this length whatever the payload is.
    Declared(u64),
}

struct Frame<'a> {
    fin: bool,
    rsv: u8,
    opcode: u8,
    masked: bool,
    length: Length,
    payload: &'a [u8],
}

impl Frame<'_> {
    fn new(fin: bool, opcode: u8, payload: &[u8]) -> Frame<'_> {
        Frame { fin, rsv: 0, opcode, masked: true, length: Length::Shortest, payload }
    }

    fn write(&self, random: &mut Random, out: &mut Vec<u8>) {
        out.push(if self.fin { 0x80 } else { 0 } | (self.rsv & 7) << 4 | (self.opcode & 0x0f));
        let mask_bit = if self.masked { 0x80 } else { 0 };
        let length = self.payload.len() as u64;
        match self.length {
            Length::Shortest if length < 126 => out.push(mask_bit | length as u8),
            Length::Shortest if length <= 0xffff => {
                out.push(mask_bit | 126);
                out.extend_from_slice(&(length as u16).to_be_bytes());
            }
            Length::Wide16 if length <= 0xffff => {
                out.push(mask_bit | 126);
                out.extend_from_slice(&(length as u16).to_be_bytes());
            }
            Length::Declared(declared) => {
                out.push(mask_bit | 127);
                out.extend_from_slice(&declared.to_be_bytes());
            }
            _ => {
                out.push(mask_bit | 127);
                out.extend_from_slice(&length.to_be_bytes());
            }
        }
        if self.masked {
            let mask = (random.next() as u32).to_be_bytes();
            out.extend_from_slice(&mask);
            out.extend(self.payload.iter().enumerate().map(|(index, byte)| byte ^ mask[index % 4]));
        } else {
            out.extend_from_slice(self.payload);
        }
    }
}

// What the server must do with a stream.
#[derive(Debug, PartialEq)]
enum Expect {
    // Deliver these messages, then answer the stream's close with 1000.
    Echo,
    // Deliver these messages, then close with this code.
    Close(u16),
    // Deliver these messages and close the connection, with or without a close frame.
    Closing,
    // Anything RFC 6455 allows: a mutated or random stream.
    Any,
}

struct Stream {
    name: &'static str,
    bytes: Vec<u8>,
    // Kind, length and FNV-1a hash of every message the server must deliver, in order.
    messages: Vec<(MessageKind, usize, u64)>,
    expect: Expect,
}

fn fnv(data: &[u8]) -> u64 {
    data.iter().fold(0xcbf2_9ce4_8422_2325, |hash, byte| (hash ^ u64::from(*byte)).wrapping_mul(0x0100_0000_01b3))
}

// A message the server must deliver, with control frames between its fragments.
fn valid_message(random: &mut Random, out: &mut Vec<u8>, messages: &mut Vec<(MessageKind, usize, u64)>) {
    let text = random.chance(40);
    let length = random.length();
    let data = if text {
        // ASCII and three-byte characters, so fragments split characters.
        let mut data = Vec::with_capacity(length);
        while data.len() + 3 <= length {
            if random.chance(20) {
                data.extend_from_slice("€".as_bytes());
            } else {
                data.push(b'a' + random.below(26) as u8);
            }
        }
        data
    } else {
        random.bytes(length)
    };
    let opcode = if text { 0x1 } else { 0x2 };
    let fragments = if random.chance(50) { 1 } else { 2 + random.size(4) };
    let mut cuts: Vec<usize> = (1..fragments).map(|_| random.size(data.len() + 1)).collect();
    cuts.push(0);
    cuts.push(data.len());
    cuts.sort_unstable();
    for (index, window) in cuts.windows(2).enumerate() {
        let last = index == cuts.len() - 2;
        let mut frame = Frame::new(last, if index == 0 { opcode } else { 0x0 }, &data[window[0]..window[1]]);
        frame.length = match random.below(8) {
            0 => Length::Wide16,
            1 => Length::Wide64,
            _ => Length::Shortest,
        };
        frame.write(random, out);
        if !last && random.chance(30) {
            let control = random.some_bytes(126);
            Frame::new(true, if random.chance(50) { 0x9 } else { 0xA }, &control).write(random, out);
        }
    }
    messages.push((if text { MessageKind::Text } else { MessageKind::Binary }, data.len(), fnv(&data)));
}

fn close_frame(random: &mut Random, out: &mut Vec<u8>) {
    let payload: Vec<u8> = match random.below(4) {
        0 => Vec::new(),
        1 => 1000u16.to_be_bytes().to_vec(),
        2 => [&4321u16.to_be_bytes()[..], "bye ✓".as_bytes()].concat(),
        _ => 3000u16.to_be_bytes().to_vec(),
    };
    Frame::new(true, 0x8, &payload).write(random, out);
}

fn generate(random: &mut Random, index: u64) -> Stream {
    let mut bytes = Vec::new();
    let mut messages = Vec::new();
    for _ in 0..random.below(3) {
        valid_message(random, &mut bytes, &mut messages);
    }
    let (name, expect) = match index % 16 {
        0 | 1 => {
            valid_message(random, &mut bytes, &mut messages);
            close_frame(random, &mut bytes);
            ("valid messages and a close", Expect::Echo)
        }
        2 => {
            // A head cut anywhere before its end.
            let mut frame = Vec::new();
            let length = [10, 300, 70_000][random.size(3)];
            Frame::new(true, 0x2, &random.bytes(length)).write(random, &mut frame);
            let head = if length < 126 {
                6
            } else if length <= 0xffff {
                8
            } else {
                14
            };
            bytes.extend_from_slice(&frame[..1 + random.size(head - 1)]);
            ("a truncated head", Expect::Closing)
        }
        3 => {
            let declared = LIMIT as u64 + 1 + random.below(1 << 40);
            Frame { length: Length::Declared(declared), ..Frame::new(true, 0x2, &[]) }.write(random, &mut bytes);
            ("a 64-bit length over the limit", Expect::Close(1009))
        }
        4 => {
            let declared = (1 << 63) | random.below(1 << 20);
            Frame { length: Length::Declared(declared), ..Frame::new(true, 0x2, &[]) }.write(random, &mut bytes);
            ("a 64-bit length with its top bit set", Expect::Close(1002))
        }
        5 => {
            let rsv = 1 + random.below(7) as u8;
            let payload = random.some_bytes(50);
            Frame { rsv, ..Frame::new(true, 0x2, &payload) }.write(random, &mut bytes);
            ("RSV bits", Expect::Close(1002))
        }
        6 => {
            let payload = random.some_bytes(50);
            Frame { masked: false, ..Frame::new(true, if random.chance(50) { 0x2 } else { 0x9 }, &payload) }
                .write(random, &mut bytes);
            ("an unmasked frame", Expect::Close(1002))
        }
        7 => {
            if random.chance(50) {
                // A fragmented ping.
                Frame::new(false, 0x9, b"ping").write(random, &mut bytes);
            } else {
                // A control frame over 125 bytes.
                let length = 126 + random.size(200);
                let payload = random.bytes(length);
                Frame::new(true, if random.chance(50) { 0x9 } else { 0x8 }, &payload).write(random, &mut bytes);
            }
            ("a malformed control frame", Expect::Close(1002))
        }
        8 => {
            Frame::new(random.chance(50), 0x0, &random.some_bytes(100)).write(random, &mut bytes);
            ("a continuation without a start", Expect::Close(1002))
        }
        9 => {
            Frame::new(false, 0x2, &random.some_bytes(100)).write(random, &mut bytes);
            if random.chance(50) {
                Frame::new(true, 0x9, b"between").write(random, &mut bytes);
            }
            Frame::new(true, if random.chance(50) { 0x1 } else { 0x2 }, b"next").write(random, &mut bytes);
            ("a start without its continuation", Expect::Close(1002))
        }
        10 => {
            // Fragments within the limit one by one, over it together.
            let part = LIMIT / 3 + 1;
            for index in 0..4 {
                Frame::new(false, if index == 0 { 0x2 } else { 0x0 }, &random.bytes(part)).write(random, &mut bytes);
            }
            ("oversized fragments", Expect::Close(1009))
        }
        11 => {
            // "€" is E2 82 AC: a fragment ends inside it, and the next does not finish it.
            let first = [&b"price "[..], &[0xE2, 0x82]].concat();
            Frame::new(false, 0x1, &first).write(random, &mut bytes);
            Frame::new(true, 0x0, &[0x28, b'x']).write(random, &mut bytes);
            ("invalid UTF-8 split across fragments", Expect::Close(1007))
        }
        12 => {
            let codes = [0u16, 999, 1004, 1005, 1006, 1015, 1016, 2999, 5000, 65535];
            if random.chance(20) {
                Frame::new(true, 0x8, &[0x03]).write(random, &mut bytes);
            } else {
                Frame::new(true, 0x8, &codes[random.size(codes.len())].to_be_bytes()).write(random, &mut bytes);
            }
            ("a close with a bad code", Expect::Close(1002))
        }
        13 => {
            let payload = [&1000u16.to_be_bytes()[..], &[b'o', 0xC3, 0x28]].concat();
            Frame::new(true, 0x8, &payload).write(random, &mut bytes);
            ("a close with a reason that is not UTF-8", Expect::Close(1007))
        }
        14 => {
            let opcode = [0x3, 0x4, 0x5, 0x6, 0x7, 0xB, 0xC, 0xD, 0xE, 0xF][random.size(10)];
            Frame::new(true, opcode, &random.some_bytes(20)).write(random, &mut bytes);
            ("a reserved opcode", Expect::Close(1002))
        }
        _ => {
            // A valid stream with flipped, cut, repeated or random bytes, or random bytes alone.
            valid_message(random, &mut bytes, &mut messages);
            close_frame(random, &mut bytes);
            messages.clear();
            if random.chance(20) {
                bytes = random.some_bytes(4096);
            } else {
                for _ in 0..1 + random.below(8) {
                    if bytes.is_empty() {
                        break;
                    }
                    let at = random.size(bytes.len());
                    match random.below(4) {
                        0 => bytes[at] ^= 1 << random.below(8),
                        1 => bytes[at] = random.next() as u8,
                        2 => bytes.truncate(at),
                        _ => {
                            let end = (at + random.size(64)).min(bytes.len());
                            let copy = bytes[at..end].to_vec();
                            bytes.splice(at..at, copy);
                        }
                    }
                }
            }
            ("a mutated or random stream", Expect::Any)
        }
    };
    Stream { name, bytes, messages, expect }
}

enum Event {
    Request(u64),
    Message { connection: u64, message: u64, kind: MessageKind, length: usize, hash: u64 },
    Closed { connection: u64, code: Option<u16> },
}

struct Recorder(Mutex<mpsc::UnboundedSender<Event>>);

impl Handler for Recorder {
    fn request(&self, head: &RequestHead<'_>) {
        // Other processes may probe a freshly bound loopback port; only this test's requests count.
        if head.headers.contains_key("x-runic-test") {
            let _ = self.0.lock().unwrap().send(Event::Request(head.id));
        }
    }
    fn request_aborted(&self, _id: u64) {}
    fn message(&self, connection: u64, message: u64, kind: MessageKind, data: &[u8]) {
        let event = Event::Message { connection, message, kind, length: data.len(), hash: fnv(data) };
        let _ = self.0.lock().unwrap().send(event);
    }
    fn closed(&self, connection: u64, close: Option<WebSocketClose<'_>>) {
        let _ = self.0.lock().unwrap().send(Event::Closed { connection, code: close.map(|close| close.code) });
    }
    fn completed(&self, _token: u64, _accepted: bool) {}
}

async fn connect(server: &Server, events: &mut mpsc::UnboundedReceiver<Event>) -> (TcpStream, u64) {
    let port = server.port();
    let mut stream = TcpStream::connect(("127.0.0.1", port)).await.unwrap();
    let request = format!(
        "GET /socket HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n\
         Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nX-Runic-Test: 1\r\n\r\n"
    );
    stream.write_all(request.as_bytes()).await.unwrap();
    let id = loop {
        match tokio::time::timeout(Duration::from_secs(5), events.recv()).await.expect("a request").unwrap() {
            Event::Request(id) => break id,
            Event::Message { connection, message, .. } => server.consume(connection, message),
            Event::Closed { .. } => {}
        }
    };
    server.accept_websocket(id, None, HeaderMap::new()).unwrap();
    let mut head = [0u8; 512];
    let mut length = 0;
    while !head[..length].ends_with(b"\r\n\r\n") {
        length += stream.read(&mut head[length..length + 1]).await.unwrap();
    }
    assert!(head.starts_with(b"HTTP/1.1 101"));
    (stream, id)
}

// The server's frames on a connection: unmasked, and here only pongs and one close. Parsed from a fixed buffer, so
// reading allocates nothing.
#[derive(Default)]
struct Received {
    buffer: Vec<u8>,
    close: Option<u16>,
    frames_after_close: usize,
}

impl Received {
    fn take(&mut self, data: &[u8]) {
        self.buffer.extend_from_slice(data);
        loop {
            let [first, second, ..] = self.buffer[..] else { return };
            assert_eq!(second & 0x80, 0, "server frames are unmasked");
            assert!(second < 126, "the server sent a frame over 125 bytes: {first:#x}");
            let (length, head) = (second as usize, 2);
            if self.buffer.len() < head + length {
                return;
            }
            let opcode = first & 0x0f;
            assert!(first & 0x80 != 0 && matches!(opcode, 0x8 | 0xA), "unexpected server frame {first:#x}");
            if self.close.is_some() {
                self.frames_after_close += 1;
            }
            if opcode == 0x8 {
                let payload = &self.buffer[head..head + length];
                assert!(payload.len() >= 2, "the server's close names a code");
                self.close.get_or_insert(u16::from_be_bytes([payload[0], payload[1]]));
            }
            self.buffer.drain(..head + length);
        }
    }
}

struct Outcome {
    close: Option<u16>,
    closed_event: Option<Option<u16>>,
    delivered: Vec<(MessageKind, usize, u64)>,
    peak: isize,
}

async fn exercise(server: &Server, events: &mut mpsc::UnboundedReceiver<Event>, bytes: &[u8]) -> Outcome {
    let (mut stream, id) = connect(server, events).await;
    let (mut reader, mut writer) = stream.split();
    let mut received = Received { buffer: Vec::with_capacity(4096), ..Received::default() };
    let mut delivered = Vec::with_capacity(8);
    let mut closed_event = None;
    let mut scratch = [0u8; 2048];
    let mut written = 0;
    let mut shut = false;
    let mut ended = false;
    let base = LIVE.load(Ordering::Relaxed);
    PEAK.store(base, Ordering::Relaxed);
    let deadline = tokio::time::sleep(Duration::from_secs(10));
    tokio::pin!(deadline);
    while !ended || closed_event.is_none() {
        tokio::select! {
            // The stream, then the end of the client's side.
            result = async {
                if written < bytes.len() { writer.write(&bytes[written..]).await } else { writer.shutdown().await.map(|()| 0) }
            }, if !shut => match result {
                Ok(count) if count > 0 => written += count,
                _ if written < bytes.len() => written = bytes.len(),
                _ => shut = true,
            },
            result = reader.read(&mut scratch), if !ended => match result {
                Ok(0) | Err(_) => ended = true,
                Ok(count) => received.take(&scratch[..count]),
            },
            event = events.recv() => match event.unwrap() {
                Event::Message { connection, message, kind, length, hash } => {
                    server.consume(connection, message);
                    if connection == id {
                        delivered.push((kind, length, hash));
                    }
                }
                Event::Closed { connection, code } if connection == id => closed_event = Some(code),
                _ => {}
            },
            () = &mut deadline => panic!("the connection did not end: {} of {} bytes written, ended {ended}", written, bytes.len()),
        }
    }
    let peak = PEAK.load(Ordering::Relaxed) - base;
    assert_eq!(received.frames_after_close, 0, "the server sent frames after its close");
    Outcome { close: received.close, closed_event, delivered, peak }
}

#[tokio::test]
async fn random_and_mutated_frame_streams_end_in_a_close_without_panics_or_unbounded_memory() {
    let default_hook = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
        PANICS.fetch_add(1, Ordering::SeqCst);
        default_hook(info);
    }));
    let (sender, mut events) = mpsc::unbounded_channel();
    let config = ServerConfig { max_message_bytes: LIMIT, receive_window: 1, ..ServerConfig::default() };
    let server = Arc::new(Server::start(config, Arc::new(Recorder(Mutex::new(sender)))).expect("server starts"));
    // A connection that stays open through every stream and must keep working.
    let (mut sentinel, sentinel_id) = connect(&server, &mut events).await;
    // RUNIC_ROBUSTNESS_SEED explores other streams locally; CI runs the fixed seed.
    let seed = std::env::var("RUNIC_ROBUSTNESS_SEED").ok().and_then(|seed| seed.parse().ok()).unwrap_or(SEED);
    let mut random = Random(seed);
    let mut largest_peak = 0;
    let mut closes = std::collections::BTreeMap::<String, usize>::new();
    for index in 0..STREAMS {
        let stream = generate(&mut random, index);
        let outcome = exercise(&server, &mut events, &stream.bytes).await;
        let context = format!("stream {index} ({}, {} bytes)", stream.name, stream.bytes.len());
        assert_eq!(PANICS.load(Ordering::SeqCst), 0, "{context}: the server panicked");
        assert!(outcome.peak <= LIMIT as isize + SLACK, "{context}: the server held {} bytes", outcome.peak);
        largest_peak = largest_peak.max(outcome.peak);
        if let Some(code) = outcome.close {
            assert!(matches!(code, 1000 | 1002 | 1007 | 1009), "{context}: close code {code}");
        }
        match stream.expect {
            Expect::Any => {}
            _ => assert_eq!(outcome.delivered, stream.messages, "{context}: delivered messages"),
        }
        match stream.expect {
            Expect::Echo => {
                assert_eq!(outcome.close, Some(1000), "{context}: the close is answered with 1000");
                assert!(matches!(outcome.closed_event, Some(Some(_))), "{context}: the host saw the peer's close");
            }
            Expect::Close(code) => {
                assert_eq!(outcome.close, Some(code), "{context}");
                assert_eq!(outcome.closed_event, Some(None), "{context}: the host saw the connection end");
            }
            Expect::Closing => {
                assert_eq!(outcome.close, None, "{context}: a transport that ended gets no close");
                assert_eq!(outcome.closed_event, Some(None), "{context}: the host saw the connection end");
            }
            Expect::Any => {}
        }
        *closes.entry(format!("{:?}", outcome.close)).or_default() += 1;

        if index % 50 == 49 || index == STREAMS - 1 {
            let mut frame = Vec::new();
            let text = format!("sentinel {index}");
            Frame::new(true, 0x1, text.as_bytes()).write(&mut random, &mut frame);
            sentinel.write_all(&frame).await.unwrap();
            let (connection, message, length, hash) = loop {
                let event = tokio::time::timeout(Duration::from_secs(5), events.recv()).await.unwrap().unwrap();
                if let Event::Message { connection, message, length, hash, .. } = event {
                    break (connection, message, length, hash);
                }
            };
            server.consume(connection, message);
            assert_eq!((connection, length, hash), (sentinel_id, text.len(), fnv(text.as_bytes())));
            server.send(sentinel_id, MessageKind::Binary, text.as_bytes(), 0).unwrap();
            let mut reply = vec![0u8; 2 + text.len()];
            tokio::time::timeout(Duration::from_secs(5), sentinel.read_exact(&mut reply)).await.unwrap().unwrap();
            assert_eq!(&reply[2..], text.as_bytes(), "the sentinel connection still works");
        }
    }
    eprintln!(
        "seed {seed}: {STREAMS} streams, server close codes {closes:?}, largest growth while reading {largest_peak} bytes"
    );
}
