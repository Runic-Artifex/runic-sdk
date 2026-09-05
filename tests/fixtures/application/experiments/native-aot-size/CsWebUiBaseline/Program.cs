using System;
using System.Threading.Tasks;
using CsWebUi;
using NativeAotSizeComparison;

var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

using (var window = new WebUiWindow())
using (window.Bind("ping", static invocation =>
    WebUiResult.FromString($"pong:{invocation.GetString()}")))
using (window.Bind("complete", invocation =>
{
    if (invocation.GetString() == "pong:comparison")
    {
        completed.TrySetResult();
    }

    return WebUiResult.None;
}))
{
    window.Show(ComparisonContent.Html);
    await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
    window.Close();
}

WebUiApplication.Clean();
Console.WriteLine("cs-webui NativeAOT comparison interaction passed.");
