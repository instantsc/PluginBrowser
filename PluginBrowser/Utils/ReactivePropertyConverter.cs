using System.Text.Json;
using System.Text.Json.Serialization;
using Reactive.Bindings;

namespace PluginBrowser.Utils;

public class ReactivePropertyConverter<T> : JsonConverter<ReactiveProperty<T>>
{
    public override ReactiveProperty<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var converter = (JsonConverter<T>)options.GetConverter(typeof(T));

        var value = converter.Read(ref reader, typeof(T), options);
        var instance = new ReactiveProperty<T>
        {
            Value = value
        };
        return instance;
    }

    public override void Write(Utf8JsonWriter writer, ReactiveProperty<T> value, JsonSerializerOptions options)
    {
        var converter = (JsonConverter<T>)options.GetConverter(typeof(T));
        converter.Write(writer, value.Value, options);
    }

    public override bool HandleNull => true;
}
