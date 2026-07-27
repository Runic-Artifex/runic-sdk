global using TodoApplication =
    WebUIToolkit.MVVM.Html.Htmx.CsWebUi.CwhtmlHtmxApplication<
        WebUIToolkit.Samples.SimpleTodo.TodoViewModel,
        WebUIToolkit.MVVM.CommunityToolkit.CommunityToolkitMvvmBindingAdapter<
            WebUIToolkit.Samples.SimpleTodo.TodoViewModel>,
        WebUIToolkit.Samples.SimpleTodo.TodoAppView,
        WebUIToolkit.Samples.SimpleTodo.TodoRenderModel>;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.MVVM;
using WebUIToolkit.MVVM.Html.Htmx.CsWebUi;

namespace WebUIToolkit.Samples.SimpleTodo;

/// <summary>Describes SimpleTodo's domain-specific generated application composition.</summary>
internal static class TodoApplicationRoot
{
    internal const string AllowedOrigin = "https://simple-todo.native";
    internal static readonly MvvmContract Contract = new("samples.simple-todo");

    internal static ValueTask<TodoApplication> CreateAsync(
        string staticWebRoot,
        CwhtmlHtmxAppBuilder frontend,
        CancellationToken cancellationToken = default) =>
        frontend.CreateApplicationAsync(
            TodoAppView.HtmxView,
            TodoDocumentView.CwhtmlView,
            Contract,
            AllowedOrigin,
            staticWebRoot,
            static _ => ValueTask.FromResult(new TodoViewModel()),
            static model => TodoAppView.CreateHtmxAdapter(
                model,
                TodoJsonContext.Default),
            TodoRenderModel.Initial,
            TodoRenderModel.Response,
            static (application, developmentAssets) =>
                new TodoDocumentModel(application, developmentAssets),
            static (view, model) => view.ConfigureValidators(
                "selectedId",
            [
                (value, _) =>
                {
                    string? selectedId = value.GetString();
                    bool exists = Guid.TryParse(selectedId, out Guid id) &&
                        model.Items.Any(item => item.Id == id);
                    IReadOnlyList<string> messages = exists
                        ? []
                        : ["That task is no longer available."];
                    return ValueTask.FromResult(messages);
                },
            ]),
            cancellationToken);
}
