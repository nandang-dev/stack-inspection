using Microsoft.AspNetCore.Mvc;
using StackInspection.Api.Swagger;
using StackInspection.Application.Analyze;
using StackInspection.Application.CollectSku;
using StackInspection.Application.Contracts;
using Swashbuckle.AspNetCore.Filters;

namespace StackInspection.Api.Controllers;

/// <summary>Penilaian kualitas tumpukan kardus dari foto truk/kontainer.</summary>
[ApiController]
[Route("api/v1/stack-inspections")]
[Produces("application/json")]
public sealed class StackInspectionsController : ControllerBase
{
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
    /// <c>Photo:MinLongSide</c>, ada stempel aplikasi kamera, atau tidak ada kardus terdeteksi.
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
    /// (jumlah SKU sama per kolom &gt; maxStack); <c>CLASS_POSITION_INVALID</c> (kelas A &lt; B &lt; C, kelas
    /// berat harus di bawah). Stack dibentuk ulang dari <c>row</c>/<c>column</c>/<c>level</c>.
    /// </remarks>
    /// <param name="request">Hasil Collect SKU dan master SKU.</param>
    /// <response code="200">Skor dan daftar pelanggaran.</response>
    /// <response code="400">Request tidak valid (master kosong, SKU duplikat, kelas tidak valid, posisi duplikat).</response>
    [HttpPost("analyze")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(AnalyzeStackResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [SwaggerRequestExample(typeof(AnalyzeStackRequest), typeof(AnalyzeStackRequestExample))]
    [SwaggerResponseExample(StatusCodes.Status200OK, typeof(AnalyzeStackResponseExample))]
    public ActionResult<AnalyzeStackResponse> Analyze([FromBody] AnalyzeStackRequest request) =>
        Ok(_analyze.Handle(request));
}
