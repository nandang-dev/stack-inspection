using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.OpenApi.Models;
using StackInspection.Api.Errors;
using StackInspection.Api.Swagger;
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

string[] corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    // "*" (atau kosong) = semua origin; selain itu hanya origin yang terdaftar.
    if (corsOrigins.Length == 0 || corsOrigins.Contains("*"))
    {
        policy.AllowAnyOrigin();
    }
    else
    {
        policy.WithOrigins(corsOrigins);
    }

    // Content-Disposition harus di-expose agar klien lintas origin (UI/Swagger di domain lain) bisa membaca
    // lampiran Preview; tanpa ini header disembunyikan browser dan HTML tampil sebagai teks, bukan unduhan.
    policy.AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Content-Disposition");
}));

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
builder.Services.AddInspectionConcurrency(builder.Configuration);

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
    options.OperationFilter<ModelDropdownFilter>();
});
builder.Services.AddSwaggerExamplesFromAssemblyOf<Program>();

WebApplication app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseRateLimiter();
if (app.Configuration.GetValue("Swagger:Enabled", app.Environment.IsDevelopment()))
{
    // Di balik reverse proxy dengan path (mis. vision.enerren.com/stack-inspection), Traefik membuang prefix
    // dan mengirimnya di X-Forwarded-Prefix. Prefix itu dipasang sebagai server agar "Try it out" memanggil
    // /stack-inspection/api/... dan bukan /api/... di root domain.
    app.UseSwagger(options => options.PreSerializeFilters.Add((document, request) =>
    {
        string? prefix = SwaggerPathPrefix.FromHeader(request.Headers["X-Forwarded-Prefix"]);
        if (prefix is not null)
        {
            document.Servers = [new OpenApiServer { Url = prefix }];
        }
    }));
    app.UseSwaggerUI();
}

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).ExcludeFromDescription();
app.Run();

/// <summary>Entry point (dipakai juga oleh WebApplicationFactory di integration test).</summary>
public partial class Program;
