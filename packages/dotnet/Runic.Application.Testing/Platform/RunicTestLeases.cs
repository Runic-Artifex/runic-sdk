using System.Collections.Immutable;
using Runic.Platform;

namespace Runic.Application.Testing;

// The leases of RunicTestFileDialogs, following the rules of Runic.Platform.Runtime's FileLeases: a read lease is
// consumed once; a save lease writes through one open transaction at a time, and a commit compares the file with the
// content the application last saw. Each lease is released with its dialogs when the test window closes.
internal abstract class TestLease(RunicTestFileDialogs owner) : IAsyncDisposable
{
    private protected readonly object _gate = new();
    private bool _closed;

    private protected bool IsClosed
    {
        get { lock (_gate) return _closed; }
    }

    private protected void RequireOpen()
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_closed, this);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
        }
        await ReleaseAsync().ConfigureAwait(false);
        owner.Released(this);
    }

    private protected virtual ValueTask ReleaseAsync() => ValueTask.CompletedTask;
}

internal sealed class TestReadLease(RunicTestFileDialogs owner, string displayName, byte[] content) : TestLease(owner), IReadFileLease
{
    private Stream? _stream;
    private bool _opened;

    public string DisplayName => displayName;

    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            RequireOpen();
            if (_opened) throw new InvalidOperationException("A read lease can be consumed once.");
            _opened = true;
            _stream = new MemoryStream(content, writable: false);
            return ValueTask.FromResult(_stream);
        }
    }

    private protected override async ValueTask ReleaseAsync()
    {
        if (_stream is { } stream) await stream.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class TestLeaseList<T>(RunicTestFileDialogs owner, ImmutableArray<T> leases)
    : TestLease(owner), IReadFileLeaseList, IDirectoryLeaseList where T : IAsyncDisposable
{
    public int Count => leases.Length;

    IReadFileLease IReadOnlyList<IReadFileLease>.this[int index] => (IReadFileLease)leases[index];
    IDirectoryLease IReadOnlyList<IDirectoryLease>.this[int index] => (IDirectoryLease)leases[index];

    IEnumerator<IReadFileLease> IEnumerable<IReadFileLease>.GetEnumerator() =>
        leases.Cast<IReadFileLease>().GetEnumerator();
    IEnumerator<IDirectoryLease> IEnumerable<IDirectoryLease>.GetEnumerator() =>
        leases.Cast<IDirectoryLease>().GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => ((System.Collections.IEnumerable)leases).GetEnumerator();

    private protected override async ValueTask ReleaseAsync()
    {
        foreach (var lease in leases) await lease.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class TestDirectoryLease(RunicTestFileDialogs owner, string path) : TestLease(owner), IDirectoryLease
{
    public string DisplayName
    {
        get
        {
            RequireOpen();
            var trimmed = Path.TrimEndingDirectorySeparator(path);
            var name = Path.GetFileName(trimmed);
            return name.Length == 0 ? path : name;
        }
    }

    public string LocalPath
    {
        get
        {
            RequireOpen();
            return path;
        }
    }
}

internal class TestSaveLease(RunicTestFileDialogs owner, RunicTestFile file) : TestLease(owner), ISaveFileLease
{
    // The contents the application last saw; null is an absent file. More than one only after an uncertain commit.
    private ImmutableArray<byte[]?> _baseline = [file.Current];
    private TestWriteTransaction? _open;
    private readonly HashSet<TestWriteTransaction> _transactions = [];

    private protected RunicTestFile File => file;

    public string DisplayName => file.DisplayName;

    public ValueTask<PlatformResult<IFileWriteTransaction>> BeginWriteAsync(FileWritePolicy policy, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        lock (_gate)
        {
            RequireOpen();
            if (file.WriteUnavailable is { } reason)
                return Result(new PlatformResult<IFileWriteTransaction>.Unavailable(reason));
            if (_open is not null) return Result(new PlatformResult<IFileWriteTransaction>.Failed(PlatformFailureCode.ResourceBusy));
            var transaction = new TestWriteTransaction(this);
            _open = transaction;
            _transactions.Add(transaction);
            return Result(new PlatformResult<IFileWriteTransaction>.Success(transaction));
        }

        static ValueTask<PlatformResult<IFileWriteTransaction>> Result(PlatformResult<IFileWriteTransaction> result) =>
            ValueTask.FromResult(result);
    }

    internal bool IsBaseline(byte[]? current)
    {
        lock (_gate)
        {
            foreach (var seen in _baseline)
                if (seen is null ? current is null : current is not null && seen.AsSpan().SequenceEqual(current)) return true;
            return false;
        }
    }

    internal FileCommitResult Commit(TestWriteTransaction transaction, byte[] staged)
    {
        var outcome = file.Commit(IsBaseline, staged);
        lock (_gate)
        {
            if (ReferenceEquals(_open, transaction)) _open = null;
            if (outcome is FileCommitResult.Committed) _baseline = [staged];
            else if (outcome is FileCommitResult.CommitUnknown && !IsBaseline(staged)) _baseline = _baseline.Add(staged);
        }
        Owner.Committed(file, outcome, staged);
        return outcome;
    }

    internal void Finished(TestWriteTransaction transaction)
    {
        lock (_gate) if (ReferenceEquals(_open, transaction)) _open = null;
    }

    internal void Closed(TestWriteTransaction transaction)
    {
        lock (_gate)
        {
            _transactions.Remove(transaction);
            if (ReferenceEquals(_open, transaction)) _open = null;
        }
    }

    private protected void Observe(byte[]? content)
    {
        lock (_gate) _baseline = [content];
    }

    private protected RunicTestFileDialogs Owner { get; } = owner;

    private protected override async ValueTask ReleaseAsync()
    {
        TestWriteTransaction[] transactions;
        lock (_gate) transactions = [.. _transactions];
        foreach (var transaction in transactions) await transaction.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class TestDocumentLease(RunicTestFileDialogs owner, RunicTestFile file) : TestSaveLease(owner, file), IDocumentFileLease
{
    private readonly List<Stream> _reads = [];

    public string LocalPath
    {
        get
        {
            RequireOpen();
            return File.LocalPath;
        }
    }

    public ValueTask<PlatformResult<Stream>> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            RequireOpen();
            var content = File.Current;
            Observe(content);
            if (content is null)
                return ValueTask.FromResult<PlatformResult<Stream>>(new PlatformResult<Stream>.Failed(PlatformFailureCode.NotFound));
            var stream = new MemoryStream(content, writable: false);
            _reads.RemoveAll(static read => !read.CanRead);
            _reads.Add(stream);
            return ValueTask.FromResult<PlatformResult<Stream>>(new PlatformResult<Stream>.Success(stream));
        }
    }

    private protected override async ValueTask ReleaseAsync()
    {
        await base.ReleaseAsync().ConfigureAwait(false);
        Stream[] reads;
        lock (_gate) reads = [.. _reads];
        foreach (var read in reads) await read.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class TestWriteTransaction(TestSaveLease lease) : IFileWriteTransaction
{
    private readonly object _gate = new();
    private readonly MemoryStream _content = new();
    private bool _committing;
    private bool _closed;

    public Stream Content => _content;

    public ValueTask<FileCommitResult> CommitAsync(CancellationToken cancellationToken = default)
    {
        byte[] staged;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_committing) throw new InvalidOperationException("Commit can be attempted exactly once; an uncertain outcome must not be retried.");
            _committing = true;
            if (cancellationToken.IsCancellationRequested)
            {
                lease.Finished(this);
                return ValueTask.FromCanceled<FileCommitResult>(cancellationToken);
            }
            staged = _content.ToArray();
        }
        return ValueTask.FromResult(lease.Commit(this, staged));
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
        }
        await _content.DisposeAsync().ConfigureAwait(false);
        lease.Closed(this);
    }
}
