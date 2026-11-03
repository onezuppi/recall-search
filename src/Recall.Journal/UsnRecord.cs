using System.Buffers.Binary;
using System.Text;

namespace Recall.Journal;

/// <summary>The parts of a USN_RECORD_V2 the collector needs.</summary>
public readonly record struct UsnRecord(ulong FileId, ulong ParentId, long Usn, uint Reason, uint Attributes, string Name)
{
    public const uint ReasonDataOverwrite = 0x1, ReasonDataExtend = 0x2, ReasonDataTruncation = 0x4;
    public const uint ReasonFileCreate = 0x100, ReasonRenameNewName = 0x2000;
    public const uint AttributeDirectory = 0x10;

    /// <summary>A change that can alter what a file contains or make a new file appear under this name.</summary>
    public const uint ContentReasons =
        ReasonDataOverwrite | ReasonDataExtend | ReasonDataTruncation | ReasonFileCreate | ReasonRenameNewName;

    public bool IsDirectory => (Attributes & AttributeDirectory) != 0;
}

/// <summary>Reads the output of FSCTL_READ_USN_JOURNAL: an 8 byte next-USN value followed by packed USN_RECORD_V2 entries.</summary>
public static class UsnRecordParser
{
    private const int HeaderBytes = 8;
    private const int MinRecordBytes = 60;

    public static (long NextUsn, List<UsnRecord> Records) Parse(ReadOnlySpan<byte> buffer)
    {
        var records = new List<UsnRecord>();
        if (buffer.Length < HeaderBytes) return (0, records);
        var next = BinaryPrimitives.ReadInt64LittleEndian(buffer);
        var at = HeaderBytes;
        while (buffer.Length - at >= MinRecordBytes)
        {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[at..]);
            if (length < MinRecordBytes || at + length > buffer.Length) break;
            var rec = buffer.Slice(at, length);
            at += length;
            if (BinaryPrimitives.ReadUInt16LittleEndian(rec[4..]) != 2) continue; // other record versions carry 128 bit ids
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(rec[56..]);
            var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(rec[58..]);
            if (nameOffset + nameLength > length) continue;
            records.Add(new UsnRecord(
                BinaryPrimitives.ReadUInt64LittleEndian(rec[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(rec[16..]),
                BinaryPrimitives.ReadInt64LittleEndian(rec[24..]),
                BinaryPrimitives.ReadUInt32LittleEndian(rec[40..]),
                BinaryPrimitives.ReadUInt32LittleEndian(rec[52..]),
                Encoding.Unicode.GetString(rec.Slice(nameOffset, nameLength))));
        }
        return (next, records);
    }
}
