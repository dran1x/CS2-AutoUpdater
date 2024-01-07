using System.Text.Json.Serialization;

namespace CS2AutoUpdater;

public class UpToDateCheckResponse
{
    [JsonPropertyName("response")]
    public required UpToDateCheckObject Response { get; set; }
}

public class UpToDateCheckObject
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("up_to_date")]
    public bool UpToDate { get; set; }

    [JsonPropertyName("version_is_listable")]
    public bool VersionIsListable { get; set; }

    [JsonPropertyName("required_version")]
    public int RequiredVersion { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}