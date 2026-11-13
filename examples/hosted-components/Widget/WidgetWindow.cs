using Runic.Application.Views;

namespace WidgetTool;

// The same Window opens in the widget's own native window and, hosted, as a component in a dashboard's page.
public sealed partial class WidgetWindow : RunicWindow<WidgetViewModel>;
