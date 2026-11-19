- **No visible close drain on an installed browser or in WPF.** A Window in an installed browser closes at once,
  with `WindowCloseDrainUnavailable` (event 1055); its scope still waits for durable work. A WPF window is the
  application's own, so `RunicViewHost` drains nothing visibly: wait for the work in the window's `Closing`
  handler, as
  [Closing after the Window's work has drained](../../../packages/dotnet/Runic.Application.Views/README.md#closing-after-the-windows-work-has-drained)
  shows. Both stay this way in 1.0.
