namespace AutoUpdater
{
    using System.Text.Json.Serialization;
    
    public class UpToDateCheckResponse
    {
        [JsonPropertyName("response")]
        public UpToDateCheck? Response { get; init; }

        public class UpToDateCheck
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
    }
}