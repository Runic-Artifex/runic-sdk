using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));

WebApplication application = builder.Build();
application.MapGet("/ping", static (string value) => $"pong:{value}");
await application.RunAsync();
