using System.Collections.Immutable;
using Runic.Platform;

namespace Runic.Application.Testing;

/// <summary>Which <see cref="IFileDialogs"/> method a test window's dialog call was.</summary>
public enum RunicFileDialogKind
{
    /// <summary><see cref="IFileDialogs.OpenFileAsync"/>.</summary>
    OpenFile,
    /// <summary><see cref="IFileDialogs.OpenFilesAsync"/>.</summary>
    OpenFiles,
    /// <summary><see cref="IFileDialogs.OpenDocumentAsync"/>.</summary>
    OpenDocument,
    /// <summary><see cref="IFileDialogs.SaveFileAsync"/>.</summary>
    SaveFile,
    /// <summary><see cref="IFileDialogs.SaveDocumentAsync"/>.</summary>
    SaveDocument,
    /// <summary><see cref="IFileDialogs.OpenDirectoryAsync"/>.</summary>
    OpenDirectory,
    /// <summary><see cref="IFileDialogs.OpenDirectoriesAsync"/>.</summary>
    OpenDirectories,
}

/// <summary>How a scripted dialog, message box or context menu ended.</summary>
public enum RunicTestOutcome
{
    /// <summary>The user chose something: files, a button or a menu item.</summary>
    Selected,
    /// <summary>The user dismissed it; the queue was empty or scripted a dismissal.</summary>
    Dismissed,
    /// <summary>It reported <see cref="PlatformResult{T}.Unavailable"/> or <see cref="PickerResult{T}.Unavailable"/>.</summary>
    Unavailable,
    /// <summary>It reported a failure.</summary>
    Failed,
}

/// <summary>One file dialog the application showed, with the options it passed.</summary>
/// <param name="Kind">The dialog method.</param>
/// <param name="Outcome">How the dialog ended.</param>
public sealed record RunicFileDialogCall(RunicFileDialogKind Kind, RunicTestOutcome Outcome)
{
    /// <summary>The options of an open-file or open-document dialog.</summary>
    public OpenFileOptions? OpenOptions { get; init; }
    /// <summary>The options of a save dialog.</summary>
    public SaveFileOptions? SaveOptions { get; init; }
    /// <summary>The options of a directory dialog.</summary>
    public OpenDirectoryOptions? DirectoryOptions { get; init; }
    /// <summary>The selected files, empty for a directory selection or a dialog that selected nothing.</summary>
    public ImmutableArray<RunicTestFile> Files { get; init; } = [];
    /// <summary>The selected directories' paths.</summary>
    public ImmutableArray<string> Directories { get; init; } = [];
    /// <summary>Whether a scripted response answered the dialog; <see langword="false"/> when the queue was empty.</summary>
    public bool Scripted { get; init; }
}

/// <summary>
/// Scripted <see cref="IFileDialogs"/> for tests: each dialog takes the next queued response, hands out in-memory
/// <see cref="RunicTestFile"/> leases that follow the real lease and commit rules, and records its options.
/// </summary>
/// <remarks>
/// <para>
/// Queue a response before the action that shows the dialog: <see cref="EnqueueOpen(RunicTestFile)"/>
/// for an open-file or open-document dialog, <see cref="EnqueueSave(string)"/> for a save dialog,
/// <see cref="EnqueueOpenDirectory(string[])"/>, or <see cref="EnqueueDismissed"/>, <see cref="EnqueueUnavailable"/>
/// and <see cref="EnqueueFailed"/> for any dialog. A dialog shown with an empty queue is dismissed. A response that
/// does not fit the dialog, such as a save destination for an open dialog, throws
/// <see cref="InvalidOperationException"/> from the dialog call, so a test sees the wrong dialog at once.
/// </para>
/// <para>
/// The dialogs check their options as the real providers do. When the test window closes, the dialogs report
/// <see cref="PlatformUnavailableReason.OwnerClosed"/> and release every lease they handed out. The type is safe to use
/// from any thread.
/// </para>
/// </remarks>
public sealed class RunicTestFileDialogs : IFileDialogs
{
    private readonly object _gate = new();
    private readonly Queue<Response> _responses = new();
    private readonly List<RunicFileDialogCall> _calls = [];
    private readonly HashSet<TestLease> _leases = [];
    private readonly RunicTestPlatform _platform;

    internal RunicTestFileDialogs(RunicTestPlatform platform) => _platform = platform;

