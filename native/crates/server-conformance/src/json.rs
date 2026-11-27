//! The little JSON the adapter protocol needs: one request object per line in, one response object per line out. The
//! corpus's own bytes travel as base64 strings, so this reader never sees a case's content (PROTOCOL.md, "Framing").

use std::collections::BTreeMap;
use std::fmt::Write as _;

#[derive(Clone, Debug, PartialEq)]
pub enum Json {
    Null,
    Bool(bool),
    Number(f64),
    String(String),
    Array(Vec<Json>),
    Object(BTreeMap<String, Json>),
}

impl Json {
    pub fn get(&self, key: &str) -> Option<&Json> {
        match self {
            Self::Object(members) => members.get(key),
            _ => None,
        }
    }

    pub fn as_str(&self) -> Option<&str> {
        match self {
            Self::String(text) => Some(text),
            _ => None,
        }
    }

    pub fn as_u64(&self) -> Option<u64> {
        match self {
            Self::Number(number) if *number >= 0.0 && number.fract() == 0.0 => Some(*number as u64),
            _ => None,
        }
    }

    pub fn as_bool(&self) -> Option<bool> {
        match self {
            Self::Bool(value) => Some(*value),
            _ => None,
        }
    }

    pub fn object<const N: usize>(members: [(&str, Json); N]) -> Self {
        Self::Object(members.into_iter().map(|(key, value)| (key.to_owned(), value)).collect())
    }

    pub fn text(value: impl Into<String>) -> Self {
        Self::String(value.into())
    }

    pub fn write(&self, out: &mut String) {
        match self {
            Self::Null => out.push_str("null"),
            Self::Bool(value) => out.push_str(if *value { "true" } else { "false" }),
            Self::Number(number) => {
                let _ = write!(out, "{number}");
            }
            Self::String(text) => quote(text, out),
            Self::Array(items) => {
                out.push('[');
                for (index, item) in items.iter().enumerate() {
                    if index > 0 {
                        out.push(',');
                    }
                    item.write(out);
                }
                out.push(']');
            }
            Self::Object(members) => {
                out.push('{');
                for (index, (key, value)) in members.iter().enumerate() {
                    if index > 0 {
                        out.push(',');
                    }
                    quote(key, out);
                    out.push(':');
                    value.write(out);
                }
                out.push('}');
            }
        }
    }
}

fn quote(text: &str, out: &mut String) {
    out.push('"');
    for character in text.chars() {
        match character {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            control if (control as u32) < 0x20 => {
                let _ = write!(out, "\\u{:04x}", control as u32);
            }
            other => out.push(other),
        }
    }
    out.push('"');
}

pub fn parse(text: &str) -> Result<Json, String> {
    let mut parser = Parser { bytes: text.as_bytes(), at: 0 };
    let value = parser.value(0)?;
    parser.space();
    if parser.at != parser.bytes.len() {
        return Err(format!("trailing data at {}", parser.at));
    }
    Ok(value)
}

struct Parser<'a> {
    bytes: &'a [u8],
    at: usize,
}

