using System.ComponentModel;
using System.Text.Json;

namespace Runic.Application.Views.Generated;

/// <summary>Factory used by generated Bridges to attach a <see cref="ViewRequest{T}"/> member to its route.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ViewRequestDescriptor
{
    /// <summary>Creates the descriptor of one view request of <typeparamref name="TModel"/>.</summary>
    /// <param name="name">The request's member name on the page.</param>
    /// <param name="contract">The request's contract, which a page handler registers.</param>
    /// <param name="getRequest">Reads the request from the model.</param>
    /// <param name="encodeInput">Encodes the request's input for the page.</param>
    public static BridgeInteractionDescriptor<TModel> Create<TModel, T>(string name, string contract,
        Func<TModel, ViewRequest<T>> getRequest, Func<T, string> encodeInput) where TModel : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(contract);
        ArgumentNullException.ThrowIfNull(getRequest);
        ArgumentNullException.ThrowIfNull(encodeInput);
        return new Descriptor<TModel, T>(name, contract, getRequest, encodeInput);
    }

    private sealed class Descriptor<TModel, T>(string name, string contract, Func<TModel, ViewRequest<T>> getRequest,
        Func<T, string> encodeInput) : BridgeInteractionDescriptor<TModel> where TModel : class
    {
        public override IDisposable Attach(WindowContentSession session, TModel model, string route)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(model);
            ArgumentException.ThrowIfNullOrWhiteSpace(route);
            var request = getRequest(model) ?? throw new InvalidOperationException($"The view request '{name}' resolved to null.");
            return request.Attach(new ViewRequestBinding<T>(session, route, name, contract, encodeInput));
        }
    }
}

/// <summary>Factory used by generated Bridges to attach a <see cref="ViewQuestion{TInput, TOutput}"/> member to its route.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ViewQuestionDescriptor
{
    /// <summary>Creates the descriptor of one view question of <typeparamref name="TModel"/>.</summary>
    /// <param name="name">The question's member name on the page.</param>
    /// <param name="contract">The interaction contract that a page handler must implement.</param>
    /// <param name="getQuestion">Reads the question from the model.</param>
    /// <param name="encodeInput">Encodes the question for the page.</param>
    /// <param name="decodeOutput">Decodes the page's answer.</param>
    public static BridgeInteractionDescriptor<TModel> Create<TModel, TInput, TOutput>(string name, string contract,
        Func<TModel, ViewQuestion<TInput, TOutput>> getQuestion, Func<TInput, string> encodeInput,
        Func<JsonElement, TOutput> decodeOutput) where TModel : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(contract);
        ArgumentNullException.ThrowIfNull(getQuestion);
        ArgumentNullException.ThrowIfNull(encodeInput);
        ArgumentNullException.ThrowIfNull(decodeOutput);
        return new Descriptor<TModel, TInput, TOutput>(name, contract, getQuestion, encodeInput, decodeOutput);
    }

    private sealed class Descriptor<TModel, TInput, TOutput>(string name, string contract,
        Func<TModel, ViewQuestion<TInput, TOutput>> getQuestion, Func<TInput, string> encodeInput,
        Func<JsonElement, TOutput> decodeOutput) : BridgeInteractionDescriptor<TModel> where TModel : class
    {
        public override IDisposable Attach(WindowContentSession session, TModel model, string route)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(model);
            ArgumentException.ThrowIfNullOrWhiteSpace(route);
            var question = getQuestion(model) ?? throw new InvalidOperationException($"The view question '{name}' resolved to null.");
            return question.Attach(new ViewQuestionBinding<TInput, TOutput>(session, route, name, contract, encodeInput, decodeOutput));
        }
    }
}
