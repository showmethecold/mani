namespace Mani.Playback;

// Application UI only. Never used for media track selection or numeric commands.
public static class InterfaceLanguage
{
    public const string English = "en-US", Chinese = "zh-CN";
    public static string Normalize(string? value) => value == Chinese ? Chinese : English;
}
