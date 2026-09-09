using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Serialization.Metadata;

namespace Runic.CommandLine;

/// <summary>Renders a typed result for people; JSON continues to use the registered codec.</summary>
public delegate ValueTask CommandHumanPresenter<in T>(T value, ICommandConsole console, CultureInfo culture, CancellationToken cancellationToken);

internal sealed class PresentedCommandResultCodec<T>(ICommandResultCodec<T> inner, CommandHumanPresenter<T> presenter) : ICommandResultCodec<T>
{
    public string PayloadType => inner.PayloadType;
    public JsonTypeInfo<T> TypeInfo => inner.TypeInfo;
    public ValueTask WriteHumanAsync(T value, ICommandConsole console, CultureInfo culture, CancellationToken cancellationToken) => presenter(value, console, culture, cancellationToken);
}
