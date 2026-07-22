# Acquisition fixture corpus

These fixtures define synthetic Wave B acquisition/cache expectations. Tests use an in-memory `HttpMessageHandler`; no fixture resolves DNS or contacts a live origin. `example` host names are reserved synthetic labels, and evidence strings contain no third-party license text.

The origin-index golden file is UTF-8 without BOM, LF-terminated, schema version 1, and sorted by canonical absolute origin using ordinal comparison.
