global using AdvancedTodoApplication =
    WebUIToolkit.MVVM.Html.Htmx.CsWebUi.CwhtmlHtmxApplication<
        WebUIToolkit.Samples.AdvancedTodo.UI.TodoViewModel,
        WebUIToolkit.MVVM.CommunityToolkit.CommunityToolkitMvvmBindingAdapter<
            WebUIToolkit.Samples.AdvancedTodo.UI.TodoViewModel>,
        WebUIToolkit.Samples.AdvancedTodo.UI.AdvancedTodoAppView,
        WebUIToolkit.Samples.AdvancedTodo.UI.AdvancedTodoRenderModel>;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.MVVM;
using WebUIToolkit.MVVM.Html.Htmx.CsWebUi;
using WebUIToolkit.Samples.AdvancedTodo.Application;

namespace WebUIToolkit.Samples.AdvancedTodo.UI;

/// <summary>Describes AdvancedTodo's domain-specific generated application composition.</summary>
internal static class AdvancedTodoApplicationRoot
{
    internal const string AllowedOrigin = "https://advanced-todo.native";
    internal static readonly MvvmContract Contract = new("samples.advanced-todo");

    internal static ValueTask<AdvancedTodoApplication> CreateAsync(
        TodoService service,
        string staticWebRoot,
        CwhtmlHtmxAppBuilder frontend,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        return frontend.CreateApplicationAsync(
            AdvancedTodoAppView.HtmxView,
            AdvancedTodoDocumentView.CwhtmlView,
            Contract,
            AllowedOrigin,
            staticWebRoot,
            async selectedCancellationToken =>
            {
                var model = new TodoViewModel(service);
                try
                {
                    await model.InitializeAsync(selectedCancellationToken)
                        .ConfigureAwait(false);
                    return model;
                }
                catch
                {
                    await model.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            },
            static model => AdvancedTodoAppView.CreateHtmxAdapter(
                model,
                AdvancedTodoJsonContext.Default),
            AdvancedTodoRenderModel.Initial,
            AdvancedTodoRenderModel.Response,
            static (application, developmentAssets) =>
                new AdvancedTodoDocumentModel(application, developmentAssets),
            static (view, model) => view.ConfigureValidators(
                "selectedId",
            [
                (value, _) =>
                {
                    bool exists = Guid.TryParse(value.GetString(), out Guid id) &&
                        model.VisibleItems.Any(item => item.Id == id);
                    return ValueTask.FromResult<IReadOnlyList<string>>(
                        exists ? [] : ["That task is no longer available."]);
                },
            ]),
            cancellationToken);
    }
}
