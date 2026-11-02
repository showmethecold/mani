// Adapted from IINA 1.5.1/build 180 MPVController's client ownership and event copying.
// Copyright (c) 2016 lhc. GPL-3.0; see licenses/IINA-GPL-3.0.txt.
using System.Runtime.InteropServices;

namespace Mani.Playback;

internal sealed class MpvClient : IMpvClient
{
    private nint _handle;
    private bool _networkLoggingDisabled;
    private string _logPath = "";
    private long _nextLogCheck;
    public void Initialize(PlaybackOptions options)
    {
        MpvNative.Load(options.DllPath, options.DllSha256);
        _logPath = options.LogPath;
        var api = MpvNative.mpv_client_api_version();
        if (api >> 16 != 2) throw new InvalidOperationException($"Unsupported client API: {api >> 16}.{api & 0xffff}");
        _handle = MpvNative.mpv_create();
        if (_handle == 0) throw new InvalidOperationException("mpv_create failed.");
        if (!string.IsNullOrWhiteSpace(options.LogPath)) Directory.CreateDirectory(System.IO.Path.GetDirectoryName(options.LogPath)!);
        foreach (var (name, value) in new Dictionary<string, string>
        {
            ["config"] = "no", ["load-scripts"] = "no", ["osc"] = "no", ["ytdl"] = "no",
            ["input-default-bindings"] = "no", ["input-vo-keyboard"] = "no", ["terminal"] = "no",
            ["idle"] = "yes", ["keep-open"] = options.KeepOpen ? "yes" : "no",
            ["vo"] = options.Headless ? "null" : "gpu-next", ["hwdec"] = options.Headless ? "no" : "auto-safe",
            ["volume"] = "50", ["log-file"] = options.LogPath
        }) SetOption(name, value);
        // Keep the normal client's default (auto); auxiliary capture clients need no profiles.
        if (!options.LoadAutoProfiles) SetOption("load-auto-profiles", "no");
        if (options.Headless) SetOption("ao", "null");
        else
        {
            SetOption("gpu-api", "d3d11");
            SetOption("gpu-context", "d3d11");
            SetOption("d3d11-output-mode", "composition");
            SetOption("d3d11-composition-size", "960x540");
            // Keep the VO alive across file changes/EOF; detach the panel before destruction.
            SetOption("force-window", "yes");
        }
        MpvNative.Check(MpvNative.mpv_initialize(_handle), "initialize");
        MpvNative.Check(MpvNative.mpv_request_log_messages(_handle, "error"), "log subscription");
        for (int i = 0; i < ObservedMetadata.Names.Count; i++)
            MpvNative.Check(MpvNative.mpv_observe_property(_handle, (ulong)i + 1,
                ObservedMetadata.Names[i], 1 /* STRING */), "observe " + ObservedMetadata.Names[i]);
        // NONE carries change notification only; never copy entire playlist/track trees.
        foreach (var name in new[] { "playlist", "audio-device-list" })
            MpvNative.Check(MpvNative.mpv_observe_property(_handle, 100, name, 0), "observe " + name);
    }
    private void SetOption(string name, string value) => MpvNative.Check(MpvNative.mpv_set_option_string(_handle, name, value), name);
    public void Set(string name, string value) => MpvNative.Check(MpvNative.mpv_set_property_string(_handle, name, value), name);
    public string Get(string name)
    {
        var pointer = MpvNative.mpv_get_property_string(_handle, name);
        if (pointer == 0) return "";
        try { return MpvNative.Utf8(pointer); }
        finally { MpvNative.mpv_free(pointer); }
    }
    public bool Flag(string name) => MpvNative.GetFlag(_handle, name, 3, out var value) >= 0 && value != 0;
    public double Number(string name) => MpvNative.GetDouble(_handle, name, 5, out var value) >= 0 && double.IsFinite(value) ? value : 0;
    public long Integer(string name) => MpvNative.mpv_get_property(_handle, name, 4, out var value) >= 0 ? value : 0;
    public object? Node(string name)
    {
        if (MpvNative.GetNode(_handle, name, 6, out var node) < 0) return null;
        try { return MpvData.CopyNode(node); }
        finally { MpvNative.mpv_free_node_contents(ref node); }
    }
    public MpvEvent WaitEvent()
    {
        if (!_networkLoggingDisabled && _logPath.Length > 0 && Environment.TickCount64 >= _nextLogCheck)
        {
            _nextLogCheck = Environment.TickCount64 + 1000;
            try
            {
                if (new FileInfo(_logPath) is { Exists: true, Length: >= 8 * 1024 * 1024 })
                { Set("log-file", ""); _networkLoggingDisabled = true; }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return MpvData.CopyEvent(Marshal.PtrToStructure<MpvNative.Event>(MpvNative.mpv_wait_event(_handle, 0)));
    }
    public void Send(ulong request, string[] arguments)
    {
        // Native log-file output cannot be redacted after mpv writes it. Disable
        // it before submitting any network entry (including an appended URL).
        if (!_networkLoggingDisabled && arguments.FirstOrDefault() == "loadfile" && arguments.Length > 1 &&
            (DiagnosticPrivacy.IsNetworkSource(arguments[1]) ||
             new[] { ".m3u", ".m3u8", ".pls" }.Contains(Path.GetExtension(arguments[1]).ToLowerInvariant())))
        {
            Set("log-file", "");
            _networkLoggingDisabled = true;
        }
        MpvNative.Command(_handle, arguments, request);
    }
    public void Abort(ulong request) => MpvNative.mpv_abort_async_command(_handle, request);
    public void Dispose()
    {
        if (_handle == 0) return;
        MpvNative.mpv_terminate_destroy(_handle);
        _handle = 0;
    }
}
