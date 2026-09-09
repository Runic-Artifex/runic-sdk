using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Runic.Platform.Runtime;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS;

internal sealed partial class MacDesktopNotifications : IDesktopNotifications
{
    private static readonly ConcurrentDictionary<nint, MacDesktopNotifications> Owners = new();
    private static readonly Lazy<nint> DelegateClass = new(CreateDelegateClass);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, nint> _categories = new(StringComparer.Ordinal);
    private nint _center, _delegate;
    private volatile bool _disposed;
    public event EventHandler<DesktopNotificationActivation>? Activated;
    public ValueTask<PlatformResult<Unit>> RequestPermissionAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(completion =>
        {
            var block = MacDesktopBlock.Authorization((granted, error) => completion.TrySetResult(error != 0 ? FromError(error) : granted != 0 ? Success() : Failed(FailureCode.PermissionDenied)));
            try { Args(_center, Sel("requestAuthorizationWithOptions:completionHandler:"), 4 | 2, block); }
            finally { MacDesktopBlock.Release(block); }
        }, cancellationToken);
    public ValueTask<PlatformResult<Unit>> ShowAsync(DesktopNotification notification, CancellationToken cancellationToken = default)
    {
        DesktopServiceValidation.Notification(notification);
        return ExecuteAsync(completion =>
        {
            if (_categories.Count >= 64 && !_categories.ContainsKey(notification.Id)) { completion.TrySetResult(Failed(FailureCode.ResourceBusy)); return; }
            var content = Send(Send(Class("UNMutableNotificationContent"), Sel("alloc")), Sel("init"));
            try
            {
                Arg(content, Sel("setTitle:"), String(notification.Title)); Arg(content, Sel("setBody:"), String(notification.Body));
                var actions = Send(Class("NSMutableArray"), Sel("array"));
                foreach (var action in notification.Actions)
                    Arg(actions, Sel("addObject:"), Args3(Class("UNNotificationAction"), Sel("actionWithIdentifier:title:options:"), String(action.Id), String(action.Label), 4)); // foreground
                var category = Return4(Class("UNNotificationCategory"), Sel("categoryWithIdentifier:actions:intentIdentifiers:options:"), String(notification.Id), actions, Send(Class("NSArray"), Sel("array")), 0);
                Send(category, Sel("retain"));
                if (_categories.Remove(notification.Id, out var old)) Release(old);
                _categories[notification.Id] = category;
                var categories = Send(Class("NSMutableSet"), Sel("set"));
                foreach (var value in _categories.Values) Arg(categories, Sel("addObject:"), value);
                Arg(_center, Sel("setNotificationCategories:"), categories);
                Arg(content, Sel("setCategoryIdentifier:"), String(notification.Id));
                if (notification.ActivationUri is { } uri)
                    Arg(content, Sel("setUserInfo:"), Args(Class("NSDictionary"), Sel("dictionaryWithObject:forKey:"), String(uri.AbsoluteUri), String("runic-uri")));
                var request = Args3(Class("UNNotificationRequest"), Sel("requestWithIdentifier:content:trigger:"), String(notification.Id), content, 0);
                var block = MacDesktopBlock.Create(error => completion.TrySetResult(error == 0 ? Success() : FromError(error)));
                try { Args(_center, Sel("addNotificationRequest:withCompletionHandler:"), request, block); }
                finally { MacDesktopBlock.Release(block); }
            }
            finally { Release(content); }
        }, cancellationToken);
    }
    public ValueTask<PlatformResult<Unit>> RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        DesktopServiceValidation.Identifier(id);
        return ExecuteAsync(completion =>
        {
            var ids = Array(String(id)); Arg(_center, Sel("removePendingNotificationRequestsWithIdentifiers:"), ids);
            Arg(_center, Sel("removeDeliveredNotificationsWithIdentifiers:"), ids);
            if (_categories.Remove(id, out var category)) Release(category);
            completion.TrySetResult(Success());
        }, cancellationToken);
    }
    private async ValueTask<PlatformResult<Unit>> ExecuteAsync(Action<TaskCompletionSource<PlatformResult<Unit>>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var completion = new TaskCompletionSource<PlatformResult<Unit>>(TaskCreationOptions.RunContinuationsAsynchronously);
            await MacOsMainQueue.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var pool = new Pool();
                if (!Initialize()) { completion.TrySetResult(new PlatformResult<Unit>.Unavailable(UnavailableReason.BackendUnavailable)); return; }
                action(completion);
            }).ConfigureAwait(false);
            // UN APIs have no cancellation primitive; drain their completion after submitting.
            return await completion.Task.ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    private bool Initialize()
    {
        if (_center != 0) return true;
        // Load Foundation before querying NSBundle, including pre-AppKit startup.
        _ = DelegateClass.Value;
        // UNUserNotificationCenter can raise an ObjC exception for an unbundled process.
        if (Text(Send(Send(Class("NSBundle"), Sel("mainBundle")), Sel("bundleIdentifier"))).Length == 0) return false;
        var center = Send(Class("UNUserNotificationCenter"), Sel("currentNotificationCenter"));
        if (center == 0 || Send(center, Sel("delegate")) != 0) return false;
        _center = Send(center, Sel("retain"));
        _delegate = Send(Send(DelegateClass.Value, Sel("alloc")), Sel("init")); Owners[_delegate] = this;
        Arg(_center, Sel("setDelegate:"), _delegate); return true;
    }
    private static unsafe nint CreateDelegateClass()
    {
        _ = NativeLibrary.Load("/System/Library/Frameworks/UserNotifications.framework/UserNotifications");
        var type = AllocateClass(Class("NSObject"), "RunicDesktopNotificationDelegate", 0);
        if (type == 0) throw new InvalidOperationException("Notification delegate class already exists.");
        AddProtocol(type, GetProtocol("UNUserNotificationCenterDelegate"));
        AddMethod(type, Sel("userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:"),
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&Response, "v@:@@@?");
        AddMethod(type, Sel("userNotificationCenter:willPresentNotification:withCompletionHandler:"),
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, void>)&WillPresent, "v@:@@@?");
        RegisterClass(type); return type;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Response(nint self, nint selector, nint center, nint response, nint completion)
    {
        try
        {
            using var pool = new Pool();
            if (!Owners.TryGetValue(self, out var owner)) return;
            var request = Send(Send(response, Sel("notification")), Sel("request"));
            var id = Text(Send(request, Sel("identifier")));
            var action = Text(Send(response, Sel("actionIdentifier")));
            if (action == "com.apple.UNNotificationDismissActionIdentifier") return;
            if (action == "com.apple.UNNotificationDefaultActionIdentifier") action = "default";
            DesktopServiceValidation.Identifier(id); DesktopServiceValidation.Identifier(action);
            var uriText = Text(Arg(Send(Send(request, Sel("content")), Sel("userInfo")), Sel("objectForKey:"), String("runic-uri")));
            var activation = new DesktopNotificationActivation(id, action, Uri.TryCreate(uriText, UriKind.Absolute, out var uri) && !uri.IsFile ? uri : null);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (owner._disposed) return;
                foreach (EventHandler<DesktopNotificationActivation> listener in owner.Activated?.GetInvocationList() ?? [])
                    try { listener(owner, activation); } catch { }
            });
        }
        catch { }
        finally { MacDesktopBlock.Complete(completion); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void WillPresent(nint self, nint selector, nint center, nint notification, nint completion) => MacDesktopBlock.Present(completion);
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return; _disposed = true; Activated = null;
            if (_center != 0) await MacOsMainQueue.InvokeAsync(() =>
            {
                using var pool = new Pool();
                if (Send(_center, Sel("delegate")) == _delegate) Arg(_center, Sel("setDelegate:"), 0);
                Owners.TryRemove(_delegate, out _); Release(_delegate); Release(_center); _delegate = _center = 0;
                foreach (var value in _categories.Values) Release(value); _categories.Clear();
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    private static PlatformResult<Unit> FromError(nint error) =>
        Text(Send(error, Sel("domain"))) == "UNErrorDomain" && Send(error, Sel("code")) == 1
            ? Failed(FailureCode.PermissionDenied) : Failed(FailureCode.IoError);
    private static PlatformResult<Unit> Success() => new PlatformResult<Unit>.Success(new Unit());
    private static PlatformResult<Unit> Failed(FailureCode code) => new PlatformResult<Unit>.Failed(code);
    [LibraryImport(ObjC, EntryPoint = "objc_allocateClassPair", StringMarshalling = StringMarshalling.Utf8)] private static partial nint AllocateClass(nint parent, string name, nuint extra);
    [LibraryImport(ObjC, EntryPoint = "objc_registerClassPair")] private static partial void RegisterClass(nint type);
    [LibraryImport(ObjC, EntryPoint = "objc_getProtocol", StringMarshalling = StringMarshalling.Utf8)] private static partial nint GetProtocol(string name);
    [LibraryImport(ObjC, EntryPoint = "class_addProtocol")] private static partial byte AddProtocol(nint type, nint protocol);
    [LibraryImport(ObjC, EntryPoint = "class_addMethod", StringMarshalling = StringMarshalling.Utf8)] private static partial byte AddMethod(nint type, nint selector, nint implementation, string types);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Return4(nint target, nint selector, nint a, nint b, nint c, nint d);
}