    /// <summary>The dialogs shown so far, oldest first.</summary>
    public IReadOnlyList<RunicFileDialogCall> Calls
    {
        get { lock (_gate) return [.. _calls]; }
    }

    /// <summary>The number of responses still queued.</summary>
    public int Pending
    {
        get { lock (_gate) return _responses.Count; }
    }

    /// <summary>Queues the selection of an existing file for the next open-file, open-files or open-document dialog.</summary>
    /// <param name="file">The selected file.</param>
    /// <returns>The file, to check later.</returns>
    public RunicTestFile EnqueueOpen(RunicTestFile file) => EnqueueOpen(file, selectedFilter: null);

    /// <summary>Queues the selection of an existing file with the filter the dialog reports as active.</summary>
    /// <param name="file">The selected file.</param>
    /// <param name="selectedFilter">The filter the dialog reports as active, one of the options' filters, or <see langword="null"/>.</param>
    /// <returns>The file, to check later.</returns>
    public RunicTestFile EnqueueOpen(RunicTestFile file, FileDialogFilter? selectedFilter)
    {
        ArgumentNullException.ThrowIfNull(file);
        Enqueue(new Response.Files([file], Save: false, selectedFilter));
        return file;
    }

    /// <summary>Queues the selection of a file with <paramref name="content"/> for the next open dialog.</summary>
    /// <param name="name">The file's display name.</param>
    /// <param name="content">Its bytes.</param>
    /// <returns>The file, to check later.</returns>
    public RunicTestFile EnqueueOpen(string name, ReadOnlySpan<byte> content) => EnqueueOpen(new RunicTestFile(name, content));

    /// <summary>Queues the selection of a file with UTF-8 <paramref name="text"/> for the next open dialog.</summary>
    /// <param name="name">The file's display name.</param>
    /// <param name="text">Its text.</param>
    /// <returns>The file, to check later.</returns>
    public RunicTestFile EnqueueOpen(string name, string text) => EnqueueOpen(RunicTestFile.FromText(name, text));

