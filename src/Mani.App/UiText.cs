using Microsoft.Windows.ApplicationModel.Resources;

namespace Mani_App;

internal static class UiText
{
    private static readonly ResourceManager Resources = new();
    private static readonly ResourceContext Context = Resources.CreateResourceContext();
    public static string Language { get; private set; } = Mani.Playback.InterfaceLanguage.English;
    public static void SetLanguage(string? language)
    {
        Language = Mani.Playback.InterfaceLanguage.Normalize(language);
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = Language;
        Context.QualifierValues["Language"] = Language;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = new(Language);
    }
    public static string Get(string key) => Resources.MainResourceMap.GetValue("Resources/Text_" + key, Context).ValueAsString;
    public static string Status(string value) => value switch
    {
        "Loading..." => Get("Loading"), "Buffering..." => Get("Buffering"), "Seeking..." => Get("Seeking"),
        "Playback ended" => Get("Ended"), "Open a local video to start playback" => Get("DropHint"),
        "Stopped" => Get("Stopped"), "Paused" => Get("Paused"), "Playing" => Get("Playing"), _ => value
    };
}
