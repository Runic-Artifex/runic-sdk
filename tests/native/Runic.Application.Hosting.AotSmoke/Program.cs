using System;
using System.Collections.Generic;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Hosting;

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
HostedServiceAdmissionPolicy policy = HostedServiceAdmissionPolicy.CreateInitial(
    new Uri("https://runic.example.test"),
    new HashSet<IPAddress> { IPAddress.Parse("10.0.0.10") });
builder.Services.AddRunicHostedServiceAdmission(policy);

WebApplication application = builder.Build();
RouteGroupBuilder service = application.MapRunicHostedService(policy);
service.MapPost("/command", static () => Results.Ok())
    .RequireRunicServiceRole("operator");

Console.WriteLine("Runic.Application.Hosting NativeAOT route smoke passed.");
return 0;
