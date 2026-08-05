global using TodoCsharpMarkupApplication =
    RunicMarkup.RunicToolkit.Htmx.CsWebUi.CwhtmlHtmxApplication<
        RunicToolkit.Samples.SimpleTodo.TodoViewModel,
        RunicToolkit.MVVM.CommunityToolkit.CommunityToolkitMvvmBindingAdapter<
            RunicToolkit.Samples.SimpleTodo.TodoViewModel>,
        RunicToolkit.Samples.SimpleTodo.TodoAppGenerated.RenderView,
        RunicToolkit.Samples.SimpleTodo.TodoRenderModel>;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RunicToolkit.MVVM;
using RunicMarkup.RunicToolkit.Htmx.CsWebUi;

namespace RunicToolkit.Samples.SimpleTodo;

/// <summary>Experimental C#-markup application composition over the production HTMX runtime.</summary>
internal static class TodoCsharpMarkupApplicationRoot
{
    internal static ValueTask<TodoCsharpMarkupApplication> CreateAsync(
        string staticWebRoot,
        CwhtmlHtmxAppBuilder frontend,
        CancellationToken cancellationToken = default) =>
        frontend.CreateApplicationAsync(
            TodoAppGenerated.HtmxView,
            TodoDocumentGenerated.CwhtmlView,
            TodoApplicationRoot.Contract,
            TodoApplicationRoot.AllowedOrigin,
            staticWebRoot,
            static _ => ValueTask.FromResult(new TodoViewModel()),
            static model => TodoAppGenerated.CreateHtmxAdapter(
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
