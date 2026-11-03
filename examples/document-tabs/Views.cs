using Runic.Application.Views;

namespace DocumentTabs;

// The Window and the Views of the pages a tab presents. The generator names the
// web pages after them: documents, document and documentInfo.
public sealed partial class DocumentsWindow : RunicWindow<DocumentsViewModel>;
public sealed partial class DocumentView : RunicView<DocumentViewModel>;
public sealed partial class DocumentInfoView : RunicView<DocumentInfoViewModel>;
