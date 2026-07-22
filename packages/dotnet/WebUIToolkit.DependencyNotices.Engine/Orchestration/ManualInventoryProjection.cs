using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.DependencyNotices.Engine;

public static class ManualInventoryProjection
{
    public static InventoryAdapterResult Project(ManualScanResult scan, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        List<InventoryComponent> components = new(scan.Components.Count);
        Dictionary<string, ComponentNoticeMetadata> metadata = new(StringComparer.Ordinal);
        foreach (ManualDependencyComponent component in scan.Components)
        {
            string identity = component.PackageUrl.CanonicalValue;
            components.Add(new InventoryComponent(
                component.PackageUrl,
                component.DisplayName,
                component.Version,
                InventorySourceKind.Manual,
                DependencyScope.Runtime,
                IsDirect: true,
                component.LicenseExpression,
                Integrity: null,
                sourcePath,
                component.Evidence));
            metadata.Add(identity, new ComponentNoticeMetadata(
                component.IsModified,
                component.ModificationNotice));
        }

        components.Sort(InventoryComponentComparer.Instance);
        return new InventoryAdapterResult(components, scan.Diagnostics, metadata);
    }
}

public sealed class ManualInventoryAdapter : IInventoryAdapter
{
    public InventorySourceKind SourceKind => InventorySourceKind.Manual;

    public ValueTask<InventoryAdapterResult> ScanAsync(
        InventoryAdapterContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        context.OperationPolicy.EnsureOffline(NoticeOperation.Scan);

        ManualScanResult scan = ManualComponentScanner.Scan(
            context.RootDirectory,
            context.Input.RelativePath);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ManualInventoryProjection.Project(
            scan,
            context.Input.RelativePath));
    }
}
