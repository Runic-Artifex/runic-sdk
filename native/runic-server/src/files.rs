//! File bodies. The host resolved and admitted the path; the server streams the file in bounded chunks and answers a
//! single byte range (RFC 9110 section 14), which media elements need for seeking. Kestrel's `SendFileAsync` path in
//! Runic Desktop always sends the whole file.

use std::io::SeekFrom;
use std::path::PathBuf;

use axum::body::Body;
use axum::http::{HeaderMap, HeaderValue, StatusCode, header};
use axum::response::Response;
use bytes::BytesMut;
use futures_util::stream;
use tokio::fs::File;
use tokio::io::{AsyncReadExt, AsyncSeekExt};

const CHUNK: usize = 64 * 1024;

#[derive(Debug, PartialEq, Eq)]
enum Range {
    Full,
    Partial { start: u64, end: u64 },
    Unsatisfiable,
}

pub(crate) async fn respond(
    path: PathBuf,
    status: StatusCode,
    mut headers: HeaderMap,
    request: &HeaderMap,
    head: bool,
) -> Response {
    let file = match File::open(&path).await {
        Ok(file) => file,
        Err(_) => return plain(StatusCode::NOT_FOUND, headers),
    };
    let length = match file.metadata().await {
        Ok(metadata) if metadata.is_file() => metadata.len(),
        _ => return plain(StatusCode::NOT_FOUND, headers),
    };
    let ranged = status == StatusCode::OK && !headers.contains_key(header::CONTENT_ENCODING);
    let range = if ranged { select(request, length) } else { Range::Full };
    if ranged {
        headers.insert(header::ACCEPT_RANGES, HeaderValue::from_static("bytes"));
    }
    let (status, start, count) = match range {
        Range::Full => (status, 0, length),
        Range::Partial { start, end } => {
            headers.insert(
                header::CONTENT_RANGE,
                HeaderValue::from_str(&format!("bytes {start}-{end}/{length}")).expect("ASCII"),
            );
            (StatusCode::PARTIAL_CONTENT, start, end - start + 1)
        }
        Range::Unsatisfiable => {
            headers.insert(header::CONTENT_RANGE, HeaderValue::from_str(&format!("bytes */{length}")).expect("ASCII"));
            return plain(StatusCode::RANGE_NOT_SATISFIABLE, headers);
        }
    };
    headers.insert(header::CONTENT_LENGTH, HeaderValue::from(count));
    let body = if head || count == 0 { Body::empty() } else { Body::from_stream(read(file, start, count)) };
    let mut response = Response::new(body);
    *response.status_mut() = status;
    *response.headers_mut() = headers;
    response
}

fn plain(status: StatusCode, mut headers: HeaderMap) -> Response {
    headers.insert(header::CONTENT_LENGTH, HeaderValue::from(0u64));
    headers.remove(header::CONTENT_TYPE);
    let mut response = Response::new(Body::empty());
    *response.status_mut() = status;
    *response.headers_mut() = headers;
    response
}

fn read(file: File, start: u64, count: u64) -> impl futures_util::Stream<Item = std::io::Result<bytes::Bytes>> {
    stream::try_unfold((file, start, count, false), |(mut file, start, remaining, seeked)| async move {
        if remaining == 0 {
            return Ok(None);
        }
        if !seeked && start != 0 {
            file.seek(SeekFrom::Start(start)).await?;
        }
        let size = usize::try_from(remaining.min(CHUNK as u64)).expect("chunk fits");
        let mut buffer = BytesMut::zeroed(size);
        let mut filled = 0;
        while filled < size {
            let read = file.read(&mut buffer[filled..]).await?;
            if read == 0 {
                // The file shrank after its length was sent: end the body, which aborts the connection.
                return Err(std::io::Error::new(
                    std::io::ErrorKind::UnexpectedEof,
                    "the file shrank while it was sent",
                ));
            }
            filled += read;
        }
        Ok(Some((buffer.freeze(), (file, start, remaining - size as u64, true))))
    })
}

// One range of the request's Range header. Several ranges, a non-byte unit, a malformed value or an If-Range the
// server cannot validate (it sends no validators of its own) serve the whole file, as RFC 9110 allows.
fn select(request: &HeaderMap, length: u64) -> Range {
    if request.contains_key(header::IF_RANGE) {
        return Range::Full;
    }
    let Some(value) = request.get(header::RANGE).and_then(|value| value.to_str().ok()) else {
        return Range::Full;
    };
    let Some(spec) = value.trim().strip_prefix("bytes=") else {
        return Range::Full;
    };
    if spec.contains(',') {
        return Range::Full;
    }
    let Some((first, last)) = spec.trim().split_once('-') else {
        return Range::Full;
    };
    let number = |text: &str| -> Option<u64> {
        if text.is_empty() || !text.bytes().all(|byte| byte.is_ascii_digit()) { None } else { text.parse().ok() }
    };
    match (first.trim(), last.trim()) {
        ("", suffix) => match number(suffix) {
            Some(suffix) if suffix == 0 || length == 0 => Range::Unsatisfiable,
            Some(suffix) => Range::Partial { start: length.saturating_sub(suffix), end: length - 1 },
            None => Range::Full,
        },
        (first, "") => match number(first) {
            Some(start) if start < length => Range::Partial { start, end: length - 1 },
            Some(_) => Range::Unsatisfiable,
            None => Range::Full,
        },
        (first, last) => match (number(first), number(last)) {
            (Some(start), Some(end)) if start > end => Range::Full,
            (Some(start), Some(_)) if start >= length => Range::Unsatisfiable,
            (Some(start), Some(end)) => Range::Partial { start, end: end.min(length - 1) },
            _ => Range::Full,
        },
    }
}

#[cfg(test)]
mod tests {
    use super::{Range, select};
    use axum::http::{HeaderMap, HeaderValue, header};

    fn range(value: &str, length: u64) -> Range {
        let mut headers = HeaderMap::new();
        headers.insert(header::RANGE, HeaderValue::from_str(value).unwrap());
        select(&headers, length)
    }

    #[test]
    fn single_ranges() {
        assert_eq!(range("bytes=0-9", 100), Range::Partial { start: 0, end: 9 });
        assert_eq!(range("bytes=90-", 100), Range::Partial { start: 90, end: 99 });
        assert_eq!(range("bytes=-10", 100), Range::Partial { start: 90, end: 99 });
        assert_eq!(range("bytes=-1000", 100), Range::Partial { start: 0, end: 99 });
        assert_eq!(range("bytes=50-1000", 100), Range::Partial { start: 50, end: 99 });
    }

    #[test]
    fn unsatisfiable_and_ignored_ranges() {
        assert_eq!(range("bytes=100-", 100), Range::Unsatisfiable);
        assert_eq!(range("bytes=-0", 100), Range::Unsatisfiable);
        assert_eq!(range("bytes=0-", 0), Range::Unsatisfiable);
        assert_eq!(range("bytes=0-1,5-6", 100), Range::Full);
        assert_eq!(range("items=0-1", 100), Range::Full);
        assert_eq!(range("bytes=9-1", 100), Range::Full);
        assert_eq!(range("bytes=a-b", 100), Range::Full);
        let mut headers = HeaderMap::new();
        headers.insert(header::RANGE, HeaderValue::from_static("bytes=0-1"));
        headers.insert(header::IF_RANGE, HeaderValue::from_static("\"tag\""));
        assert_eq!(select(&headers, 100), Range::Full);
    }
}
