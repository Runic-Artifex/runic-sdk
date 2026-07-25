using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.MVVM;
using WebUIToolkit.MVVM.CommunityToolkit;
using WebUIToolkit.MVVM.Html;
using WebUIToolkit.MVVM.Html.Htmx;

namespace WebUIToolkit.Htmx.Sample;

internal static class Program
{
    public static async Task<int> Main()
    {
        SampleViewModel? model = null;
        var contract = new MvvmContract("sample.htmx.counter");
        var registry = new MvvmSessionRegistry();
        registry.Map(contract, _ =>
        {
            model = new SampleViewModel();
            CommunityToolkitMvvmBindingAdapter<SampleViewModel> adapter =
                new CommunityToolkitMvvmAdapterBuilder<SampleViewModel>(model)
                    .BindProperty(
                        1,
                        nameof(SampleViewModel.Title),
                        static viewModel => viewModel.Title,
                        static (viewModel, value) => viewModel.Title = value,
                        SampleJsonContext.Default.String,
                        includeValidation: true)
                    .BindAsyncCommand(
                        2,
                        nameof(SampleViewModel.SubmitCommand),
                        static viewModel => viewModel.SubmitCommand)
                    .Build();
            return ValueTask.FromResult(new MvvmSessionActivation(adapter));
        });

        HtmxViewDescriptor descriptor = Descriptor(
            contract,
            () => model ?? throw new InvalidOperationException("The generated model is not active."));
        await using var runtime = new HtmxEndpointRuntime(
            registry.Build(),
            new HtmxEndpointOptions("https://sample.example", TimeSpan.FromMinutes(5)));
        await using HtmxOpenedView opened = await runtime.OpenAsync(descriptor);
        string route = opened.ActionRoutes[new HtmxActionHandle("submit")];

        CaptureTransport invalid = await SendAsync(
            runtime,
            Request(opened, route, 0, "", Guid.NewGuid()));
        CaptureTransport success = await SendAsync(
            runtime,
            Request(opened, route, 0, "<encoded-title>", Guid.NewGuid()));
        CaptureTransport stale = await SendAsync(
            runtime,
            Request(opened, route, 0, "ignored", Guid.NewGuid()));

        long revision = long.Parse(
            success.Response!.Headers["X-WebUI-Revision"],
            NumberStyles.None,
            CultureInfo.InvariantCulture);
        Guid runningId = Guid.NewGuid();
        Task<CaptureTransport> running = SendAsync(
            runtime,
            Request(opened, route, revision, "hold", runningId));
        await model!.CommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancel = new HtmxEndpointRequest(
            "DELETE",
            route,
            true,
            "https://sample.example",
            opened.SessionCookie,
            opened.AntiForgeryToken,
            opened.AntiForgeryToken,
            opened.Capability,
            revision,
            requestId: Guid.NewGuid(),
            targetRequestId: runningId);
        CaptureTransport cancelled = await SendAsync(runtime, cancel);
        _ = await running;

        bool passed =
            invalid.Response?.StatusCode == 200 &&
            invalid.Body.Contains("required", StringComparison.OrdinalIgnoreCase) &&
            success.Response?.StatusCode == 200 &&
            success.Body.Contains("&lt;encoded-title&gt;", StringComparison.Ordinal) &&
            success.Body.Contains("hx-swap-oob", StringComparison.Ordinal) &&
            stale.Response?.Headers["HX-Trigger"].Contains("webui:stale", StringComparison.Ordinal) == true &&
            model.WasCancelled &&
            (cancelled.Response?.Headers["HX-Trigger"].Contains("webui:cancel", StringComparison.Ordinal) == true ||
             cancelled.Response?.Headers["HX-Trigger"].Contains("webui:session-expired", StringComparison.Ordinal) == true);
        Console.WriteLine(passed
            ? "PASS: CommunityToolkit property/validation/async/cancel + primary/OOB/stale HTMX sample."
            : "FAIL: HTMX sample.");
        if (!passed)
        {
            Console.Error.WriteLine($"invalid={invalid.Response?.StatusCode}:{invalid.Body}");
            Console.Error.WriteLine($"success={success.Response?.StatusCode}:{success.Body}");
            Console.Error.WriteLine($"stale={stale.Response?.StatusCode}:{stale.Response?.Headers["HX-Trigger"]}");
            Console.Error.WriteLine($"cancel={cancelled.Response?.StatusCode}:{cancelled.Response?.Headers["HX-Trigger"]}");
        }

        return passed ? 0 : 1;
    }

