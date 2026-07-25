# Test ownership

Tests follow their production project family. Shared conformance corpora are versioned contracts and require orchestrator review.

The default solution contains the primary cumulative executable suites. The
build-only HTML/CommunityToolkit adapter and dedicated CommunityToolkit,
compiled-template, Flow, and HTMX package consumers run from
`eng/verify-wave-c.ps1`. `eng/verify-wave-d.ps1` adds the shared G4 vertical,
repository Native-AOT smokes, packed Native-AOT consumers, offline packaging,
hardening, and performance gates.
