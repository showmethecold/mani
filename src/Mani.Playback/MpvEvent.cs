// Adapted from IINA 1.5.1/build 180 MPVNode.parse and MPVController.handleEvent.
// Copyright (c) 2016 lhc. GPL-3.0; see licenses/IINA-GPL-3.0.txt.
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

namespace Mani.Playback;

// Entirely managed. No pointer borrowed from wait_event escapes the client adapter.
internal sealed record MpvEvent(int Id, int Error = 0, ulong Reply = 0,
    long EntryId = 0, int EndReason = 0, long InsertId = 0, int InsertCount = 0,
    string Message = "", MpvProperty? Property = null, object? Result = null);
internal sealed record MpvProperty(string Name, int Format, object? Value);

internal static class MpvData
{
    internal static object? CopyNode(MpvNative.Node node, int depth = 0)
    {
        if (depth > 64) throw new InvalidDataException("mpv node nesting exceeds the supported depth.");
        switch (node.Format)
        {
            case 0: return null;
            case 1: return MpvNative.Utf8(node.Pointer);
            case 3: return node.Flag != 0;
            case 4: return node.Integer;
            case 5: return node.Number;
            case 7:
            case 8:
                var list = Marshal.PtrToStructure<MpvNative.NodeList>(node.Pointer);
                if (list.Count < 0 || list.Count > 1_000_000) throw new InvalidDataException("Invalid mpv node count.");
                var values = new object?[list.Count];
                for (int i = 0; i < values.Length; i++)
                    values[i] = CopyNode(Marshal.PtrToStructure<MpvNative.Node>(list.Values + i * 16), depth + 1);
                if (node.Format == 7) return Array.AsReadOnly(values);
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (int i = 0; i < values.Length; i++)
                    map[MpvNative.Utf8(Marshal.ReadIntPtr(list.Keys, i * IntPtr.Size))] = values[i];
                return new ReadOnlyDictionary<string, object?>(map);
            case 9:
                var bytes = Marshal.PtrToStructure<MpvNative.ByteArray>(node.Pointer);
                var copy = new byte[checked((int)bytes.Size)];
                Marshal.Copy(bytes.Data, copy, 0, copy.Length);
                return Array.AsReadOnly(copy);
            default: throw new InvalidDataException($"Unsupported mpv node format: {node.Format}.");
        }
    }

    internal static MpvProperty CopyProperty(MpvNative.PropertyEvent property)
    {
        object? value = property.Format == 0 || property.Data == 0 ? null : property.Format switch
        {
            1 => MpvNative.Utf8(Marshal.ReadIntPtr(property.Data)),
            3 => Marshal.ReadInt32(property.Data) != 0,
            4 => Marshal.ReadInt64(property.Data),
            5 => Marshal.PtrToStructure<double>(property.Data),
            6 => CopyNode(Marshal.PtrToStructure<MpvNative.Node>(property.Data)),
            _ => null
        };
        return new(MpvNative.Utf8(property.Name), property.Format, value);
    }

    internal static MpvEvent CopyEvent(MpvNative.Event data)
    {
        switch (data.Id)
        {
            case 2:
                var log = Marshal.PtrToStructure<MpvNative.LogMessage>(data.Data);
                return new(data.Id, Message: $"{MpvNative.Utf8(log.Prefix)}: {MpvNative.Utf8(log.Text).Trim()}");
            case 6: return new(data.Id, EntryId: Marshal.ReadInt64(data.Data));
            case 7:
                var end = Marshal.PtrToStructure<MpvNative.EndFile>(data.Data);
                return new(data.Id, end.Error, EntryId: end.PlaylistEntryId, EndReason: end.Reason,
                    InsertId: end.InsertId, InsertCount: end.InsertCount,
                    Message: end.Error < 0 ? MpvNative.Error(end.Error) : "");
            case 5: // mpv_event_command starts with its mpv_node result.
                return new(data.Id, data.Error, data.Reply,
                    Message: data.Error < 0 ? MpvNative.Error(data.Error) : "",
                    Result: data.Data == 0 ? null : CopyNode(Marshal.PtrToStructure<MpvNative.Node>(data.Data)));
            case 22:
                return new(data.Id, data.Error, data.Reply,
                    Property: data.Data == 0 ? null : CopyProperty(Marshal.PtrToStructure<MpvNative.PropertyEvent>(data.Data)));
            default: return new(data.Id, data.Error, data.Reply);
        }
    }
}
