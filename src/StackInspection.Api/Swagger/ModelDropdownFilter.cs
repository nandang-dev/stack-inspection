using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using StackInspection.Application.Abstractions;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace StackInspection.Api.Swagger;

/// <summary>
/// Mengisi pilihan field <c>model</c> di form Collect SKU dengan daftar model di server, sehingga
/// Swagger UI menampilkannya sebagai dropdown.
/// </summary>
internal sealed class ModelDropdownFilter(IServiceProvider services) : IOperationFilter
{
    public const string FieldName = "model";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (operation.RequestBody?.Content is null)
        {
            return;
        }

        foreach (OpenApiMediaType media in operation.RequestBody.Content.Values)
        {
            if (media.Schema?.Properties is null || !media.Schema.Properties.TryGetValue(FieldName, out OpenApiSchema? field))
            {
                continue;
            }

            using IServiceScope scope = services.CreateScope();
            IModelCatalog catalog = scope.ServiceProvider.GetRequiredService<IModelCatalog>();
            field.Enum = [.. catalog.List().Select(m => (IOpenApiAny)new OpenApiString(m.Name))];
            field.Default = new OpenApiString(catalog.DefaultModelName);
            field.Description = $"Model deteksi (lihat GET /api/v1/models). Kosong = default ({catalog.DefaultModelName}).";
        }
    }
}
