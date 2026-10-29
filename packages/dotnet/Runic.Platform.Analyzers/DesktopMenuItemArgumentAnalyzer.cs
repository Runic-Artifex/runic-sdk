using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Runic.Platform.Analyzers;

/// <summary>
/// RUNICPLAT001: the title and command id of <c>new DesktopMenuItem(title, commandId)</c> look swapped. Both are
/// strings, and the 0.11 rename from <c>DesktopMenuCommand(id, label)</c> reversed their order.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DesktopMenuItemArgumentAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic ID.</summary>
    public const string Id = "RUNICPLAT001";

    internal const string MenuItemType = "Runic.Platform.DesktopMenuItem";
    internal const string TitleParameter = "Title";
    internal const string CommandIdParameter = "CommandId";

    internal static readonly DiagnosticDescriptor Rule = new(
        Id,
        "DesktopMenuItem title and command id look swapped",
        "The DesktopMenuItem title '{0}' looks like a command id and the command id '{1}' {2}; DesktopMenuItem takes (title, commandId)",
        "Runic.Platform",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "DesktopMenuItem(Title, CommandId) takes the visible title first and the command id second. A command id " +
            "has 1 to 64 ASCII letters, digits, dots, underscores or hyphens. Name the arguments to make the order visible: " +
            "new DesktopMenuItem(Title: \"New Window\", CommandId: \"file.new\").",
        helpLinkUri: RunicReleaseLinks.Diagnostic(Id));

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (start.Compilation.GetTypeByMetadataName(MenuItemType) is not { } menuItem) return;
            start.RegisterOperationAction(operation => Analyze(operation, menuItem), OperationKind.ObjectCreation);
        });
    }

    private static void Analyze(OperationAnalysisContext context, INamedTypeSymbol menuItem)
    {
        var creation = (IObjectCreationOperation)context.Operation;
        if (creation.Constructor is not { } constructor || !SymbolEqualityComparer.Default.Equals(constructor.ContainingType, menuItem))
            return;
        if (Argument(creation, TitleParameter) is not { } title || Argument(creation, CommandIdParameter) is not { } commandId)
            return;
        if (Constant(title) is not { } titleText || !LooksLikeCommandId(titleText)) return;
        // The title is shaped like an id; report it when the command id is shaped like a title instead.
        var commandIdText = Constant(commandId);
        string? reason = commandIdText is null ? null
            : !IsValidCommandId(commandIdText) ? "is not a valid command id"
            : !LooksLikeCommandId(commandIdText) && LooksLikeTitle(commandIdText) ? "looks like a title"
            : null;
        if (reason is null) return;
        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.Syntax.GetLocation(), titleText, commandIdText, reason));
    }

    internal static IArgumentOperation? Argument(IObjectCreationOperation creation, string parameter)
    {
        foreach (var argument in creation.Arguments)
            if (argument.Parameter?.Name == parameter) return argument;
        return null;
    }

    private static string? Constant(IArgumentOperation argument) =>
        argument.Value.ConstantValue is { HasValue: true, Value: string text } ? text : null;

    // DesktopMenuRules: 1 to 64 ASCII letters, digits, dots, underscores or hyphens.
    internal static bool IsValidCommandId(string value)
    {
        if (value.Length is 0 or > 64) return false;
        foreach (var character in value)
            if (!(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-')) return false;
        return true;
    }

    // An id as applications write them: lower-case segments that start with a letter, joined by dots, underscores or
    // hyphens, such as file.new or edit.find-next. A version such as v1.2 is not one.
    internal static bool LooksLikeCommandId(string value)
    {
        if (!IsValidCommandId(value)) return false;
        var separated = false;
        var segmentStart = true;
        foreach (var character in value)
        {
            if (character is >= 'A' and <= 'Z') return false;
            if (character is '.' or '_' or '-')
            {
                if (segmentStart) return false;
                separated = segmentStart = true;
                continue;
            }
            if (segmentStart && character is not (>= 'a' and <= 'z')) return false;
            segmentStart = false;
        }
        return separated && !segmentStart;
    }

    // A title as users read it: words, capitals, punctuation or letters outside ASCII.
    private static bool LooksLikeTitle(string value)
    {
        foreach (var character in value)
            if (char.IsWhiteSpace(character) || char.IsUpper(character) || character > '\u007f' || character is '&' or '…') return true;
        return false;
    }
}
