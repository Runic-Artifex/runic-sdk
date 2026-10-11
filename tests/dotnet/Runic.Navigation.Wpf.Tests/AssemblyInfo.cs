using TUnit.Core;

// W240-005b: the WPF hosts on a real dispatcher (design record W240-001 §8 and §11). The cases share one WPF
// Application and its dispatcher thread (WpfDispatcherExecutor), Ui.LoadedWindows and the open windows.
// "registration without an Application" must run before the Application exists and "Application.Shutdown"
// last, so every case keeps the former runner's order: [NotInParallel("WpfSuite", Order = n)]; the shared key
// makes TUnit honour Order across classes. Windows are shown off-screen; shutdown tests run on secondary STA
// threads.
[assembly: NotInParallel]
