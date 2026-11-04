namespace StackInspection.Api.Security;

/// <summary>Konfigurasi akses API dengan header <c>X-Api-Key</c> (section <c>Auth</c>).</summary>
public sealed class ApiKeyOptions
{
    /// <summary>Nama section di konfigurasi.</summary>
    public const string SectionName = "Auth";

    /// <summary>
    /// Environment variable berisi daftar API key dipisah koma, misalnya <c>AUTH_APIKeys=key1,key2</c>.
    /// Jika ada, menggantikan <c>Auth:ApiKeys</c>.
    /// </summary>
    public const string EnvironmentVariable = "AUTH_APIKeys";

    /// <summary><c>false</c> = semua endpoint terbuka tanpa API key (hanya untuk development lokal).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Daftar API key dipisah koma (satu key per pemakai). Jangan simpan key di appsettings.json; isi lewat
    /// <see cref="EnvironmentVariable"/>. Kosong (dan <see cref="Enabled"/> = true) = semua request ke /api ditolak.
    /// </summary>
    public string ApiKeys { get; set; } = string.Empty;

    /// <summary>Key yang valid: dipisah koma, spasi di tepi dibuang, entri kosong diabaikan.</summary>
    public IReadOnlyList<string> ParseKeys() =>
        ApiKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
