using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Runic.Platform.Runtime;
using static Runic.Platform.Windows.WindowsNotificationInterop;

namespace Runic.Platform.Windows;

internal sealed class WindowsDesktopNotifications(string applicationId) : IDesktopNotifications
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Toast> _toasts = new(StringComparer.Ordinal);
    private volatile bool _disposed;
    public event EventHandler<DesktopNotificationActivation>? Activated;
    public ValueTask<PlatformResult<Unit>> RequestPermissionAsync(CancellationToken cancellationToken = default) => RunAsync(notifier =>
    {
        unsafe
        {
            int setting = 0; Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(notifier, 8))(notifier, &setting));
            return setting == 0 ? Success() : new PlatformResult<Unit>.Failed(FailureCode.PermissionDenied);
        }
    }, cancellationToken);
    public ValueTask<PlatformResult<Unit>> ShowAsync(DesktopNotification notification, CancellationToken cancellationToken = default)
    {
        DesktopServiceValidation.Notification(notification);
        return RunAsync(notifier =>
        {
            unsafe
            {
                int setting = 0; Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(notifier, 8))(notifier, &setting));
                if (setting != 0) return new PlatformResult<Unit>.Failed(FailureCode.PermissionDenied);
            }
            if (_toasts.Count >= 64 && !_toasts.ContainsKey(notification.Id)) return new PlatformResult<Unit>.Failed(FailureCode.ResourceBusy);
            nint document = 0, io = 0, factory = 0, toast = 0, properties = 0, handler = 0; long token = 0;
            try
            {
                var raw = Activate("Windows.Data.Xml.Dom.XmlDocument");
                try { document = Query(raw, "f7f3a506-1e87-42d6-bcfb-b8c809fa5494"); } finally { Release(raw); }
                io = Query(document, "6cd0e74e-ee65-4489-9ebf-ca43e87ba637");
                using (var xml = new HString(ToastXml(notification))) Call(io, 6, xml.Handle);
                factory = Factory("Windows.UI.Notifications.ToastNotification", "04124b20-82c6-4229-b109-fd9ed4662b53");
                toast = Get(factory, 6, document); properties = Query(toast, "9dfb9fd1-143a-490e-90bf-b9fba7132de7");
                using (var tag = new HString(Tag(notification.Id))) Call(properties, 6, tag.Handle);
                using (var group = new HString("runic")) Call(properties, 8, group.Handle);
                handler = ToastActivationHandler.Create(action => Raise(notification, action));
                unsafe { Check(((delegate* unmanaged[Stdcall]<nint, nint, long*, int>)Slot(toast, 11))(toast, handler, &token)); }
                Call(notifier, 6, toast);
                if (_toasts.Remove(notification.Id, out var previous)) previous.Dispose();
                _toasts[notification.Id] = new Toast(toast, token); toast = 0;
                return Success();
            }
            finally { if (toast != 0 && token != 0) { unsafe { _ = ((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(toast, 12))(toast, token); } } Release(handler); Release(properties); Release(toast); Release(factory); Release(io); Release(document); }
        }, cancellationToken);
    }
    public ValueTask<PlatformResult<Unit>> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        DesktopServiceValidation.Identifier(id);
        return RunAsync(_ =>
        {
            nint manager = 0, history = 0;
            try
            {
                manager = Factory("Windows.UI.Notifications.ToastNotificationManager", "7ab93c52-0e48-4750-ba9d-1a4113981847"); history = Get(manager, 6);
                using var tag = new HString(Tag(id)); using var group = new HString("runic"); using var app = new HString(applicationId);
                unsafe { Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int>)Slot(history, 8))(history, tag.Handle, group.Handle, app.Handle)); }
                if (_toasts.Remove(id, out var toast)) toast.Dispose(); return Success();
            }
            finally { Release(history); Release(manager); }
        }, cancellationToken);
    }
    private async ValueTask<PlatformResult<Unit>> RunAsync(Func<nint, PlatformResult<Unit>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                int initialized = RoInitialize(1); Check(initialized);
                nint factory = 0, notifier = 0;
                try
                {
                    factory = Factory("Windows.UI.Notifications.ToastNotificationManager", "50ac103f-d235-4598-bbef-98fe4d1a3ad4");
                    using var app = new HString(applicationId); notifier = Get(factory, 7, app.Handle);
                    return action(notifier);
                }
                finally { Release(notifier); Release(factory); RoUninitialize(); }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException) { return new PlatformResult<Unit>.Failed(FailureCode.PermissionDenied); }
        catch (COMException error)
        {
            return error.HResult == unchecked((int)0x80070005)
            ? new PlatformResult<Unit>.Failed(FailureCode.PermissionDenied) : new PlatformResult<Unit>.Unavailable(UnavailableReason.BackendUnavailable);
        }
        finally { _gate.Release(); }
    }
    internal static string ToastXml(DesktopNotification notification)
    {
        var toast = new XElement("toast", new XAttribute("launch", notification.ActivationUri is null ? "default" : ActivationTarget(notification, "default")));
        if (notification.ActivationUri is not null) toast.SetAttributeValue("activationType", "protocol");
        toast.Add(new XElement("visual", new XElement("binding", new XAttribute("template", "ToastGeneric"), new XElement("text", notification.Title), new XElement("text", notification.Body))));
        if (!notification.Actions.IsEmpty) toast.Add(new XElement("actions", notification.Actions.Select(a => new XElement("action", new XAttribute("content", a.Label), new XAttribute("arguments", notification.ActivationUri is null ? a.Id : ActivationTarget(notification, a.Id)), new XAttribute("activationType", notification.ActivationUri is null ? "foreground" : "protocol")))));
        return toast.ToString(SaveOptions.DisableFormatting);
    }
    private static string ActivationTarget(DesktopNotification notification, string action)
    {
        var target = new UriBuilder(notification.ActivationUri!);
        target.Query = (target.Query.Length > 1 ? target.Query[1..] + "&" : "")
            + "runic-notification=" + Uri.EscapeDataString(notification.Id) + "&runic-action=" + Uri.EscapeDataString(action);
        return target.Uri.AbsoluteUri;
    }
    private static string Tag(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).Substring(0, 16);
    private void Raise(DesktopNotification notification, string action)
    {
        if (action != "default" && !notification.Actions.Any(a => a.Id == action)) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (_disposed) return;
            foreach (EventHandler<DesktopNotificationActivation> listener in Activated?.GetInvocationList() ?? [])
                try { listener(this, new(notification.Id, action, notification.ActivationUri)); } catch { }
        });
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return; _disposed = true; Activated = null;
            await Task.Run(() => { int initialized = RoInitialize(1); Check(initialized); try { foreach (var toast in _toasts.Values) toast.Dispose(); _toasts.Clear(); } finally { RoUninitialize(); } }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    private static PlatformResult<Unit> Success() => new PlatformResult<Unit>.Success(new Unit());
    private sealed class Toast(nint instance, long token) : IDisposable
    {
        public unsafe void Dispose()
        { _ = ((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(instance, 12))(instance, token); Release(instance); }
    }
}
