using TUnit.Core;
#if WINDOWS
using TUnit.Core.Executors;
#endif

// The cases share process-wide state: RunicModelContextRegistry.Shared and, in the Windows suite, the one WPF
// Application and its dispatcher thread. Run them one at a time in the former runner's order: each case has
// [NotInParallel("WpfSuite", Order = n)]; the shared key makes TUnit honour Order across classes.
[assembly: NotInParallel]
#if WINDOWS
// Windows (Runic.Application.Views.Wpf.Windows.Tests compiles this file): every case runs on the suite's STA
// thread inside its WPF Application dispatcher, as the former Main did. The portable build runs the cases
// directly and uses no executor.
[assembly: TestExecutor<WpfDispatcherExecutor>]
#endif
