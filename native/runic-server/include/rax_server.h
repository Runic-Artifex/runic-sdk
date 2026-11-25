/*
 * The C ABI of the experimental native Runic Desktop server (native/runic-server, src/ffi.rs). ABI version 6.
 *
 * Threading: every function may be called from any thread. Callbacks run on the server's threads, never while a
 * caller waits inside a rax_server_* function, and none starts after rax_server_stop returned. A callback must
 * return without waiting on the server and must not call rax_server_stop.
 *
 * Memory: pointers passed in are read before the function returns. Pointers passed to a callback are valid for the
 * callback, except message data, which stays valid until rax_server_ws_consume or rax_server_ws_release.
 *
 * Writes: rax_server_write, rax_server_ws_send and rax_server_ws_close return RAX_SERVER_OK when the data is
 * written or queued, or RAX_SERVER_PENDING when the queue is full; on_completed then reports the token once it is
 * queued or failed. A response or connection has at most one pending write. rax_server_ws_send and
 * rax_server_ws_close write from the calling thread when no other write is in progress.
 *
 * Coalesced sends (ABI 2): rax_server_ws_queue only queues. RAX_SERVER_FLUSH hands the caller the connection's
 * write; it calls rax_server_ws_flush soon (from any thread, typically after the current work item), which writes
 * every message queued until then in one vectored write. Until then other sends are queued behind it.
 *
 * Request bodies (ABI 3): rax_server_read reads the next chunk of a request's body; on_received reports it with the
 * token: RAX_SERVER_BODY_READ_DATA with the chunk, valid during the callback, RAX_SERVER_BODY_READ_END or
 * RAX_SERVER_BODY_READ_FAILED. One read of a request is in progress at a time; reads work until the request completes.
 *
 * IPv6 loopback (ABI 4): RAX_SERVER_FLAG_IPV6_LOOPBACK listens on [::1] instead of 127.0.0.1, with the same Host
 * check. A response whose Content-Security-Policy names frame-ancestors gets no default X-Frame-Options.
 *
 * Peer admission (ABI 5): with on_admit, every connection the listener accepts is served only when on_admit returns
 * nonzero for its peer's and the listener's address (text, valid during the call) and ports. It is called on a blocking
 * thread before anything is read from the connection, so it may look the peer up; a refused connection is closed
 * without a response. Without on_admit every connection is served.
 *
 * Names (ABI 6): the library is librax_server (rax_server.dll on Windows), its functions, types and macros take the
 * rax_server_, Rax and RAX_SERVER_ prefixes, and the behaviour is ABI 5's. ABI 1 to 5 were runic_server.
 */
#ifndef RAX_SERVER_H
#define RAX_SERVER_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define RAX_SERVER_ABI_VERSION 6u
#define RAX_SERVER_BODY_READ_DATA 1u
#define RAX_SERVER_BODY_READ_END 2u
#define RAX_SERVER_BODY_READ_FAILED 3u

#define RAX_SERVER_OK 0
#define RAX_SERVER_PENDING 1
#define RAX_SERVER_FLUSH 2
#define RAX_SERVER_INVALID_ARGUMENT (-1)
#define RAX_SERVER_UNKNOWN_ID (-2)
#define RAX_SERVER_CLOSED (-3)
#define RAX_SERVER_BIND_FAILED (-4)
#define RAX_SERVER_ABI_MISMATCH (-5)
#define RAX_SERVER_INTERNAL (-6)

#define RAX_SERVER_FLAG_ALL_INTERFACES 1u
#define RAX_SERVER_FLAG_IPV6_LOOPBACK 2u
#define RAX_SERVER_REQUEST_WEBSOCKET 1u

#define RAX_SERVER_BODY_EMPTY 0u
#define RAX_SERVER_BODY_STREAM 1u
#define RAX_SERVER_BODY_FILE 2u

#define RAX_SERVER_MESSAGE_TEXT 1u
#define RAX_SERVER_MESSAGE_BINARY 2u

