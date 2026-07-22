using WebUIToolkit.DependencyNotices.Security.Tests;

TestHarness harness = new();
ManualInputSecurityTests.Register(harness);
PathSecurityTests.Register(harness);
RenderingSecurityTests.Register(harness);
AcquisitionSecurityTests.Register(harness);
return await harness.RunAsync().ConfigureAwait(false);
