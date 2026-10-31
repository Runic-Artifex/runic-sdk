//! Accepted WebSocket connections (RFC 6455, server side, no extensions).
//!
//! Incoming data messages are leased to the host until it consumes them, which bounds what a connection buffers (the
//! receive window). A message is read into one buffer, unmasked in place and handed to the host from there.
//!
//! Outgoing frames are written by host threads, not by the server's runtime, so a send wakes no server thread. Exactly
//! one writer holds a connection's write at a time; a send that finds a write in progress appends its frame to the
//! connection's queue, and the writer drains the queue before it lets go, so concurrent sends are coalesced into one
//! vectored write. [`send`] writes from the caller's buffer when nobody writes. A queued send (`defer`, which the .NET
//! host uses) always queues; the first one takes the write and is answered with Flush, and the host flushes after its
//! current work item, as Kestrel's output pipe flushes on the thread pool. When the socket cannot take more, the rest of
//! the queue is handed to the connection's writer task, which waits for the socket to become writable. A queue above
//! [`QUEUE_LIMIT`] bytes reports the next send as pending. Frames are written in queue order, so a close follows every
//! message queued before it.

use std::collections::{HashMap, VecDeque};
use std::io::{self, IoSlice};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::Duration;

use axum::body::Body as AxumBody;
use axum::http::request::Parts;
use axum::http::{HeaderMap, HeaderValue, StatusCode, header};
use axum::response::Response;
use bytes::{Buf, BufMut, Bytes, BytesMut};
use hyper::upgrade::{OnUpgrade, Upgraded};
use hyper_util::rt::TokioIo;
use tokio::io::AsyncReadExt;
use tokio::net::TcpStream;
use tokio::net::tcp::{OwnedReadHalf, OwnedWriteHalf};
use tokio::sync::{Notify, OwnedSemaphorePermit, Semaphore, watch};

use crate::server::{Inner, MessageKind, ServerError, WebSocketClose, WriteOutcome};

// Queued bytes above which a send is reported as pending until the queue drains below it.
const QUEUE_LIMIT: usize = 1 << 20;
// Frames per vectored write.
const BATCH: usize = 64;
// The read buffer's usual size; a frame larger than it is read into a buffer of its own size.
const READ_CHUNK: usize = 16 << 10;
// Buffers of large messages are kept for the connection's next one, in each direction, so a stream of large messages
// does not allocate (and fault in) a fresh buffer for each; Kestrel's pipes reuse pooled blocks the same way.
const SPARE_MIN: usize = 32 << 10;
const SPARE_MAX: usize = 1 << 20;
// How long a connection waits for the other side's close frame after one side closed.
const CLOSE_GRACE: Duration = Duration::from_secs(5);
const ACCEPT_GUID: &[u8] = b"258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

const OP_CONTINUATION: u8 = 0x0;
const OP_TEXT: u8 = 0x1;
const OP_BINARY: u8 = 0x2;
const OP_CLOSE: u8 = 0x8;
const OP_PING: u8 = 0x9;
const OP_PONG: u8 = 0xA;

const CLOSE_PROTOCOL_ERROR: u16 = 1002;
const CLOSE_INVALID_PAYLOAD: u16 = 1007;
const CLOSE_TOO_BIG: u16 = 1009;