typedef struct RaxServer RaxServer;

typedef struct RaxStr {
    const uint8_t *ptr;
    size_t len;
} RaxStr;

typedef struct RaxHeader {
    RaxStr name;
    RaxStr value;
} RaxHeader;

typedef struct RaxRequest {
    uint64_t id;
    uint32_t flags; /* RAX_SERVER_REQUEST_WEBSOCKET */
    uint16_t remote_port;
    uint16_t local_port;
    RaxStr method;
    RaxStr target; /* path and query as sent, percent-encoded */
    RaxStr remote_address;
    RaxStr local_address;
    const RaxHeader *headers; /* lowercase names, in arrival order */
    size_t header_count;
} RaxRequest;

typedef struct RaxServerOptions {
    uint32_t abi_version; /* RAX_SERVER_ABI_VERSION */
    uint32_t flags;       /* RAX_SERVER_FLAG_ALL_INTERFACES, or loopback with the Host check: RAX_SERVER_FLAG_IPV6_LOOPBACK for [::1] */
    uint16_t port;        /* 0 for a port the system chooses */
    uint16_t reserved;
    uint32_t worker_threads;
    uint64_t max_message_bytes;
    uint32_t receive_window; /* messages delivered and not yet consumed, per connection */
    void *context;
    void (*on_request)(void *context, const RaxRequest *request);
    void (*on_aborted)(void *context, uint64_t id);
    void (*on_message)(void *context, uint64_t connection, uint64_t message, uint32_t kind, const uint8_t *data,
                       size_t length);
    /* code is the peer's close code, or -1 when the transport ended without a close frame */
    void (*on_closed)(void *context, uint64_t connection, int32_t code, const uint8_t *reason, size_t length);
    void (*on_completed)(void *context, uint64_t token, int32_t accepted);
    void (*on_received)(void *context, uint64_t token, uint32_t kind, const uint8_t *data, size_t length);
    /* optional: NULL serves every connection */
    int32_t (*on_admit)(void *context, const uint8_t *remote_address, size_t remote_length, uint16_t remote_port,
                        const uint8_t *local_address, size_t local_length, uint16_t local_port);
} RaxServerOptions;

uint32_t rax_server_abi_version(void);
size_t rax_server_last_error(uint8_t *buffer, size_t capacity);

int32_t rax_server_start(const RaxServerOptions *options, RaxServer **server, uint16_t *port);
void rax_server_stop(RaxServer *server);

int32_t rax_server_respond(const RaxServer *server, uint64_t id, uint16_t status, const RaxHeader *headers,
                             size_t header_count, uint32_t body, RaxStr file_path);
int32_t rax_server_write(const RaxServer *server, uint64_t id, const uint8_t *data, size_t length, uint64_t token);
int32_t rax_server_read(const RaxServer *server, uint64_t id, uint64_t token);
int32_t rax_server_end(const RaxServer *server, uint64_t id);
void rax_server_abort(const RaxServer *server, uint64_t id);

int32_t rax_server_ws_accept(const RaxServer *server, uint64_t id, RaxStr protocol, const RaxHeader *headers,
                               size_t header_count);
int32_t rax_server_ws_send(const RaxServer *server, uint64_t connection, uint32_t kind, const uint8_t *data,
                             size_t length, uint64_t token);
int32_t rax_server_ws_queue(const RaxServer *server, uint64_t connection, uint32_t kind, const uint8_t *data,
                              size_t length, uint64_t token);
void rax_server_ws_flush(const RaxServer *server, uint64_t connection);
int32_t rax_server_ws_close(const RaxServer *server, uint64_t connection, uint16_t code, RaxStr reason,
                              uint64_t token);
void rax_server_ws_consume(const RaxServer *server, uint64_t connection, uint64_t message);
void rax_server_ws_abort(const RaxServer *server, uint64_t connection);
void rax_server_ws_release(const RaxServer *server, uint64_t connection);

#ifdef __cplusplus
}
#endif

#endif
