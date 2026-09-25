using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;

namespace NotesReactiveViews;

public sealed partial class HomeView : ReactiveRunicView<HomeViewModel>;
public sealed partial class DocumentView : ReactiveRunicView<DocumentViewModel>;
public sealed partial class EditorView : ReactiveRunicView<EditorViewModel>;
[RunicViewContract("compact")]
public sealed partial class CompactEditorView : ReactiveRunicView<EditorViewModel>;
public sealed partial class PreviewView : ReactiveRunicView<PreviewViewModel>;
public sealed partial class PinnedNoteView : ReactiveRunicView<PinnedNoteViewModel>;
public sealed partial class PinnedTaskView : ReactiveRunicView<PinnedTaskViewModel>;
