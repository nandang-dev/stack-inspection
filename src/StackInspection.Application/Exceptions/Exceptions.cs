namespace StackInspection.Application.Exceptions;

/// <summary>Request tidak valid (HTTP 400). <see cref="Errors"/> berisi pesan per field.</summary>
public sealed class RequestValidationException : Exception
{
    public RequestValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("Request tidak valid.")
    {
        Errors = errors;
    }

    public RequestValidationException(string field, string message)
        : this(new Dictionary<string, string[]> { [field] = [message] })
    {
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}

/// <summary>Gambar tidak bisa dibaca (HTTP 400).</summary>
public sealed class InvalidImageException : Exception
{
    public InvalidImageException(string message)
        : base(message)
    {
    }
}

/// <summary>Kode alasan foto ditolak.</summary>
public static class PhotoRejectReasons
{
    public const string ResolutionTooLow = "PHOTO_RESOLUTION_TOO_LOW";
    public const string CameraOverlayDetected = "CAMERA_OVERLAY_DETECTED";
    public const string NoCartonDetected = "NO_CARTON_DETECTED";
}

/// <summary>Foto tidak layak dinilai (HTTP 422, code PHOTO_NOT_INSPECTABLE).</summary>
public sealed class PhotoNotInspectableException : Exception
{
    public const string Code = "PHOTO_NOT_INSPECTABLE";

    public PhotoNotInspectableException(string reason, string detail)
        : base(detail)
    {
        Reason = reason;
    }

    public string Reason { get; }
}
