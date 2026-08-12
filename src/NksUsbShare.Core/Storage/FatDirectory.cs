using System.Buffers.Binary;
using System.Text;

namespace NksUsbShare.Core.Storage;

/// <summary>
/// Just enough FAT to list the root directory of a remote medium.
///
/// This is not a filesystem driver and is not meant to become one; it exists so a machine that
/// cannot be given the device — macOS, where no virtual USB device can be created — can still show
/// what is on it. Reading is all it does.
/// </summary>
public static class FatDirectory
{
    public sealed record Entry(string Name, long Size, bool IsDirectory, DateTime? Modified);

    public sealed record Layout(uint RootDirectorySector, uint SectorsPerCluster, uint DataStartSector, uint RootCluster)
    {
        public uint SectorOfCluster(uint cluster) => DataStartSector + (cluster - 2) * SectorsPerCluster;
    }

    /// <summary>
    /// Works out where the root directory lives from the parameter block at the start of a volume.
    /// Returns null when the sector is not a FAT volume.
    /// </summary>
    public static Layout? ReadLayout(ReadOnlySpan<byte> bootSector, uint volumeStartSector)
    {
        if (bootSector.Length < 512) return null;
        if (bootSector[510] != 0x55 || bootSector[511] != 0xAA) return null;

        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(bootSector.Slice(11, 2));
        var sectorsPerCluster = bootSector[13];
        var reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(bootSector.Slice(14, 2));
        var fatCount = bootSector[16];
        var rootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(bootSector.Slice(17, 2));
        var fatSize16 = BinaryPrimitives.ReadUInt16LittleEndian(bootSector.Slice(22, 2));

        if (bytesPerSector is 0 || sectorsPerCluster is 0 || fatCount is 0) return null;

        // FAT32 sets the 16 bit field to zero and puts the real size in the 32 bit one.
        var fatSize = fatSize16 != 0 ? fatSize16 : BinaryPrimitives.ReadUInt32LittleEndian(bootSector.Slice(36, 4));
        if (fatSize == 0) return null;

        var rootDirectorySectors = (uint)((rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector);
        var firstDataSector = volumeStartSector + reservedSectors + fatCount * fatSize + rootDirectorySectors;

        // On FAT12 and FAT16 the root directory sits in its own fixed area before the data region;
        // on FAT32 it is an ordinary cluster chain starting at the cluster named in the header.
        if (rootEntryCount > 0)
        {
            var rootSector = volumeStartSector + reservedSectors + fatCount * fatSize;
            return new Layout(rootSector, sectorsPerCluster, firstDataSector, 0);
        }

        var rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(bootSector.Slice(44, 4));
        if (rootCluster < 2) return null;

        var layout = new Layout(0, sectorsPerCluster, firstDataSector, rootCluster);
        return layout with { RootDirectorySector = layout.SectorOfCluster(rootCluster) };
    }

    /// <summary>
    /// Parses directory entries out of a raw directory region. Long file names are assembled from
    /// the entries that precede each short one, which is how FAT stores them.
    /// </summary>
    public static IReadOnlyList<Entry> Parse(ReadOnlySpan<byte> directory)
    {
        var entries = new List<Entry>();
        var longName = new StringBuilder();

        for (var offset = 0; offset + 32 <= directory.Length; offset += 32)
        {
            var record = directory.Slice(offset, 32);
            var first = record[0];

            if (first == 0x00) break;          // nothing beyond this point
            if (first == 0xE5) { longName.Clear(); continue; }  // deleted

            var attributes = record[11];

            if ((attributes & 0x0F) == 0x0F)
            {
                // A long name fragment: five, six and two UTF-16 units at fixed offsets.
                var fragment = new StringBuilder();
                foreach (var (start, count) in ((int, int)[])[(1, 5), (14, 6), (28, 2)])
                    for (var i = 0; i < count; i++)
                    {
                        var unit = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(start + i * 2, 2));
                        if (unit is 0x0000 or 0xFFFF) break;
                        fragment.Append((char)unit);
                    }

                // Fragments arrive in reverse order, so each one goes in front.
                longName.Insert(0, fragment);
                continue;
            }

            if ((attributes & 0x08) != 0) { longName.Clear(); continue; }  // volume label, not a file

            var shortName = Encoding.ASCII.GetString(record[..8]).TrimEnd();
            var extension = Encoding.ASCII.GetString(record.Slice(8, 3)).TrimEnd();
            var name = longName.Length > 0
                ? longName.ToString()
                : extension.Length > 0 ? $"{shortName}.{extension}" : shortName;

            longName.Clear();
            if (name is "." or "..") continue;

            entries.Add(new Entry(
                name,
                BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(28, 4)),
                (attributes & 0x10) != 0,
                DecodeTimestamp(
                    BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(24, 2)),
                    BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(22, 2)))));
        }

        return entries;
    }

    /// <summary>Finds the volume label, which FAT keeps as a directory entry rather than a field.</summary>
    public static string? FindVolumeLabel(ReadOnlySpan<byte> directory)
    {
        for (var offset = 0; offset + 32 <= directory.Length; offset += 32)
        {
            var record = directory.Slice(offset, 32);
            if (record[0] is 0x00) break;
            if (record[0] is 0xE5) continue;

            var attributes = record[11];
            if ((attributes & 0x0F) == 0x0F) continue;
            if ((attributes & 0x08) == 0) continue;

            var label = Encoding.ASCII.GetString(record[..11]).TrimEnd();
            if (label.Length > 0) return label;
        }

        return null;
    }

    private static DateTime? DecodeTimestamp(ushort date, ushort time)
    {
        if (date == 0) return null;

        try
        {
            return new DateTime(
                1980 + (date >> 9), (date >> 5) & 0x0F, date & 0x1F,
                time >> 11, (time >> 5) & 0x3F, (time & 0x1F) * 2);
        }
        catch (ArgumentOutOfRangeException)
        {
            // A corrupt or unset field is not worth failing a listing over.
            return null;
        }
    }
}
