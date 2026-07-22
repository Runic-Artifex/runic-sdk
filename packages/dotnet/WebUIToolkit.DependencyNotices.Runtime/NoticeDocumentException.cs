using System;

namespace WebUIToolkit.DependencyNotices.Runtime;

public sealed class NoticeDocumentException : Exception
{
    public NoticeDocumentException(string message)
        : base(message)
    {
    }

    public NoticeDocumentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
