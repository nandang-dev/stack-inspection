using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.OpenApi.Models;
using StackInspection.Api.Errors;
using StackInspection.Application;
using StackInspection.Application.Configuration;
using StackInspection.Application.Contracts;
using StackInspection.Infrastructure;
using Swashbuckle.AspNetCore.Filters;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services
    .AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

UploadOptions upload = builder.Configuration.GetSection(UploadOptions.SectionName).Get<UploadOptions>() ?? new UploadOptions();
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = upload.MaxFileSizeBytes + (1024 * 1024));

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Stack Inspection API",
        Version = "v1",
        Description = "Menilai kualitas tumpukan kardus dari foto: Collect SKU lalu Analyze.",
    });
    foreach (Assembly assembly in new[] { typeof(Program).Assembly, typeof(CollectSkuResponse).Assembly })
    {
        string xml = Path.Combine(AppContext.BaseDirectory, $"{assembly.GetName().Name}.xml");
        if (File.Exists(xml))
        {
            options.IncludeXmlComments(xml, includeControllerXmlComments: true);
        }
    }

    options.ExampleFilters();
});
builder.Services.AddSwaggerExamplesFromAssemblyOf<Program>();

WebApplication app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
if (app.Configuration.GetValue("Swagger:Enabled", app.Environment.IsDevelopment()))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();
app.Run();

/// <summary>Entry point (dipakai juga oleh WebApplicationFactory di integration test).</summary>
public partial class Program;