impl Parser<'_> {
    fn space(&mut self) {
        while matches!(self.bytes.get(self.at), Some(b' ' | b'\t' | b'\n' | b'\r')) {
            self.at += 1;
        }
    }

    fn eat(&mut self, byte: u8) -> Result<(), String> {
        self.space();
        if self.bytes.get(self.at) == Some(&byte) {
            self.at += 1;
            Ok(())
        } else {
            Err(format!("expected '{}' at {}", byte as char, self.at))
        }
    }

    fn literal(&mut self, word: &str, value: Json) -> Result<Json, String> {
        if self.bytes[self.at..].starts_with(word.as_bytes()) {
            self.at += word.len();
            Ok(value)
        } else {
            Err(format!("unexpected input at {}", self.at))
        }
    }

    fn value(&mut self, depth: usize) -> Result<Json, String> {
        if depth > 64 {
            return Err("nested too deeply".to_owned());
        }
        self.space();
        match self.bytes.get(self.at) {
            Some(b'{') => {
                self.at += 1;
                let mut members = BTreeMap::new();
                self.space();
                if self.bytes.get(self.at) == Some(&b'}') {
                    self.at += 1;
                    return Ok(Json::Object(members));
                }
                loop {
                    self.space();
                    let key = self.string()?;
                    self.eat(b':')?;
                    let value = self.value(depth + 1)?;
                    members.insert(key, value);
                    self.space();
                    match self.bytes.get(self.at) {
                        Some(b',') => self.at += 1,
                        Some(b'}') => {
                            self.at += 1;
                            return Ok(Json::Object(members));
                        }
                        _ => return Err(format!("expected ',' or '}}' at {}", self.at)),
                    }
                }
            }
            Some(b'[') => {
                self.at += 1;
                let mut items = Vec::new();
                self.space();
                if self.bytes.get(self.at) == Some(&b']') {
                    self.at += 1;
                    return Ok(Json::Array(items));
                }
                loop {
                    items.push(self.value(depth + 1)?);
                    self.space();
                    match self.bytes.get(self.at) {
                        Some(b',') => self.at += 1,
                        Some(b']') => {
                            self.at += 1;
                            return Ok(Json::Array(items));
                        }
                        _ => return Err(format!("expected ',' or ']' at {}", self.at)),
                    }
                }
            }
            Some(b'"') => Ok(Json::String(self.string()?)),
            Some(b't') => self.literal("true", Json::Bool(true)),
            Some(b'f') => self.literal("false", Json::Bool(false)),
            Some(b'n') => self.literal("null", Json::Null),
            Some(b'-' | b'0'..=b'9') => {
                let start = self.at;
                while matches!(self.bytes.get(self.at), Some(b'-' | b'+' | b'.' | b'e' | b'E' | b'0'..=b'9')) {
                    self.at += 1;
                }
                let text = std::str::from_utf8(&self.bytes[start..self.at]).expect("ASCII");
                text.parse().map(Json::Number).map_err(|_| format!("invalid number at {start}"))
            }
            _ => Err(format!("unexpected input at {}", self.at)),
        }
    }

    fn string(&mut self) -> Result<String, String> {
        if self.bytes.get(self.at) != Some(&b'"') {
            return Err(format!("expected a string at {}", self.at));
        }
        self.at += 1;
        let mut out = String::new();
        loop {
            let start = self.at;
            while !matches!(self.bytes.get(self.at), Some(b'"' | b'\\') | None) {
                self.at += 1;
            }
            out.push_str(std::str::from_utf8(&self.bytes[start..self.at]).map_err(|_| "invalid UTF-8".to_owned())?);
            match self.bytes.get(self.at) {
                Some(b'"') => {
                    self.at += 1;
                    return Ok(out);
                }
                Some(b'\\') => {
                    self.at += 1;
                    let escape = *self.bytes.get(self.at).ok_or("unterminated escape")?;
                    self.at += 1;
                    match escape {
                        b'"' => out.push('"'),
                        b'\\' => out.push('\\'),
                        b'/' => out.push('/'),
                        b'b' => out.push('\u{8}'),
                        b'f' => out.push('\u{c}'),
                        b'n' => out.push('\n'),
                        b'r' => out.push('\r'),
                        b't' => out.push('\t'),
                        b'u' => {
                            let high = self.hex4()?;
                            let code = if (0xD800..0xDC00).contains(&high) {
                                if !self.bytes[self.at..].starts_with(b"\\u") {
                                    return Err("a lone surrogate".to_owned());
                                }
                                self.at += 2;
                                let low = self.hex4()?;
                                if !(0xDC00..0xE000).contains(&low) {
                                    return Err("a lone surrogate".to_owned());
                                }
                                0x10000 + ((high - 0xD800) << 10) + (low - 0xDC00)
                            } else {
                                high
                            };
                            out.push(char::from_u32(code).ok_or("a lone surrogate")?);
                        }
                        _ => return Err(format!("invalid escape at {}", self.at)),
                    }
                }
                _ => return Err("unterminated string".to_owned()),
            }
        }
    }

    fn hex4(&mut self) -> Result<u32, String> {
        let digits = self.bytes.get(self.at..self.at + 4).ok_or("short \\u escape")?;
        self.at += 4;
        u32::from_str_radix(std::str::from_utf8(digits).map_err(|_| "invalid \\u escape")?, 16)
            .map_err(|_| "invalid \\u escape".to_owned())
    }
}

#[cfg(test)]
mod tests {
    use super::{Json, parse};

    #[test]
    fn requests_parse_and_responses_write() {
        let value =
            parse(r#"{"seq": 3, "op": "server.start", "args": {"a": [true, null, -1.5, "\u00e9\ud83d\ude00\n"]}}"#)
                .unwrap();
        assert_eq!(value.get("seq").and_then(Json::as_u64), Some(3));
        let mut out = String::new();
        value.get("args").unwrap().write(&mut out);
        assert_eq!(out, "{\"a\":[true,null,-1.5,\"é😀\\n\"]}");
        assert!(parse("{\"a\":1} x").is_err());
        assert!(parse("\"\\ud800\"").is_err());
    }
}