    private static HtmxViewDescriptor Descriptor(
        MvvmContract contract,
        Func<SampleViewModel> getModel)
    {
        HtmxFragmentDescriptor form = new(
            new HtmxFragmentHandle("form"),
            context => Html.Fragment(
                (getModel(), context),
                static ((SampleViewModel Model, HtmxRenderContext Context) state, ref Utf8HtmlWriter writer, TemplateContext _) =>
                {
                    writer.BeginElement("label"u8);
                    writer.WriteAttribute("for"u8, "title");
                    writer.WriteText("Title");
                    writer.EndElement("label"u8);
                    writer.BeginElement("input"u8);
                    writer.WriteAttribute("id"u8, "title");
                    writer.WriteAttribute("name"u8, "title");
                    writer.WriteAttribute("value"u8, state.Model.Title);
                    if (state.Context.ValidationErrors.Count != 0)
                    {
                        writer.WriteAttribute("aria-invalid"u8, "true");
                        writer.WriteAttribute("aria-describedby"u8, state.Context.ValidationErrors[0].ValidationId);
                    }

                    foreach (HtmxValidationError error in state.Context.ValidationErrors)
                    {
                        writer.BeginElement("span"u8);
                        writer.WriteAttribute("id"u8, error.ValidationId);
                        writer.WriteAttribute("role"u8, "alert");
                        writer.WriteText(error.Message);
                        writer.EndElement("span"u8);
                    }
                }));
        HtmxFragmentDescriptor status = new(
            new HtmxFragmentHandle("status"),
            context => Html.Fragment(
                getModel(),
                static (SampleViewModel model, ref Utf8HtmlWriter writer, TemplateContext _) =>
                {
                    writer.BeginElement("output"u8);
                    writer.WriteAttribute("id"u8, "submission_count");
                    writer.WriteScalar(model.SubmissionCount);
                    writer.EndElement("output"u8);
                }));
        var success = new HtmxRenderPlan(
            form,
            [status],
            new HtmxDomHandle("title"),
            new HtmxDomHandle("status"),
            ["webui:submitted"]);
        var invalid = new HtmxRenderPlan(form, [status], new HtmxDomHandle("title"));
        var recovery = new HtmxRenderPlan(form, [status]);
        var field = new HtmxFieldDescriptor(
            new HtmxFieldHandle("title"),
            1,
            static (value, _) => HtmxConversionResult.Success(
                JsonSerializer.SerializeToElement(value, SampleJsonContext.Default.String)),
            [
                static (value, _) => ValueTask.FromResult<IReadOnlyList<string>>(
                    string.IsNullOrWhiteSpace(value.GetString()) ? ["The title is required."] : []),
            ]);
        var submit = new HtmxActionDescriptor(
            new HtmxActionHandle("submit"),
            2,
            [field],
            static (values, _) => ValueTask.FromResult(
                !string.IsNullOrWhiteSpace(values[new HtmxFieldHandle("title")].GetString())),
            success,
            invalid,
            recovery);
        return new HtmxViewDescriptor(new HtmxViewHandle("sample"), contract, [submit], success);
    }

    private static HtmxEndpointRequest Request(
        HtmxOpenedView opened,
        string route,
        long revision,
        string value,
        Guid requestId) =>
        new(
            "POST",
            route,
            true,
            "https://sample.example",
            opened.SessionCookie,
            opened.AntiForgeryToken,
            opened.AntiForgeryToken,
            opened.Capability,
            revision,
            [new HtmxFormValue("title", value)],
            Encoding.UTF8.GetByteCount(value),
            CultureInfo.InvariantCulture,
            requestId);

    private static async Task<CaptureTransport> SendAsync(
        HtmxEndpointRuntime runtime,
        HtmxEndpointRequest request)
    {
        var transport = new CaptureTransport();
        await runtime.HandleAsync(request, transport);
        return transport;
    }

    private sealed class CaptureTransport : IHtmxEndpointTransport
    {
        internal HtmxEndpointResponse? Response { get; private set; }
        internal string Body => Response is null ? string.Empty : Encoding.UTF8.GetString(Response.Body.Span);

        public ValueTask WriteAsync(HtmxEndpointResponse response, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Response = response;
            return ValueTask.CompletedTask;
        }
    }
}
