using System.Text.Json.Serialization;
namespace Winche.Storage.Models
{
    public record FilePart
    {

        [JsonPropertyName("number")]
        public required int Number { get; init; }

        [JsonPropertyName("size")]
        public long? Size { get; init; }
    }
}
