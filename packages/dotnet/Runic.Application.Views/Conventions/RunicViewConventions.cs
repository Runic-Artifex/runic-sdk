namespace Runic.Application.Views;

// The flavor-neutral View members this package declares itself (specs/application, "View requests"): a
// ViewQuestion<TInput, TOutput> is an interaction in every respect, in both MVVM flavors, and a ViewRequest<T> is a
// one-way request, a member kind of its own. They are metadata names like the library descriptors, so the generator
// and Hot Reload classify them the same way.
internal static class RunicViewConventions
{
    /// <summary>The generic definition of <see cref="ViewQuestion{TInput, TOutput}"/>.</summary>
    internal const string ViewQuestionContract = "Runic.Application.Views.ViewQuestion`2";

    /// <summary>The generic definition of <see cref="ViewRequest{T}"/>.</summary>
    internal const string ViewRequestContract = "Runic.Application.Views.ViewRequest`1";

    internal static readonly BridgeMvvmConvention Descriptor = new(
        Library: "Runic.Application.Views",
        FrameworkBaseTypes: [],
        FrameworkNamespaces: [],
        TypedCommandContracts: [],
        InputCommandContracts: [],
        AsyncCommandTypes: [],
        InteractionContracts: [ViewQuestionContract]);

    /// <summary>The <c>ViewRequest&lt;T&gt;</c> that <paramref name="type"/> is, or null.</summary>
    internal static Type? ViewRequest(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition().FullName == ViewRequestContract ? type : null;
}