    /// <summary>Queues the selection of several files for the next open-files dialog.</summary>
    /// <param name="files">The selected files, in the order the dialog returns them; at least one.</param>
    public void EnqueueOpenFiles(params RunicTestFile[] files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Length == 0 || files.Contains(null!)) throw new ArgumentException("Select at least one file, without null.", nameof(files));
        Enqueue(new Response.Files([.. files], Save: false, null));
    }

    /// <summary>Queues a new save destination for the next save-file or save-document dialog.</summary>
    /// <param name="name">The name the user chose.</param>
    /// <returns>The destination, which does not exist until the first committed write.</returns>
    public RunicTestFile EnqueueSave(string name) => EnqueueSave(RunicTestFile.Missing(name), selectedFilter: null);

    /// <summary>Queues a save destination, such as an existing file the user replaces, for the next save dialog.</summary>
    /// <param name="file">The destination.</param>
    /// <returns>The destination.</returns>
    public RunicTestFile EnqueueSave(RunicTestFile file) => EnqueueSave(file, selectedFilter: null);

    /// <summary>Queues a save destination with the filter the dialog reports as active.</summary>
    /// <param name="file">The destination.</param>
    /// <param name="selectedFilter">The filter the dialog reports as active, or <see langword="null"/>.</param>
    /// <returns>The destination.</returns>
    public RunicTestFile EnqueueSave(RunicTestFile file, FileDialogFilter? selectedFilter)
    {
        ArgumentNullException.ThrowIfNull(file);
        Enqueue(new Response.Files([file], Save: true, selectedFilter));
        return file;
    }

    /// <summary>Queues the selection of directories for the next directory dialog.</summary>
    /// <param name="paths">Fully qualified paths, one for an open-directory dialog; nothing checks that they exist.</param>
    public void EnqueueOpenDirectory(params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Length == 0) throw new ArgumentException("Select at least one directory.", nameof(paths));
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
                throw new ArgumentException("Use fully qualified directory paths.", nameof(paths));
        }
        Enqueue(new Response.Directories([.. paths]));
    }

    /// <summary>Queues a dismissal: the next dialog of any kind reports <see cref="PickerResult{T}.Dismissed"/>.</summary>
    public void EnqueueDismissed() => Enqueue(new Response.Dismissed());

    /// <summary>Queues unavailability for the next dialog of any kind.</summary>
    /// <param name="reason">The reason it reports.</param>
    public void EnqueueUnavailable(PlatformUnavailableReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        Enqueue(new Response.Unavailable(reason));
    }

    /// <summary>Queues a failure for the next dialog of any kind.</summary>
    /// <param name="code">The failure it reports.</param>
    public void EnqueueFailed(PlatformFailureCode code)
    {
        if (code == PlatformFailureCode.Unknown) throw new ArgumentOutOfRangeException(nameof(code), "Runic never reports Unknown.");
        Enqueue(new Response.Failed(code));
    }

    /// <inheritdoc />
    public ValueTask<PickerResult<IReadFileLease>> OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken = default)
    {
        CheckOpen(options, cancellationToken);
        return Show<IReadFileLease>(RunicFileDialogKind.OpenFile, new RunicFileDialogCall(RunicFileDialogKind.OpenFile, default) { OpenOptions = options },
            files => files.Length == 1 ? new TestReadLease(this, files[0].DisplayName, ReadSelected(files[0])) : null);
    }

    /// <inheritdoc />
    public ValueTask<PickerResult<IReadFileLeaseList>> OpenFilesAsync(OpenFileOptions options, CancellationToken cancellationToken = default)
    {
        CheckOpen(options, cancellationToken);
        return Show<IReadFileLeaseList>(RunicFileDialogKind.OpenFiles, new RunicFileDialogCall(RunicFileDialogKind.OpenFiles, default) { OpenOptions = options },
            files => new TestLeaseList<IReadFileLease>(this,
                [.. files.Select(file => (IReadFileLease)Track(new TestReadLease(this, file.DisplayName, ReadSelected(file))))]));
    }

    /// <inheritdoc />
    public ValueTask<PickerResult<IDocumentFileLease>> OpenDocumentAsync(OpenFileOptions options, CancellationToken cancellationToken = default)
    {
        CheckOpen(options, cancellationToken);
        return Show<IDocumentFileLease>(RunicFileDialogKind.OpenDocument, new RunicFileDialogCall(RunicFileDialogKind.OpenDocument, default) { OpenOptions = options },
            files => files.Length == 1 ? new TestDocumentLease(this, Existing(files[0])) : null);
    }

    /// <inheritdoc />
    public ValueTask<PickerResult<ISaveFileLease>> SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken = default)
    {
        CheckSave(options, cancellationToken);
        return Show<ISaveFileLease>(RunicFileDialogKind.SaveFile, new RunicFileDialogCall(RunicFileDialogKind.SaveFile, default) { SaveOptions = options },
            files => files.Length == 1 ? new TestSaveLease(this, files[0]) : null, save: true);
    }

    /// <inheritdoc />
    public ValueTask<PickerResult<IDocumentFileLease>> SaveDocumentAsync(SaveFileOptions options, CancellationToken cancellationToken = default)
    {
        CheckSave(options, cancellationToken);
        return Show<IDocumentFileLease>(RunicFileDialogKind.SaveDocument, new RunicFileDialogCall(RunicFileDialogKind.SaveDocument, default) { SaveOptions = options },
            files => files.Length == 1 ? new TestDocumentLease(this, files[0]) : null, save: true);
    }

    /// <inheritdoc />
    public ValueTask<PickerResult<IDirectoryLease>> OpenDirectoryAsync(OpenDirectoryOptions options, CancellationToken cancellationToken = default)
    {
        CheckDirectory(options, cancellationToken);
        return ShowDirectories<IDirectoryLease>(new RunicFileDialogCall(RunicFileDialogKind.OpenDirectory, default) { DirectoryOptions = options },
            paths => paths.Length == 1 ? new TestDirectoryLease(this, paths[0]) : null);
    }

    /// <inheritdoc />
    public ValueTask<PickerResult<IDirectoryLeaseList>> OpenDirectoriesAsync(OpenDirectoryOptions options, CancellationToken cancellationToken = default)
    {
        CheckDirectory(options, cancellationToken);
        return ShowDirectories<IDirectoryLeaseList>(new RunicFileDialogCall(RunicFileDialogKind.OpenDirectories, default) { DirectoryOptions = options },
            paths => new TestLeaseList<IDirectoryLease>(this, [.. paths.Select(path => (IDirectoryLease)Track(new TestDirectoryLease(this, path)))]));
    }

    private ValueTask<PickerResult<T>> Show<T>(RunicFileDialogKind kind, RunicFileDialogCall call,
        Func<ImmutableArray<RunicTestFile>, T?> select, bool save = false) where T : class, IAsyncDisposable
    {
        if (Closed() is { } closed) return Answer(call with { Outcome = RunicTestOutcome.Unavailable }, new PickerResult<T>.Unavailable(closed));
        var response = Next(out var scripted);
        call = call with { Scripted = scripted };
        switch (response)
        {
            case Response.Files files when files.Save == save && select(files.Selection) is { } lease:
                Track((TestLease)(object)lease);
                return Answer(call with { Outcome = RunicTestOutcome.Selected, Files = files.Selection },
                    new PickerResult<T>.Selected(lease) { SelectedFilter = files.SelectedFilter });
            case Response.Files or Response.Directories:
                throw Mismatch(kind, response);
            default:
                var result = Generic<T>(response, ref call);
                return Answer(call, result);
        }
    }

    private ValueTask<PickerResult<T>> ShowDirectories<T>(RunicFileDialogCall call, Func<ImmutableArray<string>, T?> select)
        where T : class, IAsyncDisposable
    {
        if (Closed() is { } closed) return Answer(call with { Outcome = RunicTestOutcome.Unavailable }, new PickerResult<T>.Unavailable(closed));
        var response = Next(out var scripted);
        call = call with { Scripted = scripted };
        switch (response)
        {
            case Response.Directories directories when select(directories.Paths) is { } lease:
                Track((TestLease)(object)lease);
                return Answer(call with { Outcome = RunicTestOutcome.Selected, Directories = directories.Paths },
                    new PickerResult<T>.Selected(lease));
            case Response.Files or Response.Directories:
                throw Mismatch(call.Kind, response);
            default:
                var result = Generic<T>(response, ref call);
                return Answer(call, result);
        }
    }

    private static PickerResult<T> Generic<T>(Response response, ref RunicFileDialogCall call) where T : IAsyncDisposable
    {
        switch (response)
        {
            case Response.Unavailable unavailable:
                call = call with { Outcome = RunicTestOutcome.Unavailable };
                return new PickerResult<T>.Unavailable(unavailable.Reason);
            case Response.Failed failed:
                call = call with { Outcome = RunicTestOutcome.Failed };
                return new PickerResult<T>.Failed(failed.Code);
            default:
                call = call with { Outcome = RunicTestOutcome.Dismissed };
                return new PickerResult<T>.Dismissed();
        }
    }

    private ValueTask<PickerResult<T>> Answer<T>(RunicFileDialogCall call, PickerResult<T> result) where T : IAsyncDisposable
    {
        lock (_gate) _calls.Add(call);
        _platform.Record("fileDialogs", Name(call.Kind), [
            ("outcome", call.Outcome.ToString()),
            ("title", call.OpenOptions?.Title ?? call.SaveOptions?.Title ?? call.DirectoryOptions?.Title),
            ("suggestedName", call.SaveOptions?.SuggestedName),
            ("files", call.Files.IsDefaultOrEmpty ? null : string.Join('\n', call.Files.Select(static file => file.DisplayName))),
        ]);
        return ValueTask.FromResult(result);
    }

    private static string Name(RunicFileDialogKind kind) => kind switch
    {
        RunicFileDialogKind.OpenFile => "openFile",
        RunicFileDialogKind.OpenFiles => "openFiles",
        RunicFileDialogKind.OpenDocument => "openDocument",
        RunicFileDialogKind.SaveFile => "saveFile",
        RunicFileDialogKind.SaveDocument => "saveDocument",
        RunicFileDialogKind.OpenDirectory => "openDirectory",
        _ => "openDirectories",
    };

    private static InvalidOperationException Mismatch(RunicFileDialogKind kind, Response response) => new(
        $"The application showed a {kind} dialog, but the next scripted response is {response.Describe()}. " +
        "Queue responses in the order the application shows its dialogs.");

    private void Enqueue(Response response)
    {
        lock (_gate) _responses.Enqueue(response);
    }

    private Response Next(out bool scripted)
    {
        _platform.Pump();
        lock (_gate)
        {
            scripted = _responses.TryDequeue(out var response);
            return response ?? new Response.Dismissed();
        }
    }

    private PlatformUnavailableReason? Closed() => _platform.IsClosed ? PlatformUnavailableReason.OwnerClosed : null;

    private static byte[] ReadSelected(RunicTestFile file) =>
        file.Current ?? throw new InvalidOperationException($"The selected test file '{file.DisplayName}' does not exist; an open dialog selects existing files.");

    private static RunicTestFile Existing(RunicTestFile file)
    {
        _ = ReadSelected(file);
        return file;
    }

    private T Track<T>(T lease) where T : TestLease
    {
        lock (_gate) _leases.Add(lease);
        return lease;
    }

    internal void Released(TestLease lease)
    {
        lock (_gate) _leases.Remove(lease);
    }

    internal void Committed(RunicTestFile file, FileCommitResult outcome, byte[] staged) =>
        _platform.Record("fileDialogs", "commit", [
            ("file", file.DisplayName),
            ("outcome", outcome.GetType().Name),
            ("code", outcome switch
            {
                FileCommitResult.NotCommitted failed => failed.Code.ToString(),
                FileCommitResult.CommitUnknown unknown => unknown.Code.ToString(),
                _ => null,
            }),
            ("content", outcome is FileCommitResult.Committed ? Convert.ToBase64String(staged) : null),
        ]);

    // Releases every lease, as a closing window does.
    internal async ValueTask ReleaseAllAsync()
    {
        TestLease[] leases;
        lock (_gate) leases = [.. _leases];
        foreach (var lease in leases) await lease.DisposeAsync().ConfigureAwait(false);
    }

    // The option checks of the real dialogs: the interface's default members run the shared checks.
    private static readonly IFileDialogs s_checks = new OptionChecks();

    private static void CheckOpen(OpenFileOptions options, CancellationToken cancellationToken) =>
        Completed(s_checks.OpenDocumentAsync(options, cancellationToken));

    private static void CheckSave(SaveFileOptions options, CancellationToken cancellationToken) =>
        Completed(s_checks.SaveDocumentAsync(options, cancellationToken));

    private static void CheckDirectory(OpenDirectoryOptions options, CancellationToken cancellationToken) =>
        Completed(s_checks.OpenDirectoryAsync(options, cancellationToken));

    // The default members complete at once; they throw before returning for options that break a rule.
    private static void Completed<T>(ValueTask<PickerResult<T>> check) where T : IAsyncDisposable
    {
        if (check.IsCompleted) _ = check.Result;
        else _ = check.AsTask().GetAwaiter().GetResult();
    }

    private sealed class OptionChecks : IFileDialogs
    {
        public ValueTask<PickerResult<IReadFileLease>> OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<PickerResult<ISaveFileLease>> SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private abstract record Response
    {
        public abstract string Describe();

        public sealed record Files(ImmutableArray<RunicTestFile> Selection, bool Save, FileDialogFilter? SelectedFilter) : Response
        {
            public override string Describe() => Save
                ? $"the save destination '{Selection[0].DisplayName}'"
                : $"the selection of {string.Join(", ", Selection.Select(static file => $"'{file.DisplayName}'"))}";
        }

        public sealed record Directories(ImmutableArray<string> Paths) : Response
        {
            public override string Describe() => $"the selection of {Paths.Length} directories";
        }

        public sealed record Dismissed : Response
        {
            public override string Describe() => "a dismissal";
        }

        public sealed record Unavailable(PlatformUnavailableReason Reason) : Response
        {
            public override string Describe() => $"unavailability ({Reason})";
        }

        public sealed record Failed(PlatformFailureCode Code) : Response
        {
            public override string Describe() => $"a failure ({Code})";
        }
    }

    internal void EnqueueScripted(string operation, IReadOnlyDictionary<string, string?> values)
    {
        switch (operation)
        {
            case "open":
                EnqueueOpen(new RunicTestFile(Required(values, "name"), Convert.FromBase64String(values.GetValueOrDefault("content") ?? "")));
                break;
            case "save":
                EnqueueSave(Required(values, "name"));
                break;
            case "openDirectory":
                EnqueueOpenDirectory(Required(values, "path").Split('\n'));
                break;
            case "dismissed":
                EnqueueDismissed();
                break;
            case "unavailable":
                EnqueueUnavailable(Enum.Parse<PlatformUnavailableReason>(Required(values, "reason")));
                break;
            case "failed":
                EnqueueFailed(Enum.Parse<PlatformFailureCode>(Required(values, "code")));
                break;
            default:
                throw new InvalidOperationException($"Unknown scripted file dialog operation '{operation}'.");
        }
    }

    internal static string Required(IReadOnlyDictionary<string, string?> values, string name) =>
        values.GetValueOrDefault(name) ?? throw new InvalidOperationException($"The scripted operation has no '{name}'.");
}
