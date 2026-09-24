using System;
using System.IO;
using System.Text.Json;

namespace AmxEventsFormPoc.Services;

// Same shape as the "Mqtt" section of DraxView/DraxView/appsettings.json —
// DraxView's own appsettings.json can be copied straight over this project's
// appsettings.json and it'll just work, since both read the same three fields.
public sealed class MqttSettings
{
    public string Broker { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string TopicPrefix { get; set; } = "drax";

    public static MqttSettings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path)) return new MqttSettings();

        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("Mqtt", out var mqtt)) return new MqttSettings();

            var settings = new MqttSettings();
            if (mqtt.TryGetProperty("Broker", out var b) && b.ValueKind == JsonValueKind.String)
                settings.Broker = b.GetString() ?? settings.Broker;
            if (mqtt.TryGetProperty("Port", out var p) && p.TryGetInt32(out var port))
                settings.Port = port;
            if (mqtt.TryGetProperty("TopicPrefix", out var t) && t.ValueKind == JsonValueKind.String)
                settings.TopicPrefix = t.GetString() ?? settings.TopicPrefix;
            return settings;
        }
        catch
        {
            // A malformed appsettings.json shouldn't stop the app from starting —
            // fall back to the defaults above, same as DraxView's own config binding
            // would just use its registered defaults if the section were missing.
            return new MqttSettings();
        }
    }
}
