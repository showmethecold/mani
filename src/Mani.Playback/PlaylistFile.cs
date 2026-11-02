// IINA 1.5.1/build 180 MainMenuActions.menuSavePlaylist: ordered UTF-8 M3U8.
// Copyright (c) 2016-2026 lhc and contributors. GPL-3.0; licenses/IINA-GPL-3.0.txt.
using System.Text;

namespace Mani.Playback;

public static class PlaylistFile
{
    public static async Task SaveAsync(string destination, PlaylistSnapshot playlist, CancellationToken token = default)
    {
        var sources = playlist.Entries.Select(entry => entry.Path).ToArray();
        if (sources.Any(source => string.IsNullOrWhiteSpace(source) || source.IndexOfAny(['\r', '\n', '\0']) >= 0 || source.StartsWith('#')))
            throw new InvalidDataException("Playlist contains a source that cannot be represented as an M3U8 line.");
        string path = Path.GetFullPath(destination), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                byte[] data = Encoding.UTF8.GetBytes(string.Concat(sources.Select(source => source + "\n")));
                await stream.WriteAsync(data, token); await stream.FlushAsync(token); stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
