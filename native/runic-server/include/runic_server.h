/*
 * The C ABI of the experimental native Runic Desktop server (native/runic-server, src/ffi.rs). ABI version 2.
 *
 * Threading: every function may be called from any thread. Callbacks run on the server's threads, never while a
 * caller waits inside a runic_server_* function, and none starts after runic_server_stop returned. A callback must
 * return without waiting on the server and must not call runic_server_stop.
 *
 * Memory: pointers passed in are read before the function returns. Pointers passed to a callback are valid for the
 * callback, except message data, which stays valid until runic_server_ws_consume or runic_server_ws_release.
 *
 * Writes: runic_server_write, runic_server_ws_send and runic_server_ws_close return RUNIC_SERVER_OK when the data is
 * written or queued, or RUNIC_SERVER_PENDING when the queue is full; on_completed then reports the token once it is
 * queued or failed. A response or connection has at most one pending write. runic_server_ws_send and
 * runic_server_ws_close write from the calling thread when no other write is in progress.
 *
 * Coalesced sends (ABI 2): runic_server_ws_queue only queues. RUNIC_SERVER_FLUSH hands the caller the connection's
 * write; it calls runic_server_ws_flush soon (from any thread, typically after the current work item), which writes
 * every message queued until then in one vectored write. Until then other sends are queued behind it.
 */
#ifndef RUNIC_SERVER_H
#define RUNIC_SERVER_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define RUNIC_SERVER_ABI_VERSION 2u

#define RUNIC_SERVER_OK 0
#define RUNIC_SERVER_PENDING 1
#define RUNIC_SERVER_FLUSH 2
#define RUNIC_SERVER_INVALID_ARGUMENT (-1)
#define RUNIC_SERVER_UNKNOWN_ID (-2)
#define RUNIC_SERVER_CLOSED (-3)
#define RUNIC_SERVER_BIND_FAILED (-4)
#define RUNIC_SERVER_ABI_MISMATCH (-5)
#define RUNIC_SERVER_INTERNAL (-6)

#define RUNIC_SERVER_FLAG_ALL_INTERFACES 1u
#define RUNIC_SERVER_REQUEST_WEBSOCKET 1u

#define RUNIC_SERVER_BODY_EMPTY 0u
#define RUNIC_SERVER_BODY_STREAM 1u
#define RUNIC_SERVER_BODY_FILE 2u

#define RUNIC_SERVER_MESSAGE_TEXT 1u
#define RUNIC_SERVER_MESSAGE_BINARY 2u

typedef struct RunicServer RunicServer;

typedef struct RunicStr {
    const uint8_t *ptr;
    size_t len;
} RunicStr;

typedef struct RunicHeader {
    RunicStr name;
    RunicStr value;
} RunicHeader;

typedef struct RunicRequest {
    uint64_t id;
    uint32_t flags; /* RUNIC_SERVER_REQUEST_WEBSOCKET */
    uint16_t remote_port;
    uint16_t local_port;
    RunicStr method;
    RunicStr target; /* path and query as sent, percent-encoded */
    RunicStr remote_address;
    RunicStr local_address;
    const RunicHeader *headers; /* lowercase names, in arrival order */
    size_t header_count;
} RunicRequest;

typedef struct RunicServerOptions {
    uint32_t abi_version; /* RUNIC_SERVER_ABI_VERSION */
    uint32_t flags;       /* RUNIC_SERVER_FLAG_ALL_INTERFACES; loopback otherwise, with the Host check */
    uint16_t port;        /* 0 for a port the system chooses */
    uint16_t reserved;
    uint32_t worker_threads;
    uint64_t max_message_bytes;
    uint32_t receive_window; /* messages delivered and not yet consumed, per connection */
    void *context;
    void (*on_request)(void *context, const RunicRequest *request);
    void (*on_aborted)(void *context, uint64_t id);
    void (*on_message)(void *context, uint64_t connection, uint64_t message, uint32_t kind, const uint8_t *data,
                       size_t length);
    /* code is the peer's close code, or -1 when the transport ended without a close frame */
    void (*on_closed)(void *context, uint64_t connection, int32_t code, const uint8_t *reason, size_t length);
    void (*on_completed)(void *context, uint64_t token, int32_t accepted);
} RunicServerOptions;

uint32_t runic_server_abi_version(void);
size_t runic_server_last_error(uint8_t *buffer, size_t capacity);

int32_t runic_server_start(const RunicServerOptions *options, RunicServer **server, uint16_t *port);
void runic_server_stop(RunicServer *server);

int32_t runic_server_respond(const RunicServer *server, uint64_t id, uint16_t status, const RunicHeader *headers,
                             size_t header_count, uint32_t body, RunicStr file_path);
int32_t runic_server_write(const RunicServer *server, uint64_t id, const uint8_t *data, size_t length, uint64_t token);
int32_t runic_server_end(const RunicServer *server, uint64_t id);
void runic_server_abort(const RunicServer *server, uint64_t id);

int32_t runic_server_ws_accept(const RunicServer *server, uint64_t id, RunicStr protocol, const RunicHeader *headers,
                               size_t header_count);
int32_t runic_server_ws_send(const RunicServer *server, uint64_t connection, uint32_t kind, const uint8_t *data,
                             size_t length, uint64_t token);
int32_t runic_server_ws_queue(const RunicServer *server, uint64_t connection, uint32_t kind, const uint8_t *data,
                              size_t length, uint64_t token);
void runic_server_ws_flush(const RunicServer *server, uint64_t connection);
int32_t runic_server_ws_close(const RunicServer *server, uint64_t connection, uint16_t code, RunicStr reason,
                              uint64_t token);
void runic_server_ws_consume(const RunicServer *server, uint64_t connection, uint64_t message);
void runic_server_ws_abort(const RunicServer *server, uint64_t connection);
void runic_server_ws_release(const RunicServer *server, uint64_t connection);

#ifdef __cplusplus
}
#endif

#endif
