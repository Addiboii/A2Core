using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>Broker fees and durations (tools/data/trade.json, tools/trade_data.py).</summary>
public sealed class TradeTables
{
    sealed record TradeFile(
        [property: JsonPropertyName("default_trade_server_id")] int DefaultTradeServerId,
        [property: JsonPropertyName("listing_duration_ms")] long ListingDurationMs,
        [property: JsonPropertyName("listing_fee_rate_bp")] int ListingFeeRateBp,
        [property: JsonPropertyName("listing_fee_min_kina")] long ListingFeeMinKina,
        [property: JsonPropertyName("sale_tax_rate_bp")] int SaleTaxRateBp,
        [property: JsonPropertyName("max_active_listings_per_character")] int MaxActiveListings,
        [property: JsonPropertyName("player_mail_fee_kina")] long PlayerMailFeeKina,
        [property: JsonPropertyName("player_mail_expire_days")] int PlayerMailExpireDays,
        [property: JsonPropertyName("cod_post_data_id")] int CodPostDataId,
        [property: JsonPropertyName("staff_post_data_id")] int StaffPostDataId);

    readonly TradeFile file;
    TradeTables(TradeFile file) => this.file = file;

    static readonly Lazy<TradeTables> embedded = new(() =>
    {
        using var stream = typeof(TradeTables).Assembly.GetManifestResourceStream("trade.json")
            ?? throw new InvalidOperationException("Missing embedded game data trade.json");
        return new TradeTables(JsonSerializer.Deserialize<TradeFile>(stream) ?? throw new InvalidOperationException("Empty trade.json"));
    });

    public static TradeTables Embedded => embedded.Value;

    public int DefaultTradeServerId => file.DefaultTradeServerId;
    public long ListingDurationMs => file.ListingDurationMs;
    public int MaxActiveListings => file.MaxActiveListings;
    public long PlayerMailFeeKina => file.PlayerMailFeeKina;
    public int PlayerMailExpireDays => file.PlayerMailExpireDays;
    public int CodPostDataId => file.CodPostDataId;

    public long ListingFee(long priceKina) =>
        Math.Max(file.ListingFeeMinKina, priceKina * file.ListingFeeRateBp / 10_000);

    public long SellerProceeds(long priceKina) => priceKina - priceKina * file.SaleTaxRateBp / 10_000;
}
