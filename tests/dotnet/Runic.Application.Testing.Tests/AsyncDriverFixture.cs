using System.ComponentModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.Input;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

public enum AsyncDriverPriority { Low, High }

[RunicUnion(typeof(AsyncDriverTextNote), typeof(AsyncDriverCountNote))]
public abstract record AsyncDriverNote;

[RunicUnionCase("text")]
public sealed record AsyncDriverTextNote(string Text) : AsyncDriverNote;

[RunicUnionCase("count")]
public sealed record AsyncDriverCountNote(int Count) : AsyncDriverNote;

[RunicCodec(typeof(AsyncDriverMoneyCodec))]
public readonly record struct AsyncDriverMoney(decimal Value);

[RunicCodecShape("string", "bridgeWire.decimal($value)")]
public sealed class AsyncDriverMoneyCodec : IRunicCodec<AsyncDriverMoney>
{
    public static AsyncDriverMoney Read(System.Text.Json.JsonElement value) => new(BridgeWire.ReadDecimal(value));
    public static void Write(System.Text.Json.Utf8JsonWriter writer, AsyncDriverMoney value) => BridgeWire.WriteDecimal(writer, value.Value);
}

// A command input with every shape the typed driver overloads encode.
public sealed record AsyncDriverInput(
    string Title,
    long Big,
    AsyncDriverPriority Priority,
    IReadOnlyList<int> Counts,
    Dictionary<string, string> Labels,
    AsyncDriverNote Note,
    AsyncDriverMoney Price,
    string? Comment,
    [property: RunicAlias("short-name")] string ShortName)
{
    // Computed, and left out of the contract.
    [JsonIgnore]
    public string Summary => $"{Title} ({Priority})";
}

// Typed command inputs, and an asynchronous command that the test completes.
public sealed class AsyncDriverViewModel : INotifyPropertyChanged
{
    private string _title = "";
    private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AsyncDriverViewModel()
    {
        ApplyCommand = new RelayCommand<AsyncDriverInput>(input => Last = input);
        SearchCommand = new RelayCommand<string>(text => Title = text ?? "");
        WaitCommand = new AsyncRelayCommand(async () =>
        {
            await _release.Task.ConfigureAwait(false);
            Title = "released";
        });
    }

    public string Title
    {
        get => _title;
        set { _title = value; PropertyChanged?.Invoke(this, new(nameof(Title))); }
    }

    [RunicIgnore]
    public AsyncDriverInput? Last { get; private set; }

    public IRelayCommand<AsyncDriverInput> ApplyCommand { get; }

    public IRelayCommand<string> SearchCommand { get; }

    public IAsyncRelayCommand WaitCommand { get; }

    public void Release() => _release.TrySetResult();

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class AsyncDriverWindow : RunicWindow<AsyncDriverViewModel>;

// A scoped service the Window scope tests resolve.
public sealed class AsyncDriverMarker;
