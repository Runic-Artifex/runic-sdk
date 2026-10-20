namespace Runic.Application.Testing;

/// <summary>
/// The status of an operation (<see cref="RunicOperationStatus.Kind"/>). The set is open: a minor release may add
/// statuses, so code that switches over it handles an unknown value, for example with a default arm.
/// </summary>
public enum RunicOperationStatusKind
{
    /// <summary>The window does not know the request (wire <c>unknown</c>).</summary>
    Unknown,
    /// <summary>The operation is running (wire <c>running</c>).</summary>
    Running,
    /// <summary>The operation completed; <see cref="RunicOperationStatus.Result"/> carries its result, if any (wire <c>succeeded</c>).</summary>
    Succeeded,
    /// <summary>The operation threw; <see cref="RunicOperationStatus.ErrorMessage"/> says why (wire <c>failed</c>).</summary>
    Failed,
    /// <summary>
    /// The operation ended with its declared failure (<c>[RunicFailure]</c>);
    /// <see cref="RunicOperationStatus.Failure"/> carries it (wire <c>domain-failed</c>).
    /// </summary>
    DomainFailed,
    /// <summary>The operation was cancelled (wire <c>cancelled</c>).</summary>
    Cancelled,
    /// <summary>The window no longer retains the operation's outcome (wire <c>expired</c>).</summary>
    Expired,
}

/// <summary>
/// How a <c>Start{Command}</c> route answered (<see cref="RunicTestOperation.Admission"/>). The set is open: a
/// minor release may add answers, so code that switches over it handles an unknown value.
/// </summary>
public enum RunicOperationAdmissionKind
{
    /// <summary>The operation started (wire <c>accepted</c>).</summary>
    Accepted,
    /// <summary>An operation with this request id and input already started; this is the same operation (wire <c>duplicate</c>).</summary>
    Duplicate,
    /// <summary>The request id belonged to an operation whose outcome is no longer retained (wire <c>expired</c>).</summary>
    Expired,
    /// <summary>
    /// The operation was not started, for example because the command cannot execute or its input is invalid;
    /// <see cref="RunicTestOperation.Reason"/> says why (wire <c>rejected</c>).
    /// </summary>
    Rejected,
    /// <summary>Starting the operation threw (wire <c>failed</c>).</summary>
    Failed,
    /// <summary>Starting the operation was cancelled (wire <c>cancelled</c>).</summary>
    Cancelled,
}

/// <summary>
/// How a checked field write ended (<see cref="RunicFieldWriteReceipt{TModel}.Kind"/>). The set is open: a minor
/// release may add outcomes, so code that switches over it handles an unknown value.
/// </summary>
public enum RunicFieldWriteReceiptKind
{
    /// <summary>The value was written (wire <c>applied</c>).</summary>
    Applied,
    /// <summary>The value was written, but its validation did not complete (wire <c>committed-with-error</c>).</summary>
    CommittedWithError,
    /// <summary>The baseline no longer matched; <see cref="RunicFieldWriteReceipt{TModel}.Value"/> is the current value (wire <c>conflict</c>).</summary>
    Conflict,
    /// <summary>The write was refused (wire <c>rejected</c>).</summary>
    Rejected,
    /// <summary>The route replied with an error instead of a receipt, for example after the window closed.</summary>
    Failed,
}

/// <summary>
/// How .NET answered a cancellation request (<see cref="RunicTestOperation.Cancel"/>). The set is open: a minor
/// release may add answers, so code that switches over it handles an unknown value.
/// </summary>
public enum RunicOperationCancelKind
{
    /// <summary>The running operation was asked to stop (wire <c>cancellation-requested</c>).</summary>
    CancellationRequested,
    /// <summary>The operation had already ended (wire <c>not-running</c>).</summary>
    NotRunning,
    /// <summary>The window does not know the request (wire <c>unknown</c>).</summary>
    Unknown,
    /// <summary>The window no longer retains the operation (wire <c>expired</c>).</summary>
    Expired,
}

// Wire tokens of the kinds above. Runic.Application.Testing references Runic.Application.Views at
// its exact version, so a token missing here is a protocol bug and fails loudly.
internal static class RunicOperationWire
{
    internal static RunicOperationStatusKind Status(string? token) => token switch
    {
        "unknown" => RunicOperationStatusKind.Unknown,
        "running" => RunicOperationStatusKind.Running,
        "succeeded" => RunicOperationStatusKind.Succeeded,
        "failed" => RunicOperationStatusKind.Failed,
        "domain-failed" => RunicOperationStatusKind.DomainFailed,
        "cancelled" => RunicOperationStatusKind.Cancelled,
        "expired" => RunicOperationStatusKind.Expired,
        _ => throw Unrecognized("operation status", token),
    };

    internal static RunicOperationAdmissionKind Admission(string? token) => token switch
    {
        "accepted" => RunicOperationAdmissionKind.Accepted,
        "duplicate" => RunicOperationAdmissionKind.Duplicate,
        "expired" => RunicOperationAdmissionKind.Expired,
        "rejected" => RunicOperationAdmissionKind.Rejected,
        "failed" => RunicOperationAdmissionKind.Failed,
        "cancelled" => RunicOperationAdmissionKind.Cancelled,
        _ => throw Unrecognized("operation admission", token),
    };

    internal static RunicFieldWriteReceiptKind Receipt(string? token) => token switch
    {
        "applied" => RunicFieldWriteReceiptKind.Applied,
        "committed-with-error" => RunicFieldWriteReceiptKind.CommittedWithError,
        "conflict" => RunicFieldWriteReceiptKind.Conflict,
        "rejected" => RunicFieldWriteReceiptKind.Rejected,
        _ => throw Unrecognized("field write receipt", token),
    };

    internal static RunicOperationCancelKind Cancel(string? token) => token switch
    {
        "cancellation-requested" => RunicOperationCancelKind.CancellationRequested,
        "not-running" => RunicOperationCancelKind.NotRunning,
        "unknown" => RunicOperationCancelKind.Unknown,
        "expired" => RunicOperationCancelKind.Expired,
        _ => throw Unrecognized("cancellation reply", token),
    };

    private static InvalidOperationException Unrecognized(string what, string? token) =>
        new($"The {what} '{token}' is not one Runic.Application.Testing knows; use the Runic.Application.Views version it was built with.");
}
