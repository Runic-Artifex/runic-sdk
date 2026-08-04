# RunicCommandLine.Abstractions

Parser-neutral contracts for command outcomes, safe faults, exit policy, and
machine-output selection. The shipping library targets `net10.0`, uses only the
BCL, and exposes no parser, hosting, reflection-serialization, or UI types.

## Stable identities

- Machine response protocol: `runic.commandline/1`
- Output environment variable: `RUNIC_COMMANDLINE_OUTPUT`
- Output precedence: explicit argument, then environment value, then caller default

The classifier accepts `human` and `json` environment values using ordinal
case-insensitive matching. An invalid environment value is returned as an
invalid classification. An explicit argument wins without inspecting an
otherwise invalid environment value.

## Machine output contract

Protocol version 1 is one UTF-8 JSON object followed by one LF. Machine stdout
contains no BOM, ANSI control sequence, progress output, or log line. A success
uses exit category `Success`, a typed payload, and no fault. A failure uses a
non-success category, no payload, and a sanitized `CommandFault`. JSON metadata
and envelope writing belong to a higher layer and must use source-generated
serialization.
