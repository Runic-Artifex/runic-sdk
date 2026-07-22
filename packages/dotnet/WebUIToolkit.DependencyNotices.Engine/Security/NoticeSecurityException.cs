using System;

namespace WebUIToolkit.DependencyNotices.Engine;

public sealed class NoticeSecurityException : InvalidOperationException
{
    public NoticeSecurityException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
