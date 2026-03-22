using NUlid;
using System.Text.Json.Serialization;
using System.Text.Json;
using System;

namespace Ilmarinen.Protocol;

public class UlidJsonConverter : JsonConverter<Ulid>
{
    public override Ulid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var str = reader.GetString();
        return str != null ? Ulid.Parse(str) : default;
    }

    public override void Write(Utf8JsonWriter writer, Ulid value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
