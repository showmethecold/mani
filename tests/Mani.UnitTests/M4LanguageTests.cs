using System.Text.Json;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using Mani.Playback;
using Mani_App;

namespace Mani.UnitTests;

public sealed class M4LanguageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mani-language-" + Guid.NewGuid().ToString("N"));
    public M4LanguageTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);
    [Theory]
    [InlineData("{}", "en-US")]
    [InlineData("{\"language\":\"zh-CN\"}", "zh-CN")]
    [InlineData("{\"language\":\"zh-TW\"}", "en-US")]
    [InlineData("{\"language\":7}", "en-US")]
    [InlineData("{\"language\":null}", "en-US")]
    public async Task ExplicitLanguageIgnoresSystemAndMalformedValues(string json, string expected)
    {
        var file = Path.Combine(_directory, "settings.json"); await File.WriteAllTextAsync(file, json, TestContext.Current.CancellationToken);
        Assert.Equal(expected, (await new PlayerSettingsStore(file).LoadAsync(TestContext.Current.CancellationToken)).Language);
    }
    [Fact]
    public async Task LanguageRoundTripsWithoutChangingMediaPreferencesOrUnknownFields()
    {
        var file = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(file, "{\"audioLanguage\":\"ja\",\"volumePercent\":37,\"controlBarAutoHideTimeout\":3.5}", TestContext.Current.CancellationToken);
        var store = new PlayerSettingsStore(file); var before = await store.LoadAsync(TestContext.Current.CancellationToken);
        await store.SaveAsync(before with { Language = "zh-CN" }, TestContext.Current.CancellationToken);
        Assert.Equal(before with { Language = "zh-CN" }, await new PlayerSettingsStore(file).LoadAsync(TestContext.Current.CancellationToken));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)); Assert.Equal("ja", json.RootElement.GetProperty("audioLanguage").GetString());
    }
    [Fact]
    public void OtherWindowEditsCannotRevertLanguage()
    {
        var before = new PlayerSettings();
        var shared = before with { Language = "zh-CN" };
        Assert.Equal("zh-CN", SharedPlayerSettings.Merge(shared, before, before with { VolumePercent = 12 }).Language);
        Assert.Equal("en-US", SharedPlayerSettings.Merge(shared, shared, shared with { Language = "en-US" }).Language);
    }
    [Fact]
    public void BothResourcesHaveSameKeysAndFormatParameters()
    {
        Dictionary<string, string> Read(string lang) => XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Resources", lang + ".resw")).Root!.Elements("data").ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!);
        var en = Read("en-US"); var zh = Read("zh-CN");
        Assert.Equal(en.Keys.Order(), zh.Keys.Order());
        foreach (var (key, value) in en)
        {
            Assert.False(string.IsNullOrWhiteSpace(zh[key]), key);
            Assert.Equal(Regex.Matches(value, @"\{\d+(?:[^{}]*)\}").Select(m => m.Value).Order(), Regex.Matches(zh[key], @"\{\d+(?:[^{}]*)\}").Select(m => m.Value).Order());
        }
    }
    [Theory]
    [InlineData(80, PlaybackModifiers.Control | PlaybackModifiers.Shift, PlaybackKeyAction.Panel)]
    [InlineData(67, PlaybackModifiers.Control | PlaybackModifiers.Shift, PlaybackKeyAction.Panel)]
    [InlineData(39, PlaybackModifiers.Control, PlaybackKeyAction.Playlist)]
    [InlineData(190, PlaybackModifiers.Control | PlaybackModifiers.Shift, PlaybackKeyAction.Chapter)]
    [InlineData(69, PlaybackModifiers.Shift, PlaybackKeyAction.Edition)]
    internal void NavigationKeysRespectInputOwnership(int key, PlaybackModifiers modifiers, PlaybackKeyAction action)
    {
        Assert.Equal(action, PlaybackKeyMap.Resolve(key, modifiers, true)?.Action);
        Assert.Null(PlaybackKeyMap.Resolve(key, modifiers, true, ownsInput: true));
    }
    [Fact]
    public void DiagnosticsRemoveEntireNetworkUrlsWithoutChangingMediaIdentity()
    {
        const string source = "https://user:password@example.test/movie?token=private#secret";
        string redacted = DiagnosticPrivacy.Redact("Open failed: " + source);
        Assert.Equal("Open failed: <network-media>", redacted);
        Assert.DoesNotContain("private", redacted);
        Assert.NotEqual(PlaybackHistoryStore.Identity(source), PlaybackHistoryStore.Identity("https://example.test/movie"));
    }
}
