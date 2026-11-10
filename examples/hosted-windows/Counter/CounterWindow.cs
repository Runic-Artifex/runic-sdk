using Runic.Application.Views;

namespace CounterTool;

// The same Window opens in the counter's own native window and, hosted, in a native window of the shell.
public sealed partial class CounterWindow : RunicWindow<CounterViewModel>;
