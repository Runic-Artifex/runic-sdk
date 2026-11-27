using Runic.Application.Views;

namespace Acme.App;

// The build generates the Window's Bridge and its TypeScript client, Frontend/src/generated/counter.ts.
public sealed partial class CounterWindow : RunicWindow<CounterViewModel>;
