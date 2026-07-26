using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CsWebUi;
using WebUIToolkit.Hosting.CsWebUi;

namespace WebUIToolkit.Hosting.CsWebUi.Tests;

internal sealed class FakeRuntime : ICsWebUiRuntime
{
    private readonly TaskCompletionSource _applicationExit =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal List<FakeWindow> Windows { get; } = [];

    public ICsWebUiWindow CreateWindow(Action<WebUiWindow>? configureWindow)
    {
        var window = new FakeWindow(configureWindow is not null);
        Windows.Add(window);
        return window;
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        _applicationExit.Task.WaitAsync(cancellationToken);

    internal void SignalApplicationExit() => _applicationExit.TrySetResult();
}

internal sealed class FakeWindow(bool configurationHookSupplied) : ICsWebUiWindow
{
    private Action<WebUiEventType>? _events;

    internal bool ConfigurationHookSupplied { get; } = configurationHookSupplied;

    internal string? RootFolder { get; private set; }

    internal uint Width { get; private set; }

    internal uint Height { get; private set; }

    internal bool IsResizable { get; private set; }

    internal bool IsPublic { get; private set; } = true;

    internal string? ShownPath { get; private set; }

    internal CsWebUiPresentationMode PresentationMode { get; private set; }

    internal WebUiBrowser Browser { get; private set; }

    internal List<string> NavigateCalls { get; } = [];

    internal string? Title { get; private set; }

    internal int CloseCount { get; private set; }

    internal int DisposeCount { get; private set; }

    public void SetRootFolder(string path) => RootFolder = path;

    public void SetSize(uint width, uint height)
    {
        Width = width;
        Height = height;
    }

    public void SetResizable(bool isResizable) => IsResizable = isResizable;

    public void SetPublic(bool isPublic) => IsPublic = isPublic;

    public IDisposable BindEvents(Action<WebUiEventType> callback)
    {
        _events = callback;
        return new CallbackBinding(() => _events = null);
    }

    public void Show(
        string relativePath,
        CsWebUiPresentationMode presentationMode,
        WebUiBrowser browser)
    {
        ShownPath = relativePath;
        PresentationMode = presentationMode;
        Browser = browser;
    }

    public void Navigate(string relativePath) => NavigateCalls.Add(relativePath);

    public void SetTitle(string title) => Title = title;

    public void Close() => CloseCount++;

    public void Dispose() => DisposeCount++;

    internal void Raise(WebUiEventType eventType) => _events?.Invoke(eventType);

    private sealed class CallbackBinding(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    internal TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"webuitoolkit-cswebui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
