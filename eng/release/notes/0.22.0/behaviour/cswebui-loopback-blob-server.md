- **CS-WebUI NativeAOT applications are about 3 MB smaller on Linux and macOS.** Each window's blob and upload
  endpoint is now served there by Runic's own loopback HTTP/1.1 server instead of .NET's managed `HttpListener`, which
  kept `SslStream`, X.509, OCSP and an `HttpClient` in every published executable although the endpoint only serves
  `http://` on loopback. The CS-WebUI size fixture on linux-x64 goes from 10,710,336 to 7,743,328 bytes (−2.97 MB,
  −28 %). Windows keeps http.sys. The endpoint's URLs, responses and checks (Host, origin, upload token, loopback peers)
  are unchanged. The new server is stricter than `HttpListener`: it answers `400` to a request whose body framing is
  ambiguous (`Content-Length` with `Transfer-Encoding`, repeated or malformed lengths), to bare CR or LF line ends, to a
  dot segment in the path and to repeated `Host`, `Origin` or upload headers; `501` to a transfer coding other than
  `chunked`; and `414`, `431` or `408` to a request line over 8 KiB, a head over 32 KiB or 100 fields, or a head that
  takes longer than 10 seconds. A new connection must send its first byte within 5 seconds, and an idle one closes
  after 30 seconds. At its limit of 128 connections, the oldest one without a request in progress is closed to make
  room, and a failed accept is retried (event 2105). The relay that holds the other loopback address now has the same
  bounds: 128 connections, and 2 seconds for a client to close its side after the endpoint closed the connection
  (before, a client that never closed its socket kept two of the process's file descriptors for good).
  `docs/security.md` lists the limits.
