- **A command or operation that is not available answers `unavailable`, also when the window is at capacity.** A
  `command` of an asynchronous command and a `start` checked the window's capacity before the command's key and
  availability, so a window running 64 awaited commands, or retaining 64 operations, answered `capacity` for a command
  that could not run anyway (#1370). Both now check the capacity last, after availability, which tells the page the
  reason that retrying does not change. A command whose key an asynchronous resolver resolves still answers `capacity`
  before its key, since the resolution is itself awaited work of the window.