/// A frame the host or the server sends.
pub(crate) enum Outgoing<'a> {
    Data(MessageKind, &'a [u8]),
    Close(u16, &'a str),
    Pong(&'a [u8]),
}

impl Outgoing<'_> {
    fn opcode(&self) -> u8 {
        match self {
            Self::Data(MessageKind::Text, _) => OP_TEXT,
            Self::Data(MessageKind::Binary, _) => OP_BINARY,
            Self::Close(..) => OP_CLOSE,
            Self::Pong(_) => OP_PONG,
        }
    }
}

// The write side of a connection. `writing` is the right to write to the socket: exactly one thread or the writer task
// holds it, and while nobody holds it on an established connection the queue is empty.
struct Writes {
    queue: VecDeque<Bytes>,
    queued: usize,
    writing: bool,
    // The writer task holds `writing`: a writer that could not finish handed the queue over, or the connection is not
    // established yet.
    handed_over: bool,
    // The host holds `writing` and has yet to call flush (a queued send answered with Flush).
    flush_scheduled: bool,
    stream: Option<Arc<OwnedWriteHalf>>,
    close_queued: bool,
    close_written: bool,
    ended: bool,
    pending: Option<u64>,
    spare: Option<BytesMut>,
}

pub(crate) struct Socket {
    id: u64,
    writes: Mutex<Writes>,
    wake_writer: Notify,
    leases: Mutex<HashMap<u64, (Bytes, OwnedSemaphorePermit)>>,
    // A consumed large message's buffer, for the next large message read.
    spare_read: Mutex<Option<BytesMut>>,
    window: Arc<Semaphore>,
    aborted: watch::Sender<bool>,
    next_message: AtomicU64,
    closed_reported: AtomicBool,
}

enum Drained {
    Empty,
    Blocked,
}

impl Socket {
    pub(crate) fn new(id: u64, window: usize) -> Arc<Self> {
        Arc::new(Self {
            id,
            writes: Mutex::new(Writes {
                queue: VecDeque::new(),
                queued: 0,
                // Until the handshake completed, sends queue and the writer task writes them once it can.
                writing: true,
                handed_over: true,
                flush_scheduled: false,
                stream: None,
                close_queued: false,
                close_written: false,
                ended: false,
                pending: None,
                spare: None,
            }),
            wake_writer: Notify::new(),
            leases: Mutex::new(HashMap::new()),
            spare_read: Mutex::new(None),
            window: Arc::new(Semaphore::new(window)),
            aborted: watch::channel(false).0,
            next_message: AtomicU64::new(1),
            closed_reported: AtomicBool::new(false),
        })
    }

    fn writes(&self) -> MutexGuard<'_, Writes> {
        self.writes.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    pub(crate) fn consume(&self, message: u64) {
        let lease = self.leases.lock().unwrap().remove(&message);
        if let Some((data, permit)) = lease {
            drop(permit);
            if let Some(buffer) = reusable(data) {
                *self.spare_read.lock().unwrap() = Some(buffer);
            }
        }
    }

    // A buffer for a large message of `length` bytes.
    fn read_buffer(&self, length: usize) -> BytesMut {
        match self.spare_read.lock().unwrap().take() {
            Some(buffer) if buffer.capacity() >= length => buffer,
            _ => BytesMut::with_capacity(length),
        }
    }

    pub(crate) fn is_close_queued(&self) -> bool {
        self.writes().close_queued
    }

    pub(crate) fn abort(&self) {
        self.aborted.send_replace(true);
    }

    pub(crate) fn release(&self) {
        self.leases.lock().unwrap().clear();
        // A flush the host scheduled may never come now: the writer task writes the queue (a close among it) instead.
        let mut writes = self.writes();
        if writes.flush_scheduled {
            writes.flush_scheduled = false;
            self.hand_over(writes);
        }
    }

    // The host's flush after a send answered with Flush: writes the queue from the calling thread.
    pub(crate) fn flush(&self, inner: &Arc<Inner>) {
        let stream = {
            let mut writes = self.writes();
            if !writes.flush_scheduled {
                return;
            }
            writes.flush_scheduled = false;
            match writes.stream.clone() {
                Some(stream) => stream,
                None => return,
            }
        };
        match self.drain(inner, &stream, true) {
            Ok(Drained::Empty) => {}
            Ok(Drained::Blocked) => self.hand_over(self.writes()),
            Err(_) => self.abort(),
        }
    }

    fn report_closed(&self, inner: &Inner, close: Option<(u16, String)>) {
        if self.closed_reported.swap(true, Ordering::AcqRel) {
            return;
        }
        let id = self.id;
        inner.notify(|handler| {
            handler.closed(id, close.as_ref().map(|(code, reason)| WebSocketClose { code: *code, reason }));
        });
    }

    // The connection ended: later sends fail and a pending send is reported as failed. Runs on a server thread.
    fn finish(&self, inner: &Inner) {
        let pending = {
            let mut writes = self.writes();
            writes.ended = true;
            writes.flush_scheduled = false;
            writes.stream = None;
            writes.queue.clear();
            writes.queued = 0;
            writes.pending.take()
        };
        if let Some(token) = pending {
            inner.notify(|handler| handler.completed(token, false));
        }
        self.report_closed(inner, None);
    }

    // Writes one frame from the caller's buffer when nobody else writes, or queues it. With `defer` the frame is always
    // queued, and a caller that gets the right to write is answered with Flush: it calls flush later, so that frames
    // sent until then are written together.
    fn send(
        &self,
        inner: &Arc<Inner>,
        item: &Outgoing<'_>,
        token: u64,
        defer: bool,
    ) -> Result<WriteOutcome, ServerError> {
        let closing = matches!(item, Outgoing::Close(..));
        let (head, head_length, close_payload);
        let payload: &[u8] = match item {
            Outgoing::Data(_, data) | Outgoing::Pong(data) => data,
            Outgoing::Close(code, reason) => {
                let mut bytes = Vec::with_capacity(2 + reason.len());
                bytes.extend_from_slice(&code.to_be_bytes());
                bytes.extend_from_slice(reason.as_bytes());
                close_payload = bytes;
                &close_payload
            }
        };
        (head, head_length) = frame_head(item.opcode(), payload.len());
        let mut writes = self.writes();
        if writes.ended {
            // The connection ended before the host closed it: its close has nothing left to do.
            return if closing { Ok(WriteOutcome::Accepted) } else { Err(ServerError::Closed) };
        }
        if writes.close_queued {
            // A second close after the first is a no-op, as for a peer that already closed; data fails.
            return match item {
                Outgoing::Data(..) => Err(ServerError::Closed),
                _ => Ok(WriteOutcome::Accepted),
            };
        }
        if closing {
            writes.close_queued = true;
        }
        if writes.writing || defer {
            let length = head_length + payload.len();
            let mut frame = match writes.spare.take() {
                Some(buffer) if buffer.capacity() >= length => buffer,
                spare => {
                    writes.spare = spare;
                    BytesMut::with_capacity(length)
                }
            };
            frame.put_slice(&head[..head_length]);
            frame.put_slice(payload);
            let over = writes.queued >= QUEUE_LIMIT;
            writes.queued += frame.len();
            writes.queue.push_back(frame.freeze());
            if !writes.writing {
                writes.writing = true;
                writes.flush_scheduled = true;
                return Ok(WriteOutcome::Flush);
            }
            if over {
                writes.pending = Some(token);
                return Ok(WriteOutcome::Pending);
            }
            return Ok(WriteOutcome::Accepted);
        }
        // Nobody writes, so the queue is empty: write this frame from the caller's buffer.
        let Some(stream) = writes.stream.clone() else {
            return Err(ServerError::Closed);
        };
        writes.writing = true;
        drop(writes);
        let total = head_length + payload.len();
        let written = match stream.try_write_vectored(&[IoSlice::new(&head[..head_length]), IoSlice::new(payload)]) {
            Ok(written) => written,
            Err(error) if error.kind() == io::ErrorKind::WouldBlock => 0,
            Err(_) => {
                self.abort();
                return Err(ServerError::Closed);
            }
        };
        if written < total {
            // Keep what the socket did not take, ahead of anything queued meanwhile, and let the writer task finish it.
            let mut rest = BytesMut::with_capacity(total - written);
            if written < head_length {
                rest.put_slice(&head[written..head_length]);
                rest.put_slice(payload);
            } else {
                rest.put_slice(&payload[written - head_length..]);
            }
            let mut writes = self.writes();
            if writes.ended {
                return Err(ServerError::Closed);
            }
            writes.queued += rest.len();
            writes.queue.push_front(rest.freeze());
            self.hand_over(writes);
            return Ok(WriteOutcome::Accepted);
        }
        // Frames queued while this one was written go out before the right to write is given back.
        match self.drain(inner, &stream, true) {
            Ok(Drained::Empty) => {}
            Ok(Drained::Blocked) => self.hand_over(self.writes()),
            Err(_) => self.abort(),
        }
        Ok(WriteOutcome::Accepted)
    }

    fn hand_over(&self, mut writes: MutexGuard<'_, Writes>) {
        writes.handed_over = true;
        drop(writes);
        self.wake_writer.notify_one();
    }

    // Writes queued frames while the socket takes them. The caller holds the right to write; it is given back when the
    // queue is empty. `inline` is true on a host thread, which never runs a callback.
    fn drain(&self, inner: &Arc<Inner>, stream: &OwnedWriteHalf, inline: bool) -> io::Result<Drained> {
        let mut batch: Vec<Bytes> = Vec::new();
        loop {
            let mut writes = self.writes();
            let mut completed = None;
            if writes.pending.is_some() && writes.queued < QUEUE_LIMIT {
                completed = writes.pending.take();
            }
            if writes.queue.is_empty() {
                writes.writing = false;
                let close_written = writes.close_queued;
                writes.close_written = close_written;
                drop(writes);
                if close_written {
                    self.wake_writer.notify_one();
                }
                self.complete(inner, completed, inline);
                return Ok(Drained::Empty);
            }
            let count = writes.queue.len().min(BATCH);
            batch.extend(writes.queue.drain(..count));
            drop(writes);
            self.complete(inner, completed, inline);

            let slices: Vec<IoSlice<'_>> = batch.iter().map(|frame| IoSlice::new(frame)).collect();
            let total: usize = batch.iter().map(Bytes::len).sum();
            let result = stream.try_write_vectored(&slices);
            drop(slices);
            let written = match result {
                Ok(written) => written,
                Err(error) if error.kind() == io::ErrorKind::WouldBlock => 0,
                Err(error) => return Err(error),
            };
            let mut writes = self.writes();
            if writes.ended {
                // The connection ended meanwhile and dropped its queue.
                return Ok(Drained::Empty);
            }
            writes.queued -= written;
            let mut skip = written;
            let mut rest = Vec::new();
            for mut frame in batch.drain(..) {
                if skip >= frame.len() {
                    skip -= frame.len();
                    if writes.spare.is_none() {
                        writes.spare = reusable(frame);
                    }
                    continue;
                }
                frame.advance(skip);
                skip = 0;
                rest.push(frame);
            }
            for frame in rest.into_iter().rev() {
                writes.queue.push_front(frame);
            }
            if written < total {
                return Ok(Drained::Blocked);
            }
        }
    }

    fn complete(&self, inner: &Arc<Inner>, token: Option<u64>, inline: bool) {
        let Some(token) = token else {
            return;
        };
        if inline {
            // A host thread inside a send: the completion runs on a server thread instead.
            let notify = inner.clone();
            inner.spawn(async move { notify.notify(|handler| handler.completed(token, true)) });
        } else {
            inner.notify(|handler| handler.completed(token, true));
        }
    }
}

pub(crate) fn send(
    inner: &Arc<Inner>,
    connection: u64,
    item: Outgoing<'_>,
    token: u64,
    defer: bool,
) -> Result<WriteOutcome, ServerError> {
    let socket = inner.sockets.lock().unwrap().get(&connection).cloned().ok_or(ServerError::UnknownId)?;
    if let Outgoing::Data(MessageKind::Text, data) = &item
        && std::str::from_utf8(data).is_err()
    {
        return Err(ServerError::InvalidArgument("a text message must be UTF-8"));
    }
    socket.send(inner, &item, token, defer)
}

pub(crate) fn flush(inner: &Arc<Inner>, connection: u64) {
    let socket = inner.sockets.lock().unwrap().get(&connection).cloned();
    if let Some(socket) = socket {
        socket.flush(inner);
    }
}

// The head of an unmasked frame with FIN set.
fn frame_head(opcode: u8, length: usize) -> ([u8; 10], usize) {
    let mut head = [0u8; 10];
    head[0] = 0x80 | opcode;
    let size = if length < 126 {
        head[1] = length as u8;
        2
    } else if let Ok(length) = u16::try_from(length) {
        head[1] = 126;
        head[2..4].copy_from_slice(&length.to_be_bytes());
        4
    } else {
        head[1] = 127;
        head[2..10].copy_from_slice(&(length as u64).to_be_bytes());
        10
    };
    (head, size)
}

/// A WebSocket handshake request the server can accept: a GET with `Connection: upgrade`, `Upgrade: websocket`,
/// version 13 and a key.
pub(crate) struct Upgrade {
    key: HeaderValue,
    on_upgrade: OnUpgrade,
}

impl Upgrade {
    pub(crate) fn from_parts(parts: &mut Parts) -> Option<Self> {
        if parts.headers.get(header::SEC_WEBSOCKET_VERSION).is_none_or(|version| version.as_bytes() != b"13") {
            return None;
        }
        let key = parts.headers.get(header::SEC_WEBSOCKET_KEY)?.clone();
        let on_upgrade = parts.extensions.remove::<OnUpgrade>()?;
        Some(Self { key, on_upgrade })
    }
}

fn accept_key(key: &[u8]) -> HeaderValue {
    use base64::Engine as _;
    use sha1::{Digest, Sha1};
    let mut sha1 = Sha1::new();
    sha1.update(key);
    sha1.update(ACCEPT_GUID);
    let mut encoded = [0u8; 28];
    let length = base64::engine::general_purpose::STANDARD
        .encode_slice(sha1.finalize(), &mut encoded)
        .expect("a SHA-1 digest is 28 base64 characters");
    HeaderValue::from_bytes(&encoded[..length]).expect("base64 is a valid header value")
}

// Reports the connection closed if the handshake never completed, and ends its writes.
struct Start {
    inner: Arc<Inner>,
    socket: Arc<Socket>,
}

impl Drop for Start {
    fn drop(&mut self) {
        self.socket.finish(&self.inner);
    }
}

pub(crate) fn upgrade(inner: &Arc<Inner>, socket: Arc<Socket>, upgrade: Upgrade, protocol: Option<String>) -> Response {
    let mut response = Response::new(AxumBody::empty());
    *response.status_mut() = StatusCode::SWITCHING_PROTOCOLS;
    let headers: &mut HeaderMap = response.headers_mut();
    headers.insert(header::CONNECTION, HeaderValue::from_static("upgrade"));
    headers.insert(header::UPGRADE, HeaderValue::from_static("websocket"));
    headers.insert(header::SEC_WEBSOCKET_ACCEPT, accept_key(upgrade.key.as_bytes()));
    if let Some(protocol) = protocol.and_then(|protocol| HeaderValue::from_str(&protocol).ok()) {
        headers.insert(header::SEC_WEBSOCKET_PROTOCOL, protocol);
    }
    let start = Start { inner: inner.clone(), socket };
    inner.spawn(async move {
        if let Ok(upgraded) = upgrade.on_upgrade.await {
            run(start, upgraded).await;
        }
    });
    response
}

async fn run(start: Start, upgraded: Upgraded) {
    let inner = start.inner.clone();
    let socket = start.socket.clone();
    // The connection's own TCP stream, so a host thread can write to it directly. axum serves through hyper-util's
    // automatic HTTP version detection, which wraps the stream; its downcast unwraps it.
    let Ok(parts) = hyper_util::server::conn::auto::upgrade::downcast::<TokioIo<TcpStream>>(upgraded) else {
        return;
    };
    let (read_half, write_half) = parts.io.into_inner().into_split();
    let write_half = Arc::new(write_half);
    let mut buffer = BytesMut::with_capacity(READ_CHUNK.max(parts.read_buf.len()));
    buffer.extend_from_slice(&parts.read_buf);
    {
        // This task holds the write until it has written what the host sent before the handshake completed; reading
        // starts after, so the server's own frames (pongs, a protocol error's close) never wait behind that.
        let mut writes = socket.writes();
        writes.stream = Some(write_half.clone());
        writes.handed_over = false;
    }
    let mut aborted = socket.aborted.subscribe();
    let written = async {
        loop {
            match socket.drain(&inner, &write_half, false) {
                Ok(Drained::Empty) => return true,
                Ok(Drained::Blocked) if write_half.writable().await.is_ok() => {}
                _ => return false,
            }
        }
    };
    tokio::select! {
        _ = aborted.wait_for(|aborted| *aborted) => return,
        written = written => if !written { return },
    }

    let reader = read(&socket, &inner, read_half, buffer);
    let writer = write(&socket, &inner, &write_half);
    tokio::pin!(reader, writer);
    let mut read_done = false;
    let mut write_done = false;
    let grace = tokio::time::sleep(Duration::MAX);
    tokio::pin!(grace);
    let mut grace_armed = false;
    loop {
        tokio::select! {
            _ = aborted.wait_for(|aborted| *aborted) => break,
            close = &mut reader, if !read_done => {
                read_done = true;
                let ended = close.is_none();
                socket.report_closed(&inner, close);
                if write_done || ended {
                    break;
                }
            }
            closed = &mut writer, if !write_done => {
                write_done = true;
                if read_done || !closed {
                    break;
                }
            }
            () = &mut grace, if grace_armed => break,
        }
        if (read_done || write_done) && !grace_armed {
            grace.as_mut().reset(tokio::time::Instant::now() + CLOSE_GRACE);
            grace_armed = true;
        }
    }
    // Ending the writes drops the socket's stream; with the halves here dropped, the transport closes. `start` reports
    // the end if nothing did.
    drop(start);
}

// The writer task: writes what other writers handed over, waiting for the socket to become writable. Returns true once
// the close frame was written, false when the socket failed.
async fn write(socket: &Socket, inner: &Arc<Inner>, stream: &OwnedWriteHalf) -> bool {
    loop {
        let owner = {
            let mut writes = socket.writes();
            if writes.handed_over {
                writes.handed_over = false;
                true
            } else if writes.close_written {
                return true;
            } else {
                false
            }
        };
        if owner {
            loop {
                match socket.drain(inner, stream, false) {
                    Ok(Drained::Empty) => break,
                    Ok(Drained::Blocked) => {
                        if stream.writable().await.is_err() {
                            return false;
                        }
                    }
                    Err(_) => return false,
                }
            }
            continue;
        }
        socket.wake_writer.notified().await;
    }
}

struct Head {
    fin: bool,
    opcode: u8,
    mask: [u8; 4],
    header: usize,
    length: usize,
}

// The head of a client frame, None until it is complete, or the close code of a protocol error.
fn parse_head(buffer: &[u8], limit: usize) -> Result<Option<Head>, u16> {
    let [first, second, ..] = *buffer else {
        return Ok(None);
    };
    // No extension was negotiated, and every client frame is masked.
    if first & 0x70 != 0 || second & 0x80 == 0 {
        return Err(CLOSE_PROTOCOL_ERROR);
    }
    let (length, mut offset) = match second & 0x7f {
        126 => match buffer.get(2..4) {
            Some(bytes) => (u64::from(u16::from_be_bytes([bytes[0], bytes[1]])), 4),
            None => return Ok(None),
        },
        127 => match buffer.get(2..10) {
            Some(bytes) => (u64::from_be_bytes(bytes.try_into().expect("eight bytes")), 10),
            None => return Ok(None),
        },
        length => (u64::from(length), 2),
    };
    let Some(mask) = buffer.get(offset..offset + 4) else {
        return Ok(None);
    };
    let mask = [mask[0], mask[1], mask[2], mask[3]];
    offset += 4;
    let fin = first & 0x80 != 0;
    let opcode = first & 0x0f;
    if opcode & 0x08 != 0 && (!fin || length > 125) {
        return Err(CLOSE_PROTOCOL_ERROR);
    }
    match usize::try_from(length) {
        Ok(length) if length <= limit => Ok(Some(Head { fin, opcode, mask, header: offset, length })),
        _ => Err(CLOSE_TOO_BIG),
    }
}

fn unmask(data: &mut [u8], mask: [u8; 4]) {
    let word = u64::from_ne_bytes([mask[0], mask[1], mask[2], mask[3], mask[0], mask[1], mask[2], mask[3]]);
    let (chunks, rest) = data.as_chunks_mut::<8>();
    for chunk in chunks {
        *chunk = (u64::from_ne_bytes(*chunk) ^ word).to_ne_bytes();
    }
    for (index, byte) in rest.iter_mut().enumerate() {
        *byte ^= mask[index & 3];
    }
}

// The buffer behind a large message nothing else holds, emptied for reuse.
fn reusable(data: Bytes) -> Option<BytesMut> {
    if data.len() < SPARE_MIN || data.len() > SPARE_MAX {
        return None;
    }
    let mut buffer = data.try_into_mut().ok()?;
    buffer.clear();
    Some(buffer)
}

fn close_code_allowed(code: u16) -> bool {
    matches!(code, 1000..=1003 | 1007..=1014 | 3000..=4999)
}

// Reads until the buffer holds at least `wanted` bytes. False when the transport ended.
async fn fill(stream: &mut OwnedReadHalf, buffer: &mut BytesMut, wanted: usize) -> bool {
    while buffer.len() < wanted {
        let spare = buffer.capacity() - buffer.len();
        if spare < wanted - buffer.len() || spare < 1024 {
            buffer.reserve((wanted - buffer.len()).max(READ_CHUNK));
        }
        match stream.read_buf(buffer).await {
            Ok(0) | Err(_) => return false,
            Ok(_) => {}
        }
    }
    true
}

// The read side: delivers data messages within the receive window and answers pings. Returns the peer's close frame,
// or None when the transport ended or the peer broke the protocol (then the server's close frame is queued).
async fn read(
    socket: &Socket,
    inner: &Arc<Inner>,
    mut stream: OwnedReadHalf,
    mut buffer: BytesMut,
) -> Option<(u16, String)> {
    let limit = inner.config.max_message_bytes;
    let fail = |code: u16| -> Option<(u16, String)> {
        let _ = socket.send(inner, &Outgoing::Close(code, ""), 0, false);
        None
    };
    let mut fragments: Option<(MessageKind, BytesMut)> = None;
    loop {
        let head = match parse_head(&buffer, limit) {
            Ok(Some(head)) => head,
            Ok(None) => {
                let wanted = buffer.len() + 1;
                if !fill(&mut stream, &mut buffer, wanted).await {
                    return None;
                }
                continue;
            }
            Err(code) => return fail(code),
        };
        let data = matches!(head.opcode, OP_TEXT | OP_BINARY | OP_CONTINUATION);
        if data && fragments.as_ref().map_or(0, |(_, part)| part.len()) + head.length > limit {
            return fail(CLOSE_TOO_BIG);
        }
        let payload = if head.length >= SPARE_MIN && head.length <= SPARE_MAX {
            // A large frame is read into a buffer of its own, which the host's consume hands back for the next one.
            buffer.advance(head.header);
            // Each chunk is unmasked as it arrives, while the kernel's copy is still in the cache.
            let mut payload = socket.read_buffer(head.length);
            let buffered = buffer.len().min(head.length);
            payload.extend_from_slice(&buffer.split_to(buffered));
            unmask(&mut payload, head.mask);
            while payload.len() < head.length {
                let start = payload.len();
                let remaining = (head.length - start) as u64;
                match (&mut stream).take(remaining).read_buf(&mut payload).await {
                    Ok(0) | Err(_) => return None,
                    Ok(_) => {}
                }
                let mask = head.mask;
                unmask(
                    &mut payload[start..],
                    [mask[start & 3], mask[(start + 1) & 3], mask[(start + 2) & 3], mask[(start + 3) & 3]],
                );
            }
            payload
        } else {
            if !fill(&mut stream, &mut buffer, head.header + head.length).await {
                return None;
            }
            let mut payload = buffer.split_to(head.header + head.length);
            payload.advance(head.header);
            unmask(&mut payload, head.mask);
            payload
        };
        let message = match head.opcode {
            OP_TEXT | OP_BINARY if fragments.is_none() => {
                let kind = if head.opcode == OP_TEXT { MessageKind::Text } else { MessageKind::Binary };
                if head.fin {
                    Some((kind, payload.freeze()))
                } else {
                    fragments = Some((kind, payload));
                    None
                }
            }
            OP_CONTINUATION if fragments.is_some() => {
                let (kind, mut part) = fragments.take().expect("checked");
                part.extend_from_slice(&payload);
                if head.fin {
                    Some((kind, part.freeze()))
                } else {
                    fragments = Some((kind, part));
                    None
                }
            }
            OP_CLOSE => {
                if payload.is_empty() {
                    return Some((1005, String::new()));
                }
                if payload.len() < 2 {
                    return fail(CLOSE_PROTOCOL_ERROR);
                }
                let code = u16::from_be_bytes([payload[0], payload[1]]);
                if !close_code_allowed(code) {
                    return fail(CLOSE_PROTOCOL_ERROR);
                }
                let Ok(reason) = std::str::from_utf8(&payload[2..]) else {
                    return fail(CLOSE_INVALID_PAYLOAD);
                };
                return Some((code, reason.to_owned()));
            }
            OP_PING => {
                let _ = socket.send(inner, &Outgoing::Pong(&payload), 0, false);
                None
            }
            OP_PONG => None,
            _ => return fail(CLOSE_PROTOCOL_ERROR),
        };
        let Some((kind, data)) = message else {
            continue;
        };
        if kind == MessageKind::Text && std::str::from_utf8(&data).is_err() {
            return fail(CLOSE_INVALID_PAYLOAD);
        }
        let Ok(permit) = socket.window.clone().acquire_owned().await else {
            return None;
        };
        let message = socket.next_message.fetch_add(1, Ordering::Relaxed);
        socket.leases.lock().unwrap().insert(message, (data.clone(), permit));
        let id = socket.id;
        inner.notify(|handler| handler.message(id, message, kind, &data));
    }
}

#[cfg(test)]
mod tests {
    use super::{accept_key, frame_head, parse_head, unmask};

    #[test]
    fn the_accept_key_follows_rfc_6455() {
        // RFC 6455 section 1.3.
        assert_eq!(accept_key(b"dGhlIHNhbXBsZSBub25jZQ==").to_str().unwrap(), "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=");
    }

    #[test]
    fn frame_heads_use_the_shortest_length() {
        assert_eq!(frame_head(0x2, 125).0[..2], [0x82, 125]);
        assert_eq!(frame_head(0x1, 126).0[..4], [0x81, 126, 0, 126]);
        assert_eq!(frame_head(0x2, 65_536).0[..10], [0x82, 127, 0, 0, 0, 0, 0, 1, 0, 0]);
    }

    #[test]
    fn client_heads_are_checked() {
        let head = parse_head(&[0x82, 0x85, 1, 2, 3, 4], 64).unwrap().unwrap();
        assert_eq!((head.fin, head.opcode, head.header, head.length, head.mask), (true, 2, 6, 5, [1, 2, 3, 4]));
        assert!(parse_head(&[0x82, 0xFE, 0], 1 << 20).unwrap().is_none(), "incomplete");
        assert_eq!(parse_head(&[0x82, 0x05], 64).err(), Some(1002), "unmasked");
        assert_eq!(parse_head(&[0xC2, 0x80, 0, 0, 0, 0], 64).err(), Some(1002), "reserved bit");
        assert_eq!(parse_head(&[0x09, 0x80, 0, 0, 0, 0], 64).err(), Some(1002), "fragmented ping");
        assert_eq!(parse_head(&[0x82, 0xFE, 0, 65, 0, 0, 0, 0], 64).err(), Some(1009), "too big");
    }

    #[test]
    fn unmasking_covers_every_offset() {
        let mask = [0x12, 0x34, 0x56, 0x78];
        for length in 0..40 {
            let original: Vec<u8> = (0..length).map(|value| value as u8).collect();
            let mut data = original.clone();
            unmask(&mut data, mask);
            let expected: Vec<u8> = original.iter().enumerate().map(|(index, byte)| byte ^ mask[index % 4]).collect();
            assert_eq!(data, expected);
        }
    }
}
