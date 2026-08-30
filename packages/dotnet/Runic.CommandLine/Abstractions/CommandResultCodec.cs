using System.Globalization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine;

/// <summary>
/// Supplies closed, source-generated serialization metadata and human
/// presentation for a typed command result.
/// </summary>
/// <typeparam name="T">The command result type.</typeparam>
public interface ICommandResultCodec<T>
{
    /// <summary>Gets the stable, independently versioned payload type identifier.</summary>
    string PayloadType { get; }

    /// <summary>Gets source-generated JSON metadata for the result type.</summary>
    JsonTypeInfo<T> TypeInfo { get; }

    /// <summary>Writes a result for a person without writing a machine envelope.</summary>
    /// <param name="value">The typed result.</param>
    /// <param name="console">The invocation-local console.</param>
    /// <param name="culture">The selected presentation culture.</param>
    /// <param name="cancellationToken">Cancels the pending write.</param>
    ValueTask WriteHumanAsync(
        T value,
        ICommandConsole console,
        CultureInfo culture,
        CancellationToken cancellationToken);
}
