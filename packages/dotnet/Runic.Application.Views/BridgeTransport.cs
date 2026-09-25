namespace Runic.Application.Views;

// The Bridge needs only callable handlers, change delivery, and typed input.
// Window ownership, native dialogs, and close policy stay with a host adapter.
public interface IBridgeArguments
{
    long GetInt64();
    bool GetBoolean();
    string GetString();
    string? ClientKey => null;
    string? ConnectionKey => null;
}

public interface IBridgeTransport
{
    IDisposable Bind(string name, Func<IBridgeArguments, string> handler);
    IDisposable BindAsync(string name, Func<IBridgeArguments, CancellationToken, ValueTask<string>> handler);
    void Publish(string name, string stateJson);
}
