using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReactiveUI;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Runic.Application.Bridge.PostMvvmDiscovery.Smoke")]

namespace Runic.Application.Bridge.PostMvvmFixture;

// Fixture-local bases keep this experiment from committing public Window/View syntax.
internal abstract class ExplicitWindow<TViewModel> { }
internal abstract class ExplicitView<TViewModel> : System.IDisposable
{
    // Test-only logical View state. The frontend component remains its own object.
    public TViewModel? DataContext { get; set; }
    public int DisposeCount { get; private set; }
    public void Dispose() => DisposeCount++;
}

// Fixture-only compiled mapping. The application, rather than the browser,
// chooses one of these keys when it creates a logical View presentation.
[System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true)]
internal sealed class ExplicitViewBindingAttribute(string selectionKey, System.Type modelType) : System.Attribute
{
    public string SelectionKey { get; } = selectionKey;
    public System.Type ModelType { get; } = modelType;
}

// The View is allowed to state the reusable contract it needs. The Window still
// selects one concrete model, whose compiled members remain the only generated
// bridge surface for this fixture.
internal interface IEditorContract
{
    string Title { get; set; }
}

#if POST_MVVM_EXTERNAL_MODEL
internal sealed class NotesWindow : ExplicitWindow<string> { }
#else
internal sealed class NotesWindow : ExplicitWindow<NotesViewModel> { }
#endif
#if POST_MVVM_INTERFACE_MULTIPLE_MODELS || POST_MVVM_MAPPING_UNMAPPED || POST_MVVM_MAPPING_DUPLICATE_KEY || POST_MVVM_MAPPING_INCOMPATIBLE
internal sealed class HistoryWindow : ExplicitWindow<HistoryViewModel> { }
#endif
#if POST_MVVM_MISMATCH
internal sealed class NotesView : ExplicitView<OtherViewModel> { }
internal sealed class OtherViewModel : ObservableObject { }
#elif POST_MVVM_EXTERNAL_MODEL
internal sealed class NotesView : ExplicitView<string> { }
#elif POST_MVVM_INTERFACE_VIEW || POST_MVVM_INTERFACE_SINGLE_MAPPING || POST_MVVM_INTERFACE_MULTIPLE_MODELS || POST_MVVM_MAPPING_UNMAPPED || POST_MVVM_MAPPING_DUPLICATE_KEY || POST_MVVM_MAPPING_INCOMPATIBLE
#if POST_MVVM_INTERFACE_SINGLE_MAPPING || POST_MVVM_INTERFACE_MULTIPLE_MODELS || POST_MVVM_MAPPING_UNMAPPED || POST_MVVM_MAPPING_DUPLICATE_KEY || POST_MVVM_MAPPING_INCOMPATIBLE
[ExplicitViewBinding("local-editor", typeof(NotesViewModel))]
#if !POST_MVVM_MAPPING_UNMAPPED && !POST_MVVM_INTERFACE_SINGLE_MAPPING
[ExplicitViewBinding(
#if POST_MVVM_MAPPING_DUPLICATE_KEY
    "local-editor",
#else
    "history-editor",
#endif
#if POST_MVVM_MAPPING_INCOMPATIBLE
    typeof(OtherViewModel))]
#else
    typeof(HistoryViewModel))]
#endif
#endif
#endif
internal sealed class NotesView : ExplicitView<IEditorContract> { }
#else
internal sealed class NotesView : ExplicitView<NotesViewModel> { }
#endif

#if POST_MVVM_DUPLICATE_VIEW
internal sealed class AnotherNotesView : ExplicitView<NotesViewModel> { }
#endif

#if POST_MVVM_OPEN_GENERIC
internal sealed class GenericNotesView<TViewModel> : ExplicitView<TViewModel> { }
#endif

internal sealed partial class NotesViewModel : ObservableObject, IEditorContract
{
    [ObservableProperty]
    private string _title = "Draft";

    public ReactiveCommand<string, string> RefreshCommand { get; } = ReactiveCommand.Create<string, string>(static value => value);

    private int _saveCount;
    internal int SaveCount => System.Threading.Volatile.Read(ref _saveCount);
    // Fixture-only seam for proving that accepted window work survives a View
    // unmount. Normal discovery and ordinary generated probes leave it null.
    internal Task? SaveGate { get; set; }

    [RelayCommand]
    private Task SaveAsync(CancellationToken cancellationToken)
    {
        System.Threading.Interlocked.Increment(ref _saveCount);
        return SaveGate is null ? Task.CompletedTask : SaveGate.WaitAsync(cancellationToken);
    }
}

#if POST_MVVM_INTERFACE_MULTIPLE_MODELS || POST_MVVM_MAPPING_UNMAPPED || POST_MVVM_MAPPING_DUPLICATE_KEY || POST_MVVM_MAPPING_INCOMPATIBLE
// Deliberately different concrete surface. A contract-typed View must not gain
// this member merely because a locator could choose this model at runtime.
internal sealed partial class HistoryViewModel : ObservableObject, IEditorContract
{
    [ObservableProperty]
    private string _title = "History";

    public string HistoryOnly { get; set; } = "Not part of IEditorContract";
}
#if POST_MVVM_MAPPING_INCOMPATIBLE
internal sealed class OtherViewModel : ObservableObject { }
#endif
#endif

internal static class MetadataInspectionSentinel
{
    // Deliberate fixture trap: metadata inspection must never execute application code.
#pragma warning disable CA2255
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void MarkIfExecuted()
    {
        string? path = System.Environment.GetEnvironmentVariable("RUNIC_POST_MVVM_SENTINEL");
        if (!System.String.IsNullOrWhiteSpace(path)) System.IO.File.WriteAllText(path, "module initializer executed");
    }
#pragma warning restore CA2255
}
