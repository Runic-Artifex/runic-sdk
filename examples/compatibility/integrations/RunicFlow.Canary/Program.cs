using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RunicFlow;
using RunicFlow.CommunityToolkit;
using RunicFlow.Generators;
using RunicFlow.Operations;
using RunicFlow.RunicToolkit.Navigation;

namespace RunicArtifex.Examples.Flow;

internal static partial class Program
{
    public static async Task<int> Main()
    {
        OperationOutcome<int> operation = await new OperationRunner().TryRunAsync(
            new OperationRequest(new OperationKey("canary.operation")),
            static (context, _) =>
            {
                context.Report(new OperationProgress(1, "complete"));
                return ValueTask.FromResult(42);
            }).ConfigureAwait(false);

        FlowSessionId authority = FlowSessionId.Create();
        var viewModel = new CanaryViewModel();
        await using CommunityToolkitFlowProjection<CanaryViewModel> projection =
            CommunityToolkitFlowProjection.Create(
                authority,
                viewModel,
                static model => model.Title,
                static (model, value) => model.Title = value,
                static model => model.SubmitCommand);

        CommunityToolkitFlowDispatchResult title =
            await projection.SetTitleAsync(authority, "Published package").ConfigureAwait(false);
        CommunityToolkitFlowDispatchResult submit =
            await projection.SubmitAsync(authority).ConfigureAwait(false);

        bool valid = operation == OperationOutcome<int>.Succeeded(42)
            && title.Status == CommunityToolkitFlowDispatchStatus.Committed
            && title.Snapshot.Title == "Published package"
            && submit.Status == CommunityToolkitFlowDispatchStatus.Committed
            && viewModel.SubmissionCount == 1
            && FlowGeneratorDiagnosticCatalog.IsReservedId("RFLOW0001")
            && FlowGeneratorDiagnosticCatalog.OrderedDescriptors.Count == 10
            && CommunityToolkitProjectionHandoff.ProjectionAdapterIdentity ==
                CommunityToolkitFlowProjectionContract.AdapterIdentity
            && CommunityToolkitProjectionHandoff.FixtureMappings.Count ==
                CommunityToolkitFlowProjectionContract.Members.Count
            && typeof(NavigationDesktopCloseGuard).Assembly.GetName().Name ==
                "RunicFlow.RunicToolkit";
        if (!valid)
        {
            Console.Error.WriteLine("FAIL: published Runic Flow package canary.");
            return 1;
        }

        Console.WriteLine(
            $"{CommunityToolkitFlowProjectionContract.AdapterIdentity}: package-only managed and NativeAOT canary passed.");
        return 0;
    }

    private sealed partial class CanaryViewModel : ObservableObject
    {
        [ObservableProperty]
        private string? title = "Before";

        public int SubmissionCount { get; private set; }

        [RelayCommand]
        private void Submit()
        {
            SubmissionCount++;
        }
    }
}
