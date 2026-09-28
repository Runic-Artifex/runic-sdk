using ReactiveUI.Reactive;
using ReactiveUI.SourceGenerators;

var model = new GeneratedReactiveModel();
var changes = 0;
model.PropertyChanged += (_, eventArgs) =>
{
    if (eventArgs.PropertyName == nameof(GeneratedReactiveModel.Name)) changes++;
};
model.Name = "generated";
if (model.Name != "generated" || changes != 1)
    throw new InvalidOperationException("ReactiveUI.SourceGenerators did not generate a Reactive flavor property.");

Console.WriteLine("REACTIVEUI_REACTIVE_SOURCE_GENERATOR_OK");

public partial class GeneratedReactiveModel : ReactiveObject
{
    [Reactive]
    public partial string Name { get; set; }
}
