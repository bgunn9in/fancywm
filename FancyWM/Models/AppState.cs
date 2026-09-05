using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FancyWM.Models
{
    public class AppState
    {
        public IObservableFileEntity<Settings> Settings { get; }

        public AppState() : this(Path.GetFullPath("settings.json"))
        {
        }

        internal AppState(string settingsPath)
        {
            Settings = new ObservableJsonEntityWithCommentPreservation<Settings>(settingsPath,
                () => new Settings
                {
                    AutoCollapsePanels = true,
                },
                CreateSettingsJsonSerializerOptions());
        }

        internal static JsonSerializerOptions CreateSettingsJsonSerializerOptions()
        {
            return new JsonSerializerOptions
            {
                AllowTrailingCommas = true,
                WriteIndented = true,
                PropertyNamingPolicy = null,
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Converters =
                {
                    new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
                }
            };
        }
    }
}
