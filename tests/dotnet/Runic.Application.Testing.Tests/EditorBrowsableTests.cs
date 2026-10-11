using System.ComponentModel;
using System.Reflection;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// Types that only generated bridges call stay public, because generated code
// in application assemblies calls them, but stay out of completion lists.
internal sealed class EditorBrowsableTests
{
    private static readonly Type[] GeneratedCodeOnly =
    [
        typeof(PropertyDescriptor<>),
        typeof(CheckedPropertyDescriptor<>),
        typeof(CheckedFieldValueKind),
        typeof(BridgeSnapshotWriter<>),
        typeof(CommandDescriptor<>),
        typeof(BridgeProtocol),
        typeof(ViewModelBridge<>),
        typeof(BridgeJson),
        typeof(BridgeWire),
        typeof(BridgeValueCodec<>),
        typeof(BridgeDataSubscriptionMember),
        typeof(BridgeCollectionDescriptor<>),
        typeof(BridgeInteractionDescriptor<>),
        typeof(BridgeValidation),
        typeof(BridgeValidationChild),
        typeof(BridgeContractShape),
        typeof(BridgeOperationResult),
        typeof(BridgeOperationResultKind),
        typeof(BridgeOperationDeliveryFailure),
        typeof(BridgeOperationDeliveryFailureKind),
        typeof(BridgeOperationStream),
        typeof(BridgeOperationStreamItem),
        typeof(BridgeOperationStreamRead),
        typeof(BridgeOperationExecution),
        typeof(ReactiveCommandExecution),
        typeof(ReactiveInteractionDescriptor),
    ];

    [Test]
    public void GeneratedCodeOnlyTypesAreHiddenFromEditors()
    {
        foreach (var type in GeneratedCodeOnly)
            Require(type.IsPublic && type.GetCustomAttribute<EditorBrowsableAttribute>(false)?.State == EditorBrowsableState.Never,
                $"{type.FullName} must stay public and carry [EditorBrowsable(EditorBrowsableState.Never)].");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
