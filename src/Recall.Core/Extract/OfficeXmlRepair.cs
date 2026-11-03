using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Recall.Core.Extract;

/// <summary>
/// Some generators write <c>xml:space=""</c>, which the XML spec forbids and System.Xml rejects, while Excel and Word still open the file.
/// The repair removes that attribute from the XML parts of an in-memory copy. The file on disk is never changed.
/// </summary>
internal static partial class OfficeXmlRepair
{
    [GeneratedRegex("""\sxml:space\s*=\s*(""|'')""")]
    private static partial Regex EmptyXmlSpace();

    public static bool IsInvalidXmlSpace(Exception error)
    {
        for (var e = error; e != null; e = e.InnerException)
            if (e is XmlException && e.Message.Contains("xml:space", StringComparison.Ordinal)) return true;
        return false;
    }

    public static MemoryStream RepairedCopy(string path)
    {
        var output = new MemoryStream();
        using (var source = ZipFile.OpenRead(path))
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries.Where(e => !e.FullName.EndsWith('/')))
            {
                using var input = entry.Open();
                using var copy = target.CreateEntry(entry.FullName, CompressionLevel.Fastest).Open();
                if (IsXmlPart(entry.FullName)) CopyWithoutEmptyXmlSpace(input, copy);
                else input.CopyTo(copy);
            }
        }
        output.Position = 0;
        return output;
    }

    private static bool IsXmlPart(string name) =>
        name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);

    private static void CopyWithoutEmptyXmlSpace(Stream input, Stream output)
    {
        using var reader = new StreamReader(input, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        var cleaned = EmptyXmlSpace().Replace(reader.ReadToEnd(), "");
        output.Write(new UTF8Encoding(false).GetBytes(cleaned));
    }
}
