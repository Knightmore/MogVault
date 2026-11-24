using System.Text.Json.Serialization;

namespace MogVault.Contract;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(QueryRequest))]
[JsonSerializable(typeof(QueryResponse))]
[JsonSerializable(typeof(CharactersResponse))]
[JsonSerializable(typeof(CollectionsResponse))]
[JsonSerializable(typeof(CollectionOwnersResponse))]
[JsonSerializable(typeof(WalletsResponse))]
[JsonSerializable(typeof(FreeSlotsResponse))]
[JsonSerializable(typeof(TimersResponse))]
[JsonSerializable(typeof(QuestsResponse))]
[JsonSerializable(typeof(OutfitsResponse))]
public sealed partial class MogVaultJsonContext : JsonSerializerContext;
