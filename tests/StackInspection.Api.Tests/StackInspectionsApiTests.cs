using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackInspection.Api.Contracts;
using StackInspection.Application.Abstractions;
using StackInspection.Application.Configuration;
using StackInspection.Domain;
using StackInspection.Infrastructure.Fakes;
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
        public const string ApiKey = "test-key-planogram";

        public string? FixturePath { get; init; }

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vision:UseFake"] = "true",
                ["Vision:FakeFixturePath"] = FixturePath ?? "fixtures/reference-grid.json",
                ["Swagger:Enabled"] = "true",
                // vision palsu menyediakan teks lewat jalur OCR Paddle (recognizer palsu tidak membaca apa pun)
                ["Vision:PaddleOcrFallback"] = "true",
                ["AUTH_APIKeys"] = $"{ApiKey}, test-key-ai,",
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
    public async Task PreviewFromResult_CollectResponse_ReturnsSameHtmlAsPreview()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage collectResponse = await client.PostAsync(
            "/api/v1/stack-inspections/collect-sku", Upload(Jpeg, "68410975", "68582213", "68140913"));
        CollectSkuResponse? collect = await collectResponse.Content.ReadFromJsonAsync<CollectSkuResponse>(Json);

        string masters = Uri.EscapeDataString(JsonSerializer.Serialize(Master, Json));
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/stack-inspections/preview-from-result?masterSkus={masters}", collect, Json);
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains($"{collect!.InspectionId:N}", response.Content.Headers.ContentDisposition?.FileName, StringComparison.Ordinal);
        Assert.Contains("41.67%", html, StringComparison.Ordinal);
        Assert.Equal(48, html.Split("class=\"tile").Length - 1);
        Assert.DoesNotContain("Total token", html, StringComparison.Ordinal); // tokenUsage tidak diisi
    }

    [Fact]
    public async Task PreviewFromResult_WithTokenUsage_ShowsInputOutputAndTotal()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage collectResponse = await client.PostAsync("/api/v1/stack-inspections/collect-sku", Upload(Jpeg));
        CollectSkuResponse? collect = await collectResponse.Content.ReadFromJsonAsync<CollectSkuResponse>(Json);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/stack-inspections/preview-from-result?inputTokens=2140&outputTokens=31870", collect, Json);
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<dt>Token input</dt><dd>2,140</dd>", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Token output</dt><dd>31,870</dd>", html, StringComparison.Ordinal);
        Assert.Contains("<dt>Total token</dt><dd>34,010</dd>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Collect_ReturnsRequestLogs_AndPreviewShowsThem()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsync("/api/v1/stack-inspections/collect-sku", Upload(Jpeg));
        CollectSkuResponse? collect = await response.Content.ReadFromJsonAsync<CollectSkuResponse>(Json);
        using HttpResponseMessage preview = await client.PostAsync("/api/v1/stack-inspections/preview", Upload(Jpeg));
        string html = await preview.Content.ReadAsStringAsync();

        Assert.Contains(collect!.Logs, l => l.Source == "CollectSkuHandler" && l.Message.StartsWith("Collect SKU timing", StringComparison.Ordinal));
        Assert.Contains(collect.Logs, l => l.Message.StartsWith("Hasil akhir per kardus", StringComparison.Ordinal));
        Assert.Contains("<h2>Log proses</h2>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewFromResult_WithTimings_ShowsStageBreakdown()
    {
        using HttpClient client = _factory.CreateClient();
        JsonNode body = JsonNode.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"))!
            ["paths"]!["/api/v1/stack-inspections/preview-from-result"]!["post"]!["requestBody"]!["content"]!["application/json"]!["example"]!
            .DeepClone();
        body["processingTimeMs"] = 104027;
        body["timings"] = JsonNode.Parse("""{"decodeMs":23,"inspectMs":38,"detectMs":1142,"labelLocateMs":5636,"ocrMs":96666,"skuResolveMs":519,"buildMs":3}""");

        using StringContent content = new(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync("/api/v1/stack-inspections/preview-from-result", content);
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<dt>Label locator</dt><dd>5,636 ms &#183; 5%</dd>", html, StringComparison.Ordinal);
        Assert.Contains("<dt>OCR</dt><dd>96,666 ms &#183; 93%</dd>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewFromResult_WithLabelBoxes_DrawsThemOnPhoto()
    {
        using HttpClient client = _factory.CreateClient();
        JsonNode body = JsonNode.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"))!
            ["paths"]!["/api/v1/stack-inspections/preview-from-result"]!["post"]!["requestBody"]!["content"]!["application/json"]!["example"]!
            .DeepClone();
        body["labelBoxes"] = JsonNode.Parse("""[{"cartonId":0,"boundingBox":{"x1":10,"y1":20,"x2":110,"y2":60},"confidence":0.9,"text":"6487156","textConfidence":0.99}]""");

        using StringContent content = new(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        string html = await (await client.PostAsync("/api/v1/stack-inspections/preview-from-result", content)).Content.ReadAsStringAsync();

        Assert.Contains("<rect class=\"label\" data-id=\"0\" x=\"10\" y=\"20\" width=\"100\" height=\"40\"", html, StringComparison.Ordinal);
        Assert.Contains("Label #0: 6487156 (OCR 99%)", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewFromResult_WithoutTimings_HidesStageBreakdown()
    {
        using HttpClient client = _factory.CreateClient();
        JsonNode body = JsonNode.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"))!
            ["paths"]!["/api/v1/stack-inspections/preview-from-result"]!["post"]!["requestBody"]!["content"]!["application/json"]!["example"]!
            .DeepClone();
        body["timings"] = new JsonObject();

        using StringContent content = new(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        string html = await (await client.PostAsync("/api/v1/stack-inspections/preview-from-result", content)).Content.ReadAsStringAsync();

        Assert.DoesNotContain("<dt>Label locator</dt>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewFromResult_NegativeTokens_Returns400()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage collectResponse = await client.PostAsync("/api/v1/stack-inspections/collect-sku", Upload(Jpeg));
        CollectSkuResponse? collect = await collectResponse.Content.ReadFromJsonAsync<CollectSkuResponse>(Json);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/stack-inspections/preview-from-result?inputTokens=-1&outputTokens=10", collect, Json);
        string json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("InputTokens", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviewFromResult_SwaggerExample_IsAccepted()
    {
        using HttpClient client = _factory.CreateClient();
        using JsonDocument swagger = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        JsonElement example = swagger.RootElement
            .GetProperty("paths").GetProperty("/api/v1/stack-inspections/preview-from-result").GetProperty("post")
            .GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("example");

        using StringContent body = new(example.GetRawText(), System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync("/api/v1/stack-inspections/preview-from-result", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PreviewFromResult_InconsistentResult_Returns400WithAllErrors()
    {
        using HttpClient client = _factory.CreateClient();
        const string body = """
            {"imageWidth":0,"imageHeight":0,"rowCount":1,"columnCount":1,"cells":[
              {"id":0,"row":0,"column":0,"level":3,"restsOn":[],"sku":"68140913","readStatus":"Matched",
               "boundingBox":{"x1":0,"y1":0,"x2":10,"y2":10}}]}
            """;

        using StringContent content = new(body, System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync("/api/v1/stack-inspections/preview-from-result", content);
        string json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("imageWidth dan imageHeight", json, StringComparison.Ordinal);
        Assert.Contains("seharusnya 0", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_WithMasterSkus_ReturnsHtmlWithScoreAndViolations()
    {
        using HttpClient client = _factory.CreateClient();
        using MultipartFormDataContent form = Upload(Jpeg, "68410975", "68582213", "68140913");
        form.Add(new StringContent(JsonSerializer.Serialize(Master, Json)), "masterSkus");

        using HttpResponseMessage response = await client.PostAsync("/api/v1/stack-inspections/preview", form);
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.EndsWith(".html", response.Content.Headers.ContentDisposition?.FileName, StringComparison.Ordinal);
        Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
        Assert.Contains("41.67%", html, StringComparison.Ordinal);
        Assert.Contains("MAX_STACK_EXCEEDED", html, StringComparison.Ordinal);
        Assert.Equal(48, html.Split("class=\"tile").Length - 1); // 48 dus, satu kotak per id
        Assert.Contains("SariWangi Teh Asli", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_WithoutMasterSkus_ReturnsLayoutOnly()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsync("/api/v1/stack-inspections/preview", Upload(Jpeg));
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("masterSkus tidak diisi", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Pelanggaran</h2>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"conf\">", html, StringComparison.Ordinal); // keyakinan per dus
        Assert.Contains("Keyakinan OCR", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_CrossOrigin_ExposesContentDisposition()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/stack-inspections/preview") { Content = Upload(Jpeg) };
        request.Headers.Add("Origin", "https://ui.example.com");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Access-Control-Expose-Headers", out IEnumerable<string>? exposed));
        Assert.Contains("Content-Disposition", string.Join(',', exposed!), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Preview_InvalidMasterSkusJson_Returns400()
    {
        using HttpClient client = _factory.CreateClient();
        using MultipartFormDataContent form = Upload(Jpeg);
        form.Add(new StringContent("bukan json"), "masterSkus");

        using HttpResponseMessage response = await client.PostAsync("/api/v1/stack-inspections/preview", form);
        JsonNode? body = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(body?["errors"]?["masterSkus"]);
    }

    [Fact]
    public async Task Preview_LowResolution_Returns422Json()
    {
        string fixture = Path.Combine(Path.GetTempPath(), $"lowres-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(fixture, """{"imageWidth":1600,"imageHeight":1200,"overlayTexts":[],"cartons":[]}""");
        using FakeVisionFactory lowRes = new() { FixturePath = fixture };
        using HttpClient client = lowRes.CreateClient();

        using HttpResponseMessage response = await client.PostAsync("/api/v1/stack-inspections/preview", Upload(Jpeg));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        JsonNode? problem = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("PHOTO_NOT_INSPECTABLE", problem?["code"]?.GetValue<string>());
        File.Delete(fixture);
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
    public async Task Swagger_BehindPathPrefix_UsesForwardedPrefixAsServer()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/swagger/v1/swagger.json");
        request.Headers.Add("X-Forwarded-Prefix", "/stack-inspection");

        using HttpResponseMessage response = await client.SendAsync(request);
        JsonNode? doc = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("/stack-inspection", (string?)doc?["servers"]?[0]?["url"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://evil.example")]
    [InlineData("//evil.example")]
    public async Task Swagger_WithoutValidPrefix_HasNoServers(string? prefix)
    {
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/swagger/v1/swagger.json");
        if (prefix is not null)
        {
            request.Headers.Add("X-Forwarded-Prefix", prefix);
        }

        using HttpResponseMessage response = await client.SendAsync(request);
        JsonNode? doc = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        Assert.Null(doc?["servers"]);
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

    [Fact]
    public async Task Collect_WhileAnotherInspectionRuns_Returns429ServerBusy()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using WebApplicationFactory<Program> factory = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<ICartonDetector>(sp => new BlockingDetector(sp.GetRequiredService<FakeCartonDetector>(), started, release))));
        using HttpClient client = factory.CreateClient();

        Task<HttpResponseMessage> first = client.PostAsync("/api/v1/stack-inspections/collect-sku", Upload(Jpeg));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using HttpResponseMessage busy = await client.PostAsync("/api/v1/stack-inspections/preview", Upload(Jpeg));
        release.SetResult();
        using HttpResponseMessage done = await first;

        Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
        Assert.Equal("30", busy.Headers.GetValues("Retry-After").Single());
        JsonNode? problem = JsonNode.Parse(await busy.Content.ReadAsStringAsync());
        Assert.Equal("SERVER_BUSY", problem?["code"]?.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
    }

    /// <summary>Detector yang menahan proses sampai dilepas, untuk mensimulasikan inspection yang sedang berjalan.</summary>
    private sealed class BlockingDetector(ICartonDetector inner, TaskCompletionSource started, TaskCompletionSource release) : ICartonDetector
    {
        public async Task<IReadOnlyList<CartonBox>> DetectAsync(VisionImage image, DetectionModel model, CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return await inner.DetectAsync(image, model, cancellationToken);
        }
    }

    [Fact]
    public void OcrRetryScans_FromAppsettings_AreBoundOnce()
    {
        VisionOptions vision = _factory.Services.GetRequiredService<IOptions<VisionOptions>>().Value;

        Assert.Equal(
            [(0.6, 0.3, 320), (1.0, 0.4, 480)],
            vision.OcrRetryScans.Select(s => (s.WidthRatio, s.HeightRatio, s.MinAreaWidth)));
    }

    [Fact]
    public async Task Cors_PreflightFromAnyOrigin_IsAllowed()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Options, "/api/v1/stack-inspections/collect-sku");
        request.Headers.Add("Origin", "http://localhost:5173");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("kunci-salah")]
    public async Task Api_WithoutValidApiKey_Returns401(string? key)
    {
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        if (key is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", key);
        }

        using HttpResponseMessage response = await client.GetAsync("/api/v1/models");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        JsonNode? problem = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("API_KEY_INVALID", problem?["code"]?.GetValue<string>());
    }

    [Theory]
    [InlineData(FakeVisionFactory.ApiKey)]
    [InlineData("test-key-ai")]
    public async Task Api_WithAnyRegisteredApiKey_IsAllowed(string key)
    {
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        client.DefaultRequestHeaders.Add("X-Api-Key", key);

        using HttpResponseMessage response = await client.GetAsync("/api/v1/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/swagger/v1/swagger.json")]
    public async Task HealthAndSwagger_WithoutApiKey_AreOpen(string path)
    {
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Api-Key");

        using HttpResponseMessage response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Swagger_DeclaresApiKeyHeader()
    {
        using HttpClient client = _factory.CreateClient();

        JsonNode? doc = JsonNode.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));

        Assert.Equal("X-Api-Key", (string?)doc?["components"]?["securitySchemes"]?["ApiKey"]?["name"]);
        Assert.NotNull(doc?["security"]?[0]?["ApiKey"]);
    }

    [Fact]
    public async Task Api_WhenNoKeysConfigured_RejectsEverything()
    {
        using WebApplicationFactory<Program> noKeys = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AUTH_APIKeys"] = " , ",
            })));
        using HttpClient client = noKeys.CreateClient();
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        client.DefaultRequestHeaders.Add("X-Api-Key", "");

        using HttpResponseMessage response = await client.GetAsync("/api/v1/models");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
