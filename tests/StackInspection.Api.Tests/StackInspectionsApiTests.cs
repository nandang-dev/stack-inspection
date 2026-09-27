using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using StackInspection.Application.Contracts;

namespace StackInspection.Api.Tests;

/// <summary>Integration test controller dengan fake vision (fixture JSON).</summary>
public sealed class StackInspectionsApiTests : IClassFixture<StackInspectionsApiTests.FakeVisionFactory>
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly FakeVisionFactory _factory;

    public StackInspectionsApiTests(FakeVisionFactory factory)
    {
        _factory = factory;
    }

    public sealed class FakeVisionFactory : WebApplicationFactory<Program>
    {
        public string? FixturePath { get; init; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vision:UseFake"] = "true",
                ["Vision:FakeFixturePath"] = FixturePath ?? "fixtures/reference-grid.json",
                ["Swagger:Enabled"] = "true",
            }));
        }
    }

    private static MultipartFormDataContent Upload(byte[] image, params string[] candidates)
    {
        MultipartFormDataContent form = [];
        ByteArrayContent file = new(image);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "foto.jpg");
        foreach (string candidate in candidates)
        {
            form.Add(new StringContent(candidate), "candidateSkus");
        }

        return form;
    }

    private static readonly MasterSkuDto[] Master =
    [
        new() { Sku = "68410975", Name = "Royco Ayam 576 x 8g", MaxStack = 8, Weight = 5.2, Class = "B" },
        new() { Sku = "68582213", Name = "Bango Kecap Manis 24 x 210ml", MaxStack = 12, Weight = 7.8, Class = "C" },
        new() { Sku = "68140913", Name = "SariWangi Teh Asli 48 x TB30 x 1.85g", MaxStack = 7, Weight = 3.1, Class = "A" },
    ];

    [Fact]
    public async Task CollectThenAnalyze_ReferenceFixture_Scores41_67()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage collectResponse = await client.PostAsync(
            "/api/v1/stack-inspections/collect-sku", Upload(Jpeg, "68410975", "68582213", "68140913"));
        Assert.Equal(HttpStatusCode.OK, collectResponse.StatusCode);
        CollectSkuResponse? collect = await collectResponse.Content.ReadFromJsonAsync<CollectSkuResponse>(Json);
        Assert.NotNull(collect);
        Assert.Equal((48, 8, 6), (collect.Cells.Count, collect.RowCount, collect.ColumnCount));
        Assert.Equal("fake-model", collect.Model);
        Assert.Equal(
            [("68140913", 21), ("68410975", 17), ("68582213", 10)],
            collect.DistinctSkus.Select(d => (d.Sku, d.Count)));

        using HttpResponseMessage analyzeResponse = await client.PostAsJsonAsync(
            "/api/v1/stack-inspections/analyze", new AnalyzeStackRequest { CollectResult = collect, MasterSkus = Master }, Json);
        Assert.Equal(HttpStatusCode.OK, analyzeResponse.StatusCode);
        AnalyzeStackResponse? analyze = await analyzeResponse.Content.ReadFromJsonAsync<AnalyzeStackResponse>(Json);
        Assert.NotNull(analyze);
        Assert.Equal((20, 48, 41.67), (analyze.TotalScore, analyze.MaxPossibleScore, analyze.PercentageScore));
        Assert.Equal(collect.InspectionId, analyze.InspectionId);
    }

    [Fact]
    public async Task Collect_JsonUsesCamelCaseAndStringEnums()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsync("/api/v1/stack-inspections/collect-sku", Upload(Jpeg));
        JsonNode? body = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("Matched", body?["cells"]?[0]?["readStatus"]?.GetValue<string>());
        Assert.NotNull(body?["cells"]?[0]?["boundingBox"]?["x1"]);
        Assert.NotNull(body?["excludedBackLayerCount"]);
    }

    [Fact]
    public async Task Collect_UnsupportedFormat_Returns400()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsync("/api/v1/stack-inspections/collect-sku", Upload("GIF89a"u8.ToArray()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Collect_LowResolution_Returns422PhotoNotInspectable()
    {
        string fixture = Path.Combine(Path.GetTempPath(), $"lowres-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(fixture, """{"imageWidth":1600,"imageHeight":1200,"overlayTexts":[],"cartons":[]}""");
        using FakeVisionFactory lowRes = new() { FixturePath = fixture };
        using HttpClient client = lowRes.CreateClient();

        using HttpResponseMessage response = await client.PostAsync("/api/v1/stack-inspections/collect-sku", Upload(Jpeg));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        JsonNode? problem = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("PHOTO_NOT_INSPECTABLE", problem?["code"]?.GetValue<string>());
        Assert.Equal("PHOTO_RESOLUTION_TOO_LOW", problem?["reason"]?.GetValue<string>());
        File.Delete(fixture);
    }

    [Fact]
    public async Task Analyze_InvalidClass_Returns400WithFieldError()
    {
        using HttpClient client = _factory.CreateClient();
        AnalyzeStackRequest request = new()
        {
            CollectResult = new CollectSkuResponse { Cells = [new CellDto { Sku = "68140913" }] },
            MasterSkus = [new MasterSkuDto { Sku = "68140913", Name = "x", MaxStack = 7, Class = "D" }],
        };

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/stack-inspections/analyze", request, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonNode? problem = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.NotNull(problem?["errors"]?["masterSkus[0]"]);
    }

    [Fact]
    public async Task Swagger_HasExamplesFor200And422()
    {
        using HttpClient client = _factory.CreateClient();

        string swagger = await client.GetStringAsync("/swagger/v1/swagger.json");
        JsonNode? doc = JsonNode.Parse(swagger);
        JsonNode? collect = doc?["paths"]?["/api/v1/stack-inspections/collect-sku"]?["post"]?["responses"];
        JsonNode? analyze = doc?["paths"]?["/api/v1/stack-inspections/analyze"]?["post"];

        Assert.NotNull(collect?["200"]?["content"]?["application/json"]?["example"]);
        Assert.NotNull(collect?["422"]?["content"]?["application/json"]?["example"]);
        Assert.NotNull(analyze?["responses"]?["200"]?["content"]?["application/json"]?["example"]);
        Assert.NotNull(analyze?["requestBody"]?["content"]?["application/json"]?["example"]);
        Assert.Contains("Collect SKU", swagger, StringComparison.Ordinal); // XML comment controller ikut tampil
    }

    [Fact]
    public async Task Models_ListReturnsCatalogWithDefault()
    {
        using HttpClient client = _factory.CreateClient();

        ModelInfoDto[]? models = await client.GetFromJsonAsync<ModelInfoDto[]>("/api/v1/models", Json);

        ModelInfoDto model = Assert.Single(models!);
        Assert.Equal(("fake-model", true), (model.Name, model.IsDefault));
    }

    [Fact]
    public async Task Collect_UnknownModel_Returns400()
    {
        using HttpClient client = _factory.CreateClient();
        using MultipartFormDataContent form = Upload(Jpeg);
        form.Add(new StringContent("carton-v9"), "model");

        using HttpResponseMessage response = await client.PostAsync("/api/v1/stack-inspections/collect-sku", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonNode? problem = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.NotNull(problem?["errors"]?["model"]);
    }

    [Fact]
    public async Task Swagger_ModelFieldIsDropdownOfAvailableModels()
    {
        using HttpClient client = _factory.CreateClient();

        JsonNode? doc = JsonNode.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        JsonNode? field = doc?["paths"]?["/api/v1/stack-inspections/collect-sku"]?["post"]?["requestBody"]?["content"]?["multipart/form-data"]?["schema"]?["properties"]?["model"];

        Assert.Equal(["fake-model"], field?["enum"]?.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("fake-model", field?["default"]?.GetValue<string>());
        Assert.NotNull(doc?["paths"]?["/api/v1/models"]?["get"]?["responses"]?["200"]?["content"]?["application/json"]?["example"]);
    }
}
