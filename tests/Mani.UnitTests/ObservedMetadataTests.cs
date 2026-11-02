using System.Runtime.InteropServices;
using Mani.Playback;

namespace Mani.UnitTests;

// The native payloads below are test-owned memory, not a loaded libmpv instance.
public sealed class ObservedMetadataTests
{
    [Fact]
    public void RepeatedReadsReuseTheObservedProperties()
    {
        var metadata = new ObservedMetadata();
        int queries = 0;
        string Query(string name) { queries++; return name + "-fresh"; }
        metadata.BeginRead(0);
        foreach (string name in ObservedMetadata.Names)
            Assert.Equal(name + "-fresh", metadata.Read(name, Query));
        for (int i = 0; i < 100; i++)
            foreach (string name in ObservedMetadata.Names) metadata.Read(name, Query);
        Assert.Equal(ObservedMetadata.Names.Count, queries);
    }

    [Theory]
    [InlineData(999, 1)]
    [InlineData(1000, 2)]
    [InlineData(100000, 2)]
    [InlineData(-1, 2)]
    public void ReconciliationHonorsTheIntervalAndClockResets(long nextRead, int expectedQueries)
    {
        var metadata = new ObservedMetadata();
        int queries = 0;
        string Query(string _) => (++queries).ToString();
        metadata.BeginRead(0);
        metadata.Read("media-title", Query);
        metadata.BeginRead(nextRead);
        metadata.Read("media-title", Query);
        Assert.Equal(expectedQueries, queries);
    }

    [Fact]
    public void ExplicitInvalidationRefreshesTheNextRead()
    {
        var metadata = new ObservedMetadata();
        metadata.Read("media-title", _ => "Old title");
        metadata.Invalidate();
        Assert.Null(metadata.Peek("media-title"));
        Assert.Equal("New title", metadata.Read("media-title", _ => "New title"));
    }

    [Fact]
    public void StringEventsCopyUtf8BeforeTheNativeBufferIsReleased()
    {
        var metadata = new ObservedMetadata();
        using var payload = new PropertyPayload("media-title", "Title \u4e2d\u6587 #1");
        metadata.Handle(2, payload.Event());
        payload.ReleaseValue();
        Assert.Equal("Title \u4e2d\u6587 #1", metadata.Read("media-title", _ => throw new InvalidOperationException("Unexpected query.")));
    }

    [Fact]
    public void NoneEventsClearTheValueWithoutReadingADataPointer()
    {
        var metadata = new ObservedMetadata();
        metadata.Read("media-title", _ => "Old title");
        using var payload = new PropertyPayload("media-title", "Unused");
        metadata.Handle(2, payload.Event(format: 0, missingData: true));
        Assert.Equal("", metadata.Read("media-title", _ => throw new InvalidOperationException("Unexpected query.")));
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(1, true)]
    public void UnexpectedOrMissingPayloadsFallBackToTheNextDirectRead(int format, bool missingData)
    {
        var metadata = new ObservedMetadata();
        metadata.Read("media-title", _ => "Old title");
        using var payload = new PropertyPayload("media-title", "Event title");
        metadata.Handle(2, payload.Event(format, missingData));
        Assert.Equal("Fresh title", metadata.Read("media-title", _ => "Fresh title"));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(100UL)]
    [InlineData(1UL)]
    public void UnknownOrMismatchedSubscriptionsCannotChangeAnotherProperty(ulong subscription)
    {
        var metadata = new ObservedMetadata();
        metadata.Read("media-title", _ => "Original title");
        using var payload = new PropertyPayload("media-title", "Ignored title");
        metadata.Handle(subscription, payload.Event());
        Assert.Equal("Original title", metadata.Peek("media-title"));
    }

    [Fact]
    public void AFailedDirectReadDoesNotPoisonTheCache()
    {
        var metadata = new ObservedMetadata();
        Assert.Throws<IOException>(() => metadata.Read("media-title", _ => throw new IOException("Query failed.")));
        Assert.Null(metadata.Peek("media-title"));
        Assert.Equal("Recovered", metadata.Read("media-title", _ => "Recovered"));
    }

    [Fact]
    public void EmptyStringsAreCachedWithoutRepeatedQueries()
    {
        var metadata = new ObservedMetadata();
        Assert.Equal("", metadata.Read("media-title", _ => ""));
        Assert.Equal("", metadata.Read("media-title", _ => throw new InvalidOperationException("Unexpected query.")));
    }

    [Fact]
    public void UnknownPropertiesAreRejectedBeforeQuerying()
    {
        Assert.Throws<ArgumentException>(() => new ObservedMetadata().Read("not-observed", _ => throw new InvalidOperationException("Unexpected query.")));
    }

    private sealed class PropertyPayload : IDisposable
    {
        private readonly nint _name;
        private readonly nint _data;
        private nint _value;

        public PropertyPayload(string name, string value)
        {
            _name = Marshal.StringToCoTaskMemUTF8(name);
            _value = Marshal.StringToCoTaskMemUTF8(value);
            _data = Marshal.AllocHGlobal(nint.Size);
            Marshal.WriteIntPtr(_data, _value);
        }

        public MpvNative.PropertyEvent Event(int format = 1, bool missingData = false) =>
            new() { Name = _name, Format = format, Data = missingData ? 0 : _data };

        public void ReleaseValue()
        {
            if (_value != 0) Marshal.FreeCoTaskMem(_value);
            _value = 0;
            Marshal.WriteIntPtr(_data, 0);
        }

        public void Dispose()
        {
            ReleaseValue();
            Marshal.FreeCoTaskMem(_name);
            Marshal.FreeHGlobal(_data);
        }
    }
}
