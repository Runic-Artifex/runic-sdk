using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Runic.Application.Views.Analyzers;

/// <summary>
/// The RUNICBRIDGE rules that <see cref="BridgeShapeAnalyzer"/> reports while you type. Each keeps the ID, title and
/// help link of the Bridge generator's code (tools/Runic.Application.Views.Codegen, BridgeDiagnosticCodes.All); its
/// message is the generator's message for the same member, without the trailing link.
/// </summary>
internal static class BridgeRules
{
    internal const string Category = "Runic.Application.Views";

    // Diagnostic.Properties keys that tell BridgeShapeCodeFix which mechanical fix applies.
    internal const string FixKey = "RunicFix";
    internal const string NewNameKey = "RunicNewName";
    internal const string FixPrivateSetter = "PrivateSetter";
    internal const string FixRename = "Rename";
    internal const string FixPublic = "Public";

    internal static readonly DiagnosticDescriptor NameCollision = Rule("RUNICBRIDGE004",
        "Two generated names collide, or a command's client member is reserved in TypeScript");
    internal static readonly DiagnosticDescriptor View = Rule("RUNICBRIDGE006",
        "No Window or View class, or an invalid class or View contract");
    internal static readonly DiagnosticDescriptor ViewModel = Rule("RUNICBRIDGE007",
        "A ViewModel class has an unsupported shape");
    internal static readonly DiagnosticDescriptor State = Rule("RUNICBRIDGE008",
        "A state property, ViewModel content member or append-only text has an unsupported shape");
    internal static readonly DiagnosticDescriptor Command = Rule("RUNICBRIDGE009",
        "A command has an unsupported name or shape");
    internal static readonly DiagnosticDescriptor Collection = Rule("RUNICBRIDGE010",
        "A [RunicCollection] member is not a keyed collection of DTO rows");
    internal static readonly DiagnosticDescriptor Interaction = Rule("RUNICBRIDGE011",
        "A ReactiveUI interaction is not a get-only property");
    internal static readonly DiagnosticDescriptor Failure = Rule("RUNICBRIDGE012",
        "A [RunicFailure] declaration is misplaced, repeated or names an unsupported type");
    internal static readonly DiagnosticDescriptor ReactiveUiAdapter = Rule("RUNICBRIDGE025",
        "A ReactiveUI command or interaction without the Runic ReactiveUI adapter of its flavor");
    internal static readonly DiagnosticDescriptor CommandKey = Rule("RUNICBRIDGE027",
        "A [RunicCommandKey] declaration is misplaced, or its resolver is missing or has an unsupported shape");
    internal static readonly DiagnosticDescriptor ViewMember = Rule("RUNICBRIDGE035",
        "A ViewRequest or ViewQuestion member is not a get-only property");
    internal static readonly DiagnosticDescriptor QueryCollection = Rule("RUNICBRIDGE060",
        "A RunicQueryCollection member has an unsupported shape");
    internal static readonly DiagnosticDescriptor RegionCollectionName = Rule("RUNICBRIDGE071",
        "A region collection's select or close client method collides with another client member");
    internal static readonly DiagnosticDescriptor RegionCollection = Rule("RUNICBRIDGE070",
        "A [RunicRegionCollection] is not on a get-only NavigationSelection property, or its content type is unsupported");

    // Warnings. The generator leaves them to this analyzer when the compile runs it (BridgeDiagnosticCodes.AnalyzerWarnings,
    // --analyzer-warnings), so the analyzer reports every one the generator would for the project's own declarations.
    internal static readonly DiagnosticDescriptor MissingView = Rule("RUNICBRIDGE013",
        "A ViewModel that a content slot admits has no View", DiagnosticSeverity.Warning);
    internal static readonly DiagnosticDescriptor OperationRetirement = Rule("RUNICBRIDGE028",
        "A [RunicOperationRetirement] declaration reaches no operation", DiagnosticSeverity.Warning);
    internal static readonly DiagnosticDescriptor PresentationScoped = Rule("RUNICBRIDGE034",
        "A [RunicPresentationScoped] declaration reaches no Bridge command", DiagnosticSeverity.Warning);
    internal static readonly DiagnosticDescriptor OperationClose = Rule("RUNICBRIDGE039",
        "A [RunicOperationClose] declaration reaches no operation", DiagnosticSeverity.Warning);

    internal static readonly ImmutableArray<DiagnosticDescriptor> All =
    [
        NameCollision, View, ViewModel, State, Command, Collection, Interaction, Failure, ReactiveUiAdapter, CommandKey, ViewMember,
        QueryCollection, RegionCollection, RegionCollectionName, MissingView, OperationRetirement, PresentationScoped, OperationClose,
    ];

    // Generated client members that are valid C# names but break the client object type or object literal, with the
    // reason and a suggested rename: TypeScriptModules.ReservedClientMemberNames of the generator, word for word.
    internal static readonly Dictionary<string, (string Reason, string Suggestion)> ReservedClientMemberNames = new()
    {
        ["new"] = ("declares a construct signature in a TypeScript object type, so the generated client does not compile", "Create"),
        ["constructor"] = ("replaces the client's Object.prototype.constructor, which frameworks and type checks read", "Construct"),
        ["then"] = ("makes the connected client thenable, so 'await connect…()' calls it instead of resolving to the client", "Continue"),
        ["__proto__"] = ("sets the prototype of the client object literal instead of defining a member", "Prototype"),
        ["toJSON"] = ("replaces the client's JSON.stringify serialization", "Export"),
        ["toString"] = ("replaces the client's string conversion", "Format"),
        ["valueOf"] = ("replaces the client's primitive conversion", "Evaluate"),
    };

    // One descriptor per ID: the message is the generator's, built per member, so the format is the whole message.
    private static DiagnosticDescriptor Rule(string id, string title, DiagnosticSeverity severity = DiagnosticSeverity.Error) => new(
        id, title, "{0}", Category, severity, isEnabledByDefault: true,
        description: "Reported by the Bridge generator after a build and, for the rules it can check from source, by this analyzer while you type.",
        helpLinkUri: RunicReleaseLinks.Diagnostic(id));
}
