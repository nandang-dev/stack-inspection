using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using StackInspection.Api.Contracts;
using StackInspection.Api.Rendering;
using StackInspection.Api.Swagger;
using StackInspection.Application.Analyze;
using StackInspection.Application.CollectSku;
using StackInspection.Application.Contracts;
using StackInspection.Application.Exceptions;
using Swashbuckle.AspNetCore.Filters;

namespace StackInspection.Api.Controllers;

/// <summary>Penilaian kualitas tumpukan kardus dari foto truk/kontainer.</summary>
[ApiController]
[Route("api/v1/stack-inspections")]
[Produces("application/json")]
public sealed class StackInspectionsController : ControllerBase
{
    private static readonly JsonSerializerOptions MasterJson = new(JsonSerializerDefaults.Web);

    private readonly CollectSkuHandler _collect;
    private readonly AnalyzeStackHandler _analyze;

    /// <summary>Membuat controller.</summary>
    public StackInspectionsController(CollectSkuHandler collect, AnalyzeStackHandler analyze)
    {
        _collect = collect;
        _analyze = analyze;
    }

    /// <summary>Collect SKU: deteksi kardus lapisan depan, baca SKU, dan susun baris/kolom.</summary>
    /// <remarks>
    /// Foto ditolak dengan 422 <c>PHOTO_NOT_INSPECTABLE</c> jika resolusi sisi terpanjang di bawah
    /// <c>Photo:MinLongSide</c> atau jumlah piksel di bawah <c>Photo:MinMegapixels</c>, ada stempel aplikasi
    /// kamera, atau tidak ada kardus terdeteksi.
    /// </remarks>
    /// <param name="image">Foto JPG/PNG, maksimal <c>Upload:MaxFileSizeMb</c>.</param>
    /// <param name="candidateSkus">
    /// Daftar kode SKU untuk fuzzy match, disarankan <b>seluruh master SKU</b> (opsional, boleh dipisah koma).
    /// Hasil OCR dikoreksi ke SKU terdekat di daftar ini. Jika kosong, salah baca dikoreksi ke SKU mayoritas
    /// di foto yang sama dan response memuat warning <c>SKU_CORRECTED_BY_MAJORITY</c>.
    /// </param>
    /// <param name="model">Model deteksi (opsional). Kosong = model default. Lihat <c>GET /api/v1/models</c>.</param>
    /// <param name="cancellationToken">Token pembatalan.</param>
    /// <response code="200">Hasil deteksi dan pembacaan SKU.</response>
    /// <response code="400">File kosong, format tidak didukung, terlalu besar, tidak bisa dibaca, atau model tidak dikenal.</response>
    /// <response code="422">Foto tidak layak dinilai (<c>PHOTO_NOT_INSPECTABLE</c>).</response>
    [HttpPost("collect-sku")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(100 * 1024 * 1024)]
    [ProducesResponseType(typeof(CollectSkuResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [SwaggerResponseExample(StatusCodes.Status200OK, typeof(CollectSkuResponseExample))]
    [SwaggerResponseExample(StatusCodes.Status422UnprocessableEntity, typeof(PhotoNotInspectableExample))]
    public async Task<ActionResult<CollectSkuResponse>> CollectSku(
        IFormFile? image,
        [FromForm] List<string>? candidateSkus,
        [FromForm] string? model,
        CancellationToken cancellationToken)
    {
        byte[] data = [];
        if (image is not null)
        {
            using MemoryStream buffer = new();
            await image.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            data = buffer.ToArray();
        }

        CollectSkuResponse response = await _collect
            .HandleAsync(new CollectSkuCommand(data, candidateSkus ?? [], model), cancellationToken)
            .ConfigureAwait(false);
        return Ok(response);
    }

    /// <summary>Analyze: hitung pelanggaran aturan tumpukan dan skor.</summary>
    /// <remarks>
    /// Aturan: <c>UNKNOWN_SKU</c> dan <c>LABEL_NOT_VISIBLE</c> dihitung gagal; <c>MAX_STACK_EXCEEDED</c>
    /// (jumlah SKU sama per jalur tumpukan &gt; maxStack); <c>CLASS_POSITION_INVALID</c> (kelas A &lt; B &lt; C,
    /// kelas berat harus di bawah, dibandingkan dengan semua dus di bawahnya). Tumpukan dibentuk ulang dari
    /// <c>id</c>/<c>restsOn</c>; hasil Collect SKU versi lama (tanpa keduanya) ditolak 400.
    /// </remarks>
    /// <param name="request">Hasil Collect SKU dan master SKU.</param>
    /// <response code="200">Skor dan daftar pelanggaran.</response>
    /// <response code="400">Request tidak valid (master kosong, SKU duplikat, kelas tidak valid, sel duplikat, hasil Collect versi lama).</response>
    [HttpPost("analyze")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(AnalyzeStackResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [SwaggerRequestExample(typeof(AnalyzeStackRequest), typeof(AnalyzeStackRequestExample))]
    [SwaggerResponseExample(StatusCodes.Status200OK, typeof(AnalyzeStackResponseExample))]
    public ActionResult<AnalyzeStackResponse> Analyze([FromBody] AnalyzeStackRequest request) =>
        Ok(_analyze.Handle(request));

    /// <summary>Preview: Collect SKU + Analyze sekaligus, hasilnya halaman HTML simulasi render susunan dus.</summary>
    /// <remarks>
    /// Untuk melihat hasil deteksi dan penilaian secara visual (QA, demo, pengecekan tim UI) tanpa membangun UI.
    /// Halaman berisi: ringkasan (skor, jumlah dus, grid, waktu proses, warning), <b>render grid</b> persis seperti
    /// yang akan digambar UI dari <c>cells</c> (sel ber-<c>id</c> sama digabung, dus melanggar diberi tanda merah),
    /// <b>posisi dus di foto</b> (SVG koordinat asli + garis <c>restsOn</c>), legenda SKU, tabel pelanggaran, dan
    /// jalur tumpukan. Arahkan kursor ke dus/baris tabel/jalur untuk menyorot dus yang sama di semua panel.
    /// <br/>Response dikirim sebagai lampiran (<c>Content-Disposition: attachment</c>): di Swagger klik
    /// <b>Download file</b> pada hasil Execute lalu buka file <c>.html</c>-nya di browser.
    /// <br/>Error tetap JSON seperti Collect SKU: 400 (file/model/masterSkus tidak valid) dan 422
    /// (<c>PHOTO_NOT_INSPECTABLE</c>).
    /// </remarks>
    /// <param name="request">Foto, candidateSkus, model, dan masterSkus (JSON array, opsional).</param>
    /// <param name="cancellationToken">Token pembatalan.</param>
    /// <response code="200">
    /// Halaman HTML mandiri (<c>text/html; charset=utf-8</c>, lampiran <c>stack-preview-{inspectionId}.html</c>,
    /// tanpa file/library eksternal, responsive, mode
    /// terang/gelap). Jika <c>masterSkus</c> diisi, halaman menampilkan skor dan pelanggaran Analyze; jika kosong,
    /// hanya susunan dus dari Collect SKU.
    /// </response>
    /// <response code="400">File kosong/format tidak didukung/terlalu besar, model tidak dikenal, atau masterSkus bukan JSON/tidak valid.</response>
    /// <response code="422">Foto tidak layak dinilai (<c>PHOTO_NOT_INSPECTABLE</c>).</response>
    [HttpPost("preview")]
    [Consumes("multipart/form-data")]
    [Produces("text/html", "application/json")]
    [RequestSizeLimit(100 * 1024 * 1024)]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK, "text/html")]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest, "application/json")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity, "application/json")]
    [SwaggerResponseExample(StatusCodes.Status422UnprocessableEntity, typeof(PhotoNotInspectableExample))]
    public async Task<ContentResult> Preview([FromForm] StackPreviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // masterSkus divalidasi dulu agar kesalahan input tidak menunggu proses vision
        List<MasterSkuDto> masters = ParseMasterSkus(request.MasterSkus);
        byte[] data = [];
        if (request.Image is not null)
        {
            using MemoryStream buffer = new();
            await request.Image.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            data = buffer.ToArray();
        }

        CollectSkuResponse collect = await _collect
            .HandleAsync(new CollectSkuCommand(data, request.CandidateSkus ?? [], request.Model), cancellationToken)
            .ConfigureAwait(false);
        AnalyzeStackResponse? analyze = masters.Count == 0
            ? null
            : _analyze.Handle(new AnalyzeStackRequest { CollectResult = collect, MasterSkus = masters });
        // attachment: Swagger UI menampilkan tombol "Download file" (HTML tidak dirender di Swagger)
        Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileName = $"stack-preview-{collect.InspectionId:N}.html",
        }.ToString();
        return Content(StackLayoutHtmlRenderer.Render(collect, analyze), "text/html; charset=utf-8");
    }

    private static List<MasterSkuDto> ParseMasterSkus(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<MasterSkuDto>>(json, MasterJson) ?? [];
        }
        catch (JsonException)
        {
            throw new RequestValidationException(
                "masterSkus",
                "masterSkus harus JSON array, contoh: [{\"sku\":\"68140913\",\"name\":\"SariWangi\",\"maxStack\":7,\"weight\":3.1,\"class\":\"A\"}].");
        }
    }
}
