using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KidsLocker.Service.Scheduling;

/// <summary>Старые паузы с UTC-смещением переводим в Москву, не обращаясь к поясу Windows.</summary>
internal sealed class MoscowPauseConverter : JsonConverter<DateTime?>
{
    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        var text = reader.GetString() ?? throw new JsonException("Не задан конец паузы");
        var timeStart = text.IndexOf('T');
        var hasOffset = text.EndsWith('Z') || timeStart >= 0 && (text.IndexOf('+', timeStart) >= 0 || text.IndexOf('-', timeStart) >= 0);
        if (hasOffset)
            return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture).ToOffset(TrustedSchedule.MoscowOffset).DateTime;
        return DateTime.SpecifyKind(DateTime.Parse(text, CultureInfo.InvariantCulture), DateTimeKind.Unspecified);
    }

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified));
    }
}
