- **An asynchronous command invoked with `command` replies after the frame of its end.** A command whose task completed
  inside another publication unit, as when a change in the model completes the task it awaits, published the frame of
  its end and then replied at once, without waiting for that frame. The page could then see the reply before the
  frame, with a revision it had not yet received (#1370). The reply now waits for the frame, as every other reply of a
  request that changes state does.
