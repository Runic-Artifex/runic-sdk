using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WebUIToolkit.Htmx.Sample;

internal sealed partial class SampleViewModel : ObservableValidator
{
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required]
    private string title = "ready";

    public int SubmissionCount { get; private set; }

    public bool WasCancelled { get; private set; }

    public TaskCompletionSource CommandStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [RelayCommand(IncludeCancelCommand = true, FlowExceptionsToTaskScheduler = true)]
    private async Task SubmitAsync(CancellationToken cancellationToken)
    {
        CommandStarted.TrySetResult();
        if (StringComparer.Ordinal.Equals(Title, "hold"))
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                WasCancelled = true;
                throw;
            }
        }
        else
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
            SubmissionCount++;
        }
    }
}
