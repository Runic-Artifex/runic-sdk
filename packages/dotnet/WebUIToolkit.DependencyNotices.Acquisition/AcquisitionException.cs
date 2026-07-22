using System;

namespace WebUIToolkit.DependencyNotices.Acquisition;

public sealed class AcquisitionException : InvalidOperationException
{
    public AcquisitionException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        Code = code;
    }

    public AcquisitionException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        Code = code;
    }

    public string Code { get; }
}
