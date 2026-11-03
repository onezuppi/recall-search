using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Recall.Journal;

/// <summary>Reads the NTFS change journal through a volume handle. Opening the handle needs administrator rights (or SYSTEM).</summary>
public sealed class NativeJournalSource : IJournalSource, IDisposable
{
    private const uint FsctlQueryUsnJournal = 0x900f4, FsctlReadUsnJournal = 0x900bb;
    private const uint GenericRead = 0x80000000, ShareAll = 7, OpenExisting = 3, BackupSemantics = 0x02000000, ReadAttributes = 0x80;
    private const int ErrorJournalNotActive = 1179, ErrorJournalDeleteInProgress = 1178, ErrorInvalidFunction = 1;
    private const int ReadBufferBytes = 256 * 1024;

    private readonly Dictionary<string, SafeFileHandle> _volumes = new(StringComparer.OrdinalIgnoreCase);

    public JournalState Query(string volume)
    {
        var buf = new byte[64];
        if (!DeviceIoControl(Volume(volume), FsctlQueryUsnJournal, IntPtr.Zero, 0, buf, (uint)buf.Length, out _, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            throw error is ErrorJournalNotActive or ErrorJournalDeleteInProgress or ErrorInvalidFunction
                ? new JournalUnavailableException($"{volume} has no active change journal (error {error})")
                : new IOException($"querying the change journal of {volume} failed (error {error})");
        }
        // USN_JOURNAL_DATA_V0: UsnJournalID, FirstUsn, NextUsn
        return new JournalState(BinaryPrimitives.ReadUInt64LittleEndian(buf), BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(8)),
            BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(16)));
    }

    public (long NextUsn, IReadOnlyList<UsnRecord> Records) Read(string volume, ulong journalId, long startUsn)
    {
        // READ_USN_JOURNAL_DATA_V0: StartUsn, ReasonMask, ReturnOnlyOnClose, Timeout, BytesToWaitFor, UsnJournalID
        var input = new byte[40];
        BinaryPrimitives.WriteInt64LittleEndian(input, startUsn);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), journalId);

        var output = new byte[ReadBufferBytes];
        var handle = Volume(volume);
        var pinned = GCHandle.Alloc(input, GCHandleType.Pinned);
        try
        {
            if (!DeviceIoControl(handle, FsctlReadUsnJournal, pinned.AddrOfPinnedObject(), (uint)input.Length, output, (uint)output.Length, out var returned, IntPtr.Zero))
                throw new IOException($"reading the change journal of {volume} failed (error {Marshal.GetLastWin32Error()})");
            var (next, records) = UsnRecordParser.Parse(output.AsSpan(0, (int)returned));
            return (next, records);
        }
        finally { pinned.Free(); }
    }

    public string? DirectoryPath(string volume, ulong fileId)
    {
        // FILE_ID_DESCRIPTOR: dwSize, Type (0 = FileIdType), then a 16 byte union that holds the 64 bit id.
        var descriptor = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor, 24);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(8), fileId);
        using var handle = OpenFileById(Volume(volume), descriptor, ReadAttributes, ShareAll, IntPtr.Zero, BackupSemantics);
        if (handle.IsInvalid) return null;
        var sb = new StringBuilder(1024);
        var length = GetFinalPathNameByHandle(handle, sb, (uint)sb.Capacity, 0);
        if (length == 0 || length > sb.Capacity) return null;
        var path = sb.ToString();
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    private SafeFileHandle Volume(string volume)
    {
        if (_volumes.TryGetValue(volume, out var existing)) return existing;
        var handle = CreateFile(@"\\.\" + volume.TrimEnd('\\'), GenericRead, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new UnauthorizedAccessException($"cannot open volume {volume} (error {Marshal.GetLastWin32Error()}); administrator rights are required");
        return _volumes[volume] = handle;
    }

    public void Dispose()
    {
        foreach (var h in _volumes.Values) h.Dispose();
        _volumes.Clear();
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr input, uint inputBytes, byte[] output, uint outputBytes, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(SafeFileHandle volume, byte[] descriptor, uint access, uint share, IntPtr security, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle h, StringBuilder path, uint length, uint flags);
}
