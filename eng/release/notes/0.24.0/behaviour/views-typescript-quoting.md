- **Generated TypeScript writes its string literals with `JSON.stringify`.** The Views TypeScript emitter used to escape
  strings as the .NET generator did, writing `+`, `&`, `'`, `<`, `>`, a backtick and every non-ASCII character as
  `\uXXXX`, so that its output equalled the .NET generator's during the switch-over (R3). It now writes them as
  `JSON.stringify` does: only `"`, `\` and control characters are escaped, so a wire name such as `höhe` appears as
  `"höhe"` instead of `"höhe"`. The values are the same, so nothing changes at run time; a committed generated
  client whose strings hold such characters changes once when it is regenerated.
