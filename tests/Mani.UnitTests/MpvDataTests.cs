using System.Runtime.InteropServices;
using Mani.Playback;

namespace Mani.UnitTests;

public sealed class MpvDataTests
{
    [Fact]
    public void WindowsX64LayoutsMatchThePinnedClientHeader()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(24, Marshal.SizeOf<MpvNative.Event>());
        Assert.Equal(8, Marshal.OffsetOf<MpvNative.Event>("Reply").ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<MpvNative.Event>("Data").ToInt32());
        Assert.Equal(24, Marshal.SizeOf<MpvNative.PropertyEvent>());
        Assert.Equal(32, Marshal.SizeOf<MpvNative.EndFile>());
        Assert.Equal(16, Marshal.SizeOf<MpvNative.Node>());
        Assert.Equal(24, Marshal.SizeOf<MpvNative.NodeList>());
        Assert.Equal(16, Marshal.SizeOf<MpvNative.ByteArray>());
    }

    [Fact]
    public void NestedNodesOwnTheirUtf8DataAfterNativeMemoryIsFreed()
    {
        var text = Marshal.StringToCoTaskMemUTF8("中文 #1");
        var key = Marshal.StringToCoTaskMemUTF8("title");
        var keys = Marshal.AllocHGlobal(8);
        var values = Marshal.AllocHGlobal(16);
        var list = Marshal.AllocHGlobal(24);
        object? result;
        try
        {
            Marshal.WriteIntPtr(keys, key);
            Marshal.StructureToPtr(new MpvNative.Node { Format = 1, Pointer = text }, values, false);
            Marshal.StructureToPtr(new MpvNative.NodeList { Count = 1, Values = values, Keys = keys }, list, false);
            result = MpvData.CopyNode(new MpvNative.Node { Format = 8, Pointer = list });
        }
        finally
        {
            Marshal.FreeCoTaskMem(text); Marshal.FreeCoTaskMem(key);
            Marshal.FreeHGlobal(keys); Marshal.FreeHGlobal(values); Marshal.FreeHGlobal(list);
        }
        var map = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(result);
        Assert.Equal("中文 #1", map["title"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, object?>)map).Add("other", 1));
    }

    [Fact]
    public void Int64NodesDoNotTruncateToWindowsLongWidth()
    {
        Assert.Equal(1L << 40, MpvData.CopyNode(new MpvNative.Node { Format = 4, Integer = 1L << 40 }));
    }

    [Fact]
    public void TrackModelPreservesOptionalFieldsAndIinaMetadataTitleFallback()
    {
        var track = MediaTrack.Parse(new Dictionary<string, object?>
        {
            ["id"] = 1L << 40, ["type"] = "audio", ["title"] = " ",
            ["metadata"] = new Dictionary<string, object?> { ["name"] = "中文音轨" },
            ["lang"] = "zho", ["demux-samplerate"] = 48000L, ["selected"] = true,
            ["demux-channel-count"] = 6L, ["codec"] = "flac"
        });
        Assert.NotNull(track);
        Assert.Equal("中文音轨", track.Title);
        Assert.Equal(48000, track.SampleRate);
        Assert.Equal(6, track.ChannelCount);
        Assert.Null(track.Width);
        Assert.True(track.Selected);
        Assert.Equal(1L << 40, track.Id);
    }
}
