global using AdvancedTodoCsharpMarkupApplication =
    RunicMarkup.RunicToolkit.Htmx.CsWebUi.CwhtmlHtmxApplication<
        RunicToolkit.Samples.AdvancedTodo.UI.TodoViewModel,
        RunicToolkit.MVVM.CommunityToolkit.CommunityToolkitMvvmBindingAdapter<
            RunicToolkit.Samples.AdvancedTodo.UI.TodoViewModel>,
        RunicToolkit.Samples.AdvancedTodo.UI.AdvancedTodoAppGenerated.RenderView,
        RunicToolkit.Samples.AdvancedTodo.UI.AdvancedTodoRenderModel>;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RunicMarkup.RunicToolkit.Htmx.CsWebUi;
using RunicToolkit.Samples.AdvancedTodo.Application;

namespace RunicToolkit.Samples.AdvancedTodo.UI;

/// <summary>Composes AdvancedTodo entirely from the generated C# markup application surface.</summary>
internal static class AdvancedTodoCsharpMarkupApplicationRoot
{
    internal static ValueTask<AdvancedTodoCsharpMarkupApplication> CreateAsync(
        TodoService service,
        string staticWebRoot,
        CwhtmlHtmxAppBuilder frontend,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        return frontend.CreateApplicationAsync(
            AdvancedTodoAppGenerated.HtmxView,
            AdvancedTodoDocumentGenerated.CwhtmlView,
            AdvancedTodoApplicationRoot.Contract,
            AdvancedTodoApplicationRoot.AllowedOrigin,
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
            static model => AdvancedTodoAppGenerated.CreateHtmxAdapter(
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
