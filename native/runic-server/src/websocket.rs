//! Accepted WebSocket connections. Incoming data messages are leased to the host until it consumes them, which bounds
//! what a connection buffers (the receive window); outgoing messages go through a bounded queue whose full state is
//! reported to the host as a pending write. Frames are written in queue order, so a close follows every message
//! queued before it.

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use axum::extract::ws::{CloseFrame, Message, WebSocket, WebSocketUpgrade};
use axum::response::Response;
use bytes::Bytes;
use futures_util::{SinkExt, StreamExt};
use tokio::sync::{OwnedSemaphorePermit, Semaphore, mpsc, watch};

use crate::server::{Inner, MessageKind, ServerError, WebSocketClose, WriteOutcome, offer};

const OUTGOING_CAPACITY: usize = 16;
// How long a connection waits for the other side's close frame after one side closed.
const CLOSE_GRACE: Duration = Duration::from_secs(5);

pub(crate) enum Outgoing {
    Data(MessageKind, Bytes),
    Close(u16, String),
}

pub(crate) struct Socket {
    id: u64,
    outgoing: mpsc::Sender<Outgoing>,
    receiver: Mutex<Option<mpsc::Receiver<Outgoing>>>,
    leases: Mutex<HashMap<u64, (Bytes, OwnedSemaphorePermit)>>,
    window: Arc<Semaphore>,
    aborted: watch::Sender<bool>,
    next_message: AtomicU64,
    close_queued: AtomicBool,
    closed_reported: AtomicBool,
}

impl Socket {
    pub(crate) fn new(id: u64, window: usize) -> Arc<Self> {
        let (outgoing, receiver) = mpsc::channel(OUTGOING_CAPACITY);
        Arc::new(Self {
            id,
            outgoing,
            receiver: Mutex::new(Some(receiver)),
            leases: Mutex::new(HashMap::new()),
            window: Arc::new(Semaphore::new(window)),
            aborted: watch::channel(false).0,
            next_message: AtomicU64::new(1),
            close_queued: AtomicBool::new(false),
            closed_reported: AtomicBool::new(false),
        })
    }

    pub(crate) fn consume(&self, message: u64) {
        self.leases.lock().unwrap().remove(&message);
    }

    pub(crate) fn is_close_queued(&self) -> bool {
        self.close_queued.load(Ordering::Acquire)
    }

    pub(crate) fn abort(&self) {
        self.aborted.send_replace(true);
    }

    pub(crate) fn release(&self) {
        self.leases.lock().unwrap().clear();
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
}

pub(crate) fn send(
    inner: &Arc<Inner>,
    connection: u64,
    item: Outgoing,
    token: u64,
) -> Result<WriteOutcome, ServerError> {
    let socket = inner.sockets.lock().unwrap().get(&connection).cloned().ok_or(ServerError::UnknownId)?;
    if let Outgoing::Data(MessageKind::Text, data) = &item
        && std::str::from_utf8(data).is_err()
    {
        return Err(ServerError::InvalidArgument("a text message must be UTF-8"));
    }
    let closing = matches!(item, Outgoing::Close(..));
    if socket.close_queued.load(Ordering::Acquire) {
        // A second close after the first is a no-op, as for a peer that already closed; data fails.
        return if closing { Ok(WriteOutcome::Accepted) } else { Err(ServerError::Closed) };
    }
    if closing {
        socket.close_queued.store(true, Ordering::Release);
    }
    match offer(inner, socket.outgoing.clone(), item, token) {
        // The connection ended before the host closed it: its close has nothing left to do.
        Err(ServerError::Closed) if closing => Ok(WriteOutcome::Accepted),
        other => other,
    }
}

// Reports the connection closed if the handshake never completed.
struct Start {
    inner: Arc<Inner>,
    socket: Arc<Socket>,
}

impl Drop for Start {
    fn drop(&mut self) {
        self.socket.report_closed(&self.inner, None);
    }
}

pub(crate) fn upgrade(
    inner: &Arc<Inner>,
    socket: Arc<Socket>,
    upgrade: WebSocketUpgrade,
    protocol: Option<String>,
) -> Response {
    let limit = inner.config.max_message_bytes;
    let mut upgrade = upgrade.max_message_size(limit).max_frame_size(limit);
    if let Some(protocol) = protocol {
        upgrade = upgrade.protocols([protocol]);
    }
    let start = Start { inner: inner.clone(), socket };
    upgrade.on_upgrade(move |websocket| run(start, websocket))
}

async fn run(start: Start, websocket: WebSocket) {
    let inner = start.inner.clone();
    let socket = start.socket.clone();
    let Some(mut outgoing) = socket.receiver.lock().unwrap().take() else {
        return;
    };
    let (mut sink, mut stream) = websocket.split();
    let mut aborted = socket.aborted.subscribe();

    let reader = {
        let socket = socket.clone();
        let inner = inner.clone();
        async move {
            loop {
                let Ok(permit) = socket.window.clone().acquire_owned().await else {
                    return None;
                };
                let (kind, data) = match stream.next().await {
                    Some(Ok(Message::Binary(data))) => (MessageKind::Binary, data),
                    Some(Ok(Message::Text(text))) => {
                        (MessageKind::Text, Bytes::copy_from_slice(text.as_str().as_bytes()))
                    }
                    Some(Ok(Message::Close(frame))) => {
                        return Some(
                            frame.map_or((1005, String::new()), |frame| (frame.code, frame.reason.as_str().to_owned())),
                        );
                    }
                    Some(Ok(_)) => continue,
                    Some(Err(_)) | None => return None,
                };
                let message = socket.next_message.fetch_add(1, Ordering::Relaxed);
                socket.leases.lock().unwrap().insert(message, (data.clone(), permit));
                let id = socket.id;
                inner.notify(|handler| handler.message(id, message, kind, &data));
            }
        }
    };
    let writer = async move {
        while let Some(item) = outgoing.recv().await {
            let (message, closing) = match item {
                Outgoing::Data(MessageKind::Binary, data) => (Message::Binary(data), false),
                Outgoing::Data(MessageKind::Text, data) => {
                    let text = String::from_utf8(data.to_vec()).expect("checked when queued");
                    (Message::Text(text.into()), false)
                }
                Outgoing::Close(code, reason) => {
                    (Message::Close(Some(CloseFrame { code, reason: reason.into() })), true)
                }
            };
            if sink.send(message).await.is_err() || closing {
                break;
            }
        }
        let _ = sink.flush().await;
    };
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
            () = &mut writer, if !write_done => {
                write_done = true;
                if read_done {
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
    // Dropping the halves closes the transport; `start` reports the end if nothing did.
    drop(start);
}
