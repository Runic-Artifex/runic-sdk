using System.Diagnostics.CodeAnalysis;

namespace Runic.Application.Views;

/// <summary>
/// A Runic application could not be hosted in another one's windows (the Runic host protocol, <c>runic-host/1</c>): the
/// guest's hosted start, or a host's start of a guest, failed.
/// </summary>
/// <remarks>
/// <para>
/// A guest started with <c>RunicDesktopHostOptions.Guest</c> throws it from <c>RunicDesktopHost.Run</c> before its body
/// runs, and a host or <c>RunicTestHost</c> throws it when it cannot start a guest. <see cref="Code"/> says why. The
/// codes are an open set; a later release can add some:
/// </para>
/// <list type="table">
/// <item><term><c>offer-missing</c></term><description>Guest: standard input ended, or the start timeout passed, before the host's offer.</description></item>
/// <item><term><c>offer-invalid</c></term><description>Guest: the offer line is not valid.</description></item>
/// <item><term><c>protocol-unsupported</c></term><description>The host and the guest share no protocol version.</description></item>
/// <item><term><c>host-not-accepted</c></term><description>Guest: <c>RunicGuestOptions.AcceptHost</c> declined the host.</description></item>
/// <item><term><c>guest-elevated</c>, <c>guest-other-user</c></term><description>Guest: it runs elevated, or as another user than its parent.</description></item>
/// <item><term><c>network-exposure</c></term><description>Guest: its Desktop host listens on other interfaces than loopback.</description></item>
/// <item><term><c>handshake-timeout</c>, <c>handshake-invalid</c>, <c>host-gone</c></term><description>Guest: no valid authenticated <c>hello</c> arrived in time, or standard input ended first.</description></item>
/// <item><term><c>guest-start-timeout</c>, <c>guest-exited</c>, <c>guest-not-hostable</c>, <c>guest-refused</c></term><description>Host: the guest wrote no channel line or answered no <c>hello</c> in time, exited first, could not be authenticated, or refused for another reason (<see cref="GuestCode"/>).</description></item>
/// </list>
/// <para>The protocol and this type are experimental; see <c>docs/experimental.md</c>.</para>
/// </remarks>
[Experimental("RUNICDESK103", UrlFormat = RunicReleaseLinks.ExperimentalUrlFormat)]
public sealed class RunicHostedApplicationException : Exception
{
    /// <summary>Creates the exception with its failure code and message.</summary>
    /// <param name="code">The failure code, a kebab-case token.</param>
    /// <param name="message">A human-readable explanation.</param>
    public RunicHostedApplicationException(string code, string message)
        : this(code, message, null)
    {
    }

    /// <summary>Creates the exception with its failure code, message and cause.</summary>
    /// <param name="code">The failure code, a kebab-case token.</param>
    /// <param name="message">A human-readable explanation.</param>
    /// <param name="innerException">The failure that caused it, if any.</param>
    public RunicHostedApplicationException(string code, string message, Exception? innerException)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    /// <summary>Why the hosted start failed: a kebab-case token of the open set listed on the type.</summary>
    public string Code { get; }

    /// <summary>
    /// For a host's <c>guest-refused</c> or <c>protocol-unsupported</c>: the code of the guest's refusal line, such as
    /// <c>host-not-accepted</c>; otherwise <see langword="null"/>.
    /// </summary>
    public string? GuestCode { get; init; }
}
