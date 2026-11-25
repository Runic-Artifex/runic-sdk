using System.Reflection;
using Runic.Application.Views;
using Runic.Application.Views.Generated;

internal static class PublicationChecks
{
    private static readonly HashSet<(Module Module, int Token)> ReportedUnpublishableIncludes = [];

    // #1320: the publication declarations of a ViewModel. A RunicPublicationMode value that is not defined is RUNICBRIDGE007;
    // [RunicInclude] with [RunicIgnore] on one property is RUNICBRIDGE077; [RunicInclude] where the Bridge publishes nothing
    // (a field, a static or non-public property, an indexer, the ReactiveUI.Validation context, or a property of a base class
    // that is not itself a generated ViewModel) is warning RUNICBRIDGE078, in either mode.
    internal static void Check(Type model, (Type Model, string Name)[] knownModels)
    {
        if (BridgePublication.DeclaredMode(model) is var mode && !Enum.IsDefined(typeof(RunicPublicationMode), mode))
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.ViewModel,
                $"{model.Name}: {mode} is not a RunicPublicationMode value; use RunicPublicationMode.Implicit or RunicPublicationMode.Explicit.", model);
        const BindingFlags AllDeclared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.Public | BindingFlags.NonPublic;
        var published = model.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public);
        foreach (var property in model.GetProperties(AllDeclared).OrderBy(property => property.MetadataToken))
        {
            if (!BridgePublication.IsIncluded(property)) continue;
            if (BridgeDtoContract.InheritanceChain(property).Any(declared => BridgeAttributeNames.Has(declared, "Runic.Application.Views.RunicIgnoreAttribute")))
                throw new BridgeDiagnosticException(BridgeDiagnosticCodes.IncludeIgnored,
                    $"{model.Name}.{property.Name}: [RunicInclude] and [RunicIgnore] contradict each other; the Bridge cannot tell whether to publish it. Remove one of them.",
                    property);
            var reason = !published.Any(candidate => Same(candidate, property)) ? "only a public instance property is published"
                : BridgeDtoContract.IsRunicIgnored(property) ? "the ReactiveUI.Validation context is published as the validation state"
                : property.GetIndexParameters().Length != 0 ? "an indexer is not published"
                : null;
            if (reason is not null) WarnUnpublishableInclude(model, property, reason);
        }
        foreach (var field in model.GetFields(AllDeclared).OrderBy(field => field.MetadataToken))
            if (BridgeAttributeNames.Has(field, BridgePublication.IncludeAttribute))
                WarnUnpublishableInclude(model, field, "a field is not published; on a CommunityToolkit [ObservableProperty] field, write [property: RunicInclude]");
        // Only the properties a ViewModel class declares are published, so one that a base class declares, and the ViewModel
        // does not override, is published only where that base class is a generated ViewModel itself.
        for (var owner = model.BaseType; owner is not null && owner != typeof(object) && !BridgeContractShape.IsFrameworkBase(owner); owner = owner.BaseType)
        {
            if (knownModels.Any(known => known.Model == owner)) break;
            foreach (var property in owner.GetProperties(AllDeclared).OrderBy(property => property.MetadataToken))
                if (BridgeAttributeNames.Has(property, BridgePublication.IncludeAttribute)
                    && !model.GetProperties(AllDeclared).Any(own => own.Name == property.Name && BridgeDtoContract.InheritanceChain(own).Any(chained => Same(chained, property))))
                    WarnUnpublishableInclude(model, property,
                        $"{owner.Name} declares it, and the Bridge publishes only the properties that the ViewModel class declares; override it in {model.Name}, or move it there");
        }
    }

    private static bool Same(MemberInfo left, MemberInfo right) =>
        left.MetadataToken == right.MetadataToken && left.Module.Equals(right.Module);

    private static void WarnUnpublishableInclude(Type model, MemberInfo member, string reason)
    {
        if (ReportedUnpublishableIncludes.Add((member.Module, member.MetadataToken)))
            Diagnostics.Warn(BridgeDiagnosticCodes.IncludeUnpublishable,
                $"{model.Name}.{member.Name}: [RunicInclude] has no effect here: {reason}.", member);
    }
}
