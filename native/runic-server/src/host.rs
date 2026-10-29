//! The loopback `Host` check (specs/desktop/presentation-host.md, "Security policy"): a loopback listener answers only
//! requests whose `Host` names `localhost` or a loopback address at the bound port, so a DNS-rebinding page cannot read
//! its responses as same-origin content.

use std::net::IpAddr;

/// Whether a request with this `Host` value may reach a loopback listener bound to `port`.
pub(crate) fn admits(host: Option<&[u8]>, port: u16) -> bool {
    let Some(host) = host.and_then(|value| std::str::from_utf8(value).ok()) else {
        return false;
    };
    let Some((name, authority_port)) = split(host) else {
        return false;
    };
    if authority_port != port {
        return false;
    }
    if name.eq_ignore_ascii_case("localhost") {
        return true;
    }
    let literal = name.strip_prefix('[').and_then(|inner| inner.strip_suffix(']')).unwrap_or(name);
    literal.parse::<IpAddr>().is_ok_and(|address| address.is_loopback())
}

// "name:port" or "[v6]:port". A Host without a port never matches: the listener's port is never the default port.
fn split(host: &str) -> Option<(&str, u16)> {
    let (name, port) = if host.starts_with('[') {
        let end = host.find(']')?;
        let rest = &host[end + 1..];
        (&host[..=end], rest.strip_prefix(':')?)
    } else {
        let colon = host.rfind(':')?;
        let name = &host[..colon];
        if name.contains(':') {
            return None;
        }
        (name, &host[colon + 1..])
    };
    if name.is_empty() || port.is_empty() || !port.bytes().all(|byte| byte.is_ascii_digit()) {
        return None;
    }
    Some((name, port.parse().ok()?))
}

#[cfg(test)]
mod tests {
    use super::admits;

    #[test]
    fn loopback_names_at_the_bound_port_are_admitted() {
        for host in ["localhost:8080", "LOCALHOST:8080", "127.0.0.1:8080", "127.1.2.3:8080", "[::1]:8080"] {
            assert!(admits(Some(host.as_bytes()), 8080), "{host}");
        }
    }

    #[test]
    fn other_names_ports_and_shapes_are_refused() {
        for host in [
            "evil.example:8080",
            "localhost",
            "localhost:8081",
            "127.0.0.1",
            "10.0.0.1:8080",
            "[::2]:8080",
            "::1:8080",
            "localhost:+8080",
            "localhost:",
            ":8080",
            "localhost.evil.example:8080",
            "127.0.0.1.nip.io:8080",
        ] {
            assert!(!admits(Some(host.as_bytes()), 8080), "{host}");
        }
        assert!(!admits(None, 8080));
        assert!(!admits(Some(&[0xff, b':', b'1']), 1));
    }
}
