using Microsoft.AspNetCore.Mvc;
using StackInspection.Api.Swagger;
using StackInspection.Application.Contracts;
using StackInspection.Application.Models;
using Swashbuckle.AspNetCore.Filters;

namespace StackInspection.Api.Controllers;

/// <summary>Model deteksi kardus yang tersedia di server.</summary>
[ApiController]
[Route("api/v1/models")]
[Produces("application/json")]
public sealed class ModelsController : ControllerBase
{
    private readonly ListModelsHandler _list;

    /// <summary>Membuat controller.</summary>
    public ModelsController(ListModelsHandler list)
    {
        _list = list;
    }

    /// <summary>Daftar model deteksi (nama, input size, threshold, status, metrik golden set).</summary>
    /// <remarks>Nama model dipakai di field <c>model</c> endpoint Collect SKU. Tanpa field itu, model default dipakai.</remarks>
    /// <response code="200">Daftar model; <c>isDefault = true</c> untuk model default.</response>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ModelInfoDto>), StatusCodes.Status200OK)]
    [SwaggerResponseExample(StatusCodes.Status200OK, typeof(ModelListExample))]
    public ActionResult<IReadOnlyList<ModelInfoDto>> List() => Ok(_list.Handle());
}
