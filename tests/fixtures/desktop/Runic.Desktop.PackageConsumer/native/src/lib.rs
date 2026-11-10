//! The package consumer's own native library: one C function that needs Rust's standard library, including its
//! unwinding, and with the runic-server feature the Runic Desktop native server linked into the same library.

// A crate that nothing names is not linked, so its exported C functions would be missing from this library.
#[cfg(feature = "runic-server")]
use runic_server as _;

/// Returns 42, computed through the standard library's formatting, parsing and panic catching.
#[unsafe(no_mangle)]
pub extern "C" fn consumer_native_answer() -> i32 {
    std::panic::catch_unwind(|| format!("{}", 6 * 7).parse::<i32>().unwrap_or(-1)).unwrap_or(-2)
}
