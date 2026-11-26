using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ReadStatus = DiskScan.Core.ContentSearchService.ReadStatus;
using ContentMatcher = DiskScan.Core.ContentSearchService.ContentMatcher;

namespace DiskScan.Core;

/// <summary>
/// Text from OLE compound documents: HWP 5 (한글), Word 97-2003 (.doc),
/// Excel 97-2003 (.xls) and PowerPoint 97-2003 (.ppt). Encrypted files are skipped.
/// </summary>
internal static class LegacyDocumentText
{
    private const long MaximumStreamBytes = 256L * 1024 * 1024;
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private const int HwpTagParagraphText = 67;

    public static ReadStatus ReadHwp(string path, ContentMatcher matcher, CancellationToken cancellationToken)
    {
        try
        {
            using CompoundFile? file = CompoundFile.TryOpen(path);
            if (file is null || file.Find("FileHeader") is not { } headerEntry)
                return ReadStatus.NotSearchable;

            byte[] header = file.ReadStream(headerEntry, 256);
            if (header.Length < 40 || !Encoding.ASCII.GetString(header, 0, 17).Equals("HWP Document File", StringComparison.Ordinal))
                return ReadStatus.NotSearchable;
            uint properties = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(36, 4));
            bool compressed = (properties & 1) != 0;
            if ((properties & 0x2) != 0 || (properties & 0x4) != 0)
                return ReadStatus.NotSearchable; // password-protected or distribution (ViewText) document

            if (file.Find("BodyText") is not { } body)
                return ReadStatus.NotSearchable;
            var sections = file.Children(body)
                .Select(entry => (Entry: entry, Number: SectionNumber(entry.Name)))
                .Where(item => item.Number >= 0)
                .OrderBy(item => item.Number)
                .Select(item => item.Entry)
                .ToArray();
            if (sections.Length == 0)
                return ReadStatus.NotSearchable;

            long expanded = 0;
            var text = new StringBuilder(4096);
            Span<byte> headerBytes = stackalloc byte[4];
            foreach (CompoundFile.Entry section in sections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] raw = file.ReadStream(section, MaximumStreamBytes);
                using Stream records = compressed
                    ? new DeflateStream(new MemoryStream(raw, writable: false), CompressionMode.Decompress)
                    : new MemoryStream(raw, writable: false);
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (records.ReadAtLeast(headerBytes, 4, throwOnEndOfStream: false) < 4)
                        break;
                    uint recordHeader = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes);
                    int tag = (int)(recordHeader & 0x3FF);
                    long size = recordHeader >> 20;
                    if (size == 0xFFF)
                    {
                        if (records.ReadAtLeast(headerBytes, 4, throwOnEndOfStream: false) < 4)
                            break;
                        size = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes);
                    }
                    expanded += size;
                    if (size > int.MaxValue || expanded > MaximumExpandedBytes)
                        return ReadStatus.NotSearchable;

                    if (tag != HwpTagParagraphText)
                    {
                        Skip(records, size);
                        continue;
                    }

                    byte[] data = new byte[size];
                    if (records.ReadAtLeast(data, (int)size, throwOnEndOfStream: false) < size)
                        break;
                    AppendHwpParagraph(data, text);
                    if (text.Length >= 8192 && FeedAndClear(matcher, text))
                        return ReadStatus.Decided;
                }
            }

            return FeedAndClear(matcher, text) ? ReadStatus.Decided : ReadStatus.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ReadStatus.NotSearchable;
        }
    }

    private static int SectionNumber(string name) =>
        name.StartsWith("Section", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.AsSpan(7), out int number) ? number : -1;

    private static void AppendHwpParagraph(byte[] data, StringBuilder text)
    {
        int count = data.Length / 2;
        for (int index = 0; index < count; index++)
        {
            char value = (char)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(index * 2, 2));
            if (value >= 32)
            {
                text.Append(value);
                continue;
            }

            switch (value)
            {
                case '\0':
                    break;
                case '\n':
                case '\r':
                    text.Append('\n');
                    break;
                case (char)24:
                    text.Append('-');
                    break;
                case (char)25: case (char)26: case (char)27: case (char)28: case (char)29: case (char)30: case (char)31:
                    text.Append(' ');
                    break;
                case '\t':
                    text.Append(' ');
                    index += 7;
                    break;
                default:
                    // Inline and extended controls (fields, tables, pictures …) occupy 8 code units.
                    index += 7;
                    break;
            }
        }
        text.Append('\n');
    }

    public static ReadStatus ReadLegacyOffice(string path, string extension, ContentMatcher matcher, CancellationToken cancellationToken)
    {
        try
        {
            using CompoundFile? file = CompoundFile.TryOpen(path);
            if (file is null || file.Find("EncryptedPackage") is not null)
                return ReadStatus.NotSearchable;

            return extension switch
            {
                ".doc" or ".dot" => ReadWord(file, matcher, cancellationToken),
                ".xls" or ".xlt" => ReadExcel(file, matcher, cancellationToken),
                ".ppt" or ".pps" or ".pot" => ReadPowerPoint(file, matcher, cancellationToken),
                _ => ReadStatus.NotSearchable
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ReadStatus.NotSearchable;
        }
    }

    private static ReadStatus ReadWord(CompoundFile file, ContentMatcher matcher, CancellationToken cancellationToken)
    {
        if (file.Find("WordDocument") is not { } wordEntry)
            return ReadStatus.NotSearchable;
        byte[] word = file.ReadStream(wordEntry, MaximumStreamBytes);
        if (word.Length < 64 || BinaryPrimitives.ReadUInt16LittleEndian(word) != 0xA5EC)
            return ReadStatus.NotSearchable;
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(0x0A));
        if ((flags & 0x0100) != 0)
            return ReadStatus.NotSearchable; // encrypted

        int position = 32;
        int csw = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(position));
        position += 2 + csw * 2;
        int cslw = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(position));
        position += 2 + cslw * 4;
        int fcLcbCount = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(position));
        position += 2;
        if (fcLcbCount < 34 || position + 34 * 8 > word.Length)
            return ReadStatus.NotSearchable;
        uint fcClx = BinaryPrimitives.ReadUInt32LittleEndian(word.AsSpan(position + 33 * 8));
        uint lcbClx = BinaryPrimitives.ReadUInt32LittleEndian(word.AsSpan(position + 33 * 8 + 4));

        string tableName = (flags & 0x0200) != 0 ? "1Table" : "0Table";
        if (file.Find(tableName) is not { } tableEntry)
            return ReadStatus.NotSearchable;
        byte[] table = file.ReadStream(tableEntry, MaximumStreamBytes);
        if (lcbClx == 0 || fcClx > table.Length || lcbClx > table.Length - fcClx)
            return ReadStatus.NotSearchable;

        ReadOnlySpan<byte> clx = table.AsSpan((int)fcClx, (int)lcbClx);
        int at = 0;
        while (at < clx.Length && clx[at] == 0x01)
        {
            int grpprl = BinaryPrimitives.ReadInt16LittleEndian(clx[(at + 1)..]);
            at += 3 + grpprl;
        }
        if (at >= clx.Length || clx[at] != 0x02)
            return ReadStatus.NotSearchable;
        int lcb = (int)BinaryPrimitives.ReadUInt32LittleEndian(clx[(at + 1)..]);
        ReadOnlySpan<byte> plc = clx.Slice(at + 5, Math.Min(lcb, clx.Length - at - 5));
        int pieces = (plc.Length - 4) / 12;
        if (pieces <= 0)
            return ReadStatus.NotSearchable;

        Encoding ansi = Encoding.GetEncoding(1252);
        var text = new StringBuilder(4096);
        for (int piece = 0; piece < pieces; piece++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int cpStart = BinaryPrimitives.ReadInt32LittleEndian(plc[(piece * 4)..]);
            int cpEnd = BinaryPrimitives.ReadInt32LittleEndian(plc[((piece + 1) * 4)..]);
            int characters = cpEnd - cpStart;
            if (characters <= 0)
                continue;
            uint fc = BinaryPrimitives.ReadUInt32LittleEndian(plc[((pieces + 1) * 4 + piece * 8 + 2)..]);
            bool compressed = (fc & 0x40000000) != 0;
            long offset = compressed ? (fc & 0x3FFFFFFF) / 2 : fc & 0x3FFFFFFF;
            long bytes = compressed ? characters : characters * 2L;
            if (offset < 0 || offset + bytes > word.Length)
                continue;

            string segment = compressed
                ? ansi.GetString(word, (int)offset, (int)bytes)
                : Encoding.Unicode.GetString(word, (int)offset, (int)bytes);
            foreach (char value in segment)
                text.Append(value == '\r' ? '\n' : value < 32 ? ' ' : value);
            if (text.Length >= 8192 && FeedAndClear(matcher, text))
                return ReadStatus.Decided;
        }
        return FeedAndClear(matcher, text) ? ReadStatus.Decided : ReadStatus.Completed;
    }

    private static ReadStatus ReadExcel(CompoundFile file, ContentMatcher matcher, CancellationToken cancellationToken)
    {
        if ((file.Find("Workbook") ?? file.Find("Book")) is not { } bookEntry)
            return ReadStatus.NotSearchable;
        byte[] book = file.ReadStream(bookEntry, MaximumStreamBytes);
        var text = new StringBuilder(4096);
        int position = 0;
        while (position + 4 <= book.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int type = BinaryPrimitives.ReadUInt16LittleEndian(book.AsSpan(position));
            int length = BinaryPrimitives.ReadUInt16LittleEndian(book.AsSpan(position + 2));
            int dataStart = position + 4;
            if (dataStart + length > book.Length)
                break;

            if (type == 0x002F)
                return ReadStatus.NotSearchable; // FILEPASS: encrypted workbook
            if (type == 0x00FC)
            {
                // SST: the string table, continued in CONTINUE (0x003C) records.
                var segments = new List<ArraySegment<byte>> { new(book, dataStart, length) };
                int next = dataStart + length;
                while (next + 4 <= book.Length && BinaryPrimitives.ReadUInt16LittleEndian(book.AsSpan(next)) == 0x003C)
                {
                    int continueLength = BinaryPrimitives.ReadUInt16LittleEndian(book.AsSpan(next + 2));
                    if (next + 4 + continueLength > book.Length)
                        break;
                    segments.Add(new ArraySegment<byte>(book, next + 4, continueLength));
                    next += 4 + continueLength;
                }
                if (ReadSharedStrings(segments, matcher, text, cancellationToken))
                    return ReadStatus.Decided;
                position = next;
                continue;
            }
            if (type == 0x0204 && length > 9)
            {
                // LABEL: row, column, format, then a unicode string.
                int count = BinaryPrimitives.ReadUInt16LittleEndian(book.AsSpan(dataStart + 6));
                bool wide = (book[dataStart + 8] & 1) != 0;
                int bytes = Math.Min(count * (wide ? 2 : 1), length - 9);
                text.Append(wide ? Encoding.Unicode.GetString(book, dataStart + 9, bytes & ~1) : Encoding.Latin1.GetString(book, dataStart + 9, bytes));
                text.Append('\n');
            }
            else if (type == 0x0085 && length > 8)
            {
                // BOUNDSHEET: sheet names.
                int count = book[dataStart + 6];
                bool wide = (book[dataStart + 7] & 1) != 0;
                int bytes = Math.Min(count * (wide ? 2 : 1), length - 8);
                text.Append(wide ? Encoding.Unicode.GetString(book, dataStart + 8, bytes & ~1) : Encoding.Latin1.GetString(book, dataStart + 8, bytes));
                text.Append('\n');
            }
            position = dataStart + length;
        }
        return FeedAndClear(matcher, text) ? ReadStatus.Decided : ReadStatus.Completed;
    }

    private static bool ReadSharedStrings(List<ArraySegment<byte>> segments, ContentMatcher matcher, StringBuilder text, CancellationToken cancellationToken)
    {
        var reader = new SegmentReader(segments);
        if (!reader.TrySkip(8))
            return false;
        while (!reader.AtEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reader.TryReadUInt16(out int count) || !reader.TryReadByte(out byte options))
                break;
            bool wide = (options & 0x01) != 0;
            int runs = 0;
            uint extension = 0;
            if ((options & 0x08) != 0 && !reader.TryReadUInt16(out runs))
                break;
            if ((options & 0x04) != 0 && !reader.TryReadUInt32(out extension))
                break;

            int remaining = count;
            while (remaining > 0)
            {
                if (reader.AtSegmentEnd)
                {
                    // A string split over CONTINUE records restarts with its own width flag.
                    if (!reader.NextSegment() || !reader.TryReadByte(out byte continued))
                        return FeedAndClear(matcher, text);
                    wide = (continued & 0x01) != 0;
                }
                int available = reader.SegmentRemaining / (wide ? 2 : 1);
                if (available == 0)
                {
                    if (!reader.NextSegment())
                        return FeedAndClear(matcher, text);
                    if (!reader.TryReadByte(out byte continued))
                        return FeedAndClear(matcher, text);
                    wide = (continued & 0x01) != 0;
                    continue;
                }
                int take = Math.Min(remaining, available);
                reader.ReadCharacters(take, wide, text);
                remaining -= take;
            }
            text.Append('\n');
            if (!reader.TrySkip(runs * 4L) || !reader.TrySkip(extension))
                break;
            if (text.Length >= 8192 && FeedAndClear(matcher, text))
                return true;
        }
        return FeedAndClear(matcher, text);
    }

    private static ReadStatus ReadPowerPoint(CompoundFile file, ContentMatcher matcher, CancellationToken cancellationToken)
    {
        if (file.Find("PowerPoint Document") is not { } documentEntry)
            return ReadStatus.NotSearchable;
        byte[] document = file.ReadStream(documentEntry, MaximumStreamBytes);
        var text = new StringBuilder(4096);
        int position = 0;
        while (position + 8 <= document.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int versionInstance = BinaryPrimitives.ReadUInt16LittleEndian(document.AsSpan(position));
            int type = BinaryPrimitives.ReadUInt16LittleEndian(document.AsSpan(position + 2));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(document.AsSpan(position + 4));
            int dataStart = position + 8;
            if ((versionInstance & 0x0F) == 0x0F)
            {
                position = dataStart; // container: walk into its children
                continue;
            }
            if (length > document.Length - dataStart)
                break;

            if (type == 0x0FA0)
                text.Append(Encoding.Unicode.GetString(document, dataStart, (int)length & ~1)).Append('\n');
            else if (type == 0x0FA8)
                text.Append(Encoding.Latin1.GetString(document, dataStart, (int)length)).Append('\n');

            if (text.Length >= 8192 && FeedAndClear(matcher, text))
                return ReadStatus.Decided;
            position = dataStart + (int)length;
        }
        return FeedAndClear(matcher, text) ? ReadStatus.Decided : ReadStatus.Completed;
    }

    private static bool FeedAndClear(ContentMatcher matcher, StringBuilder text)
    {
        if (text.Length == 0)
            return matcher.IsDecided;
        foreach (ReadOnlyMemory<char> chunk in text.GetChunks())
        {
            if (matcher.Feed(chunk.Span))
            {
                text.Clear();
                return true;
            }
        }
        text.Clear();
        return matcher.Feed(" ");
    }

    private static void Skip(Stream stream, long count)
    {
        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Current);
            return;
        }
        Span<byte> buffer = stackalloc byte[4096];
        while (count > 0)
        {
            int read = stream.Read(buffer[..(int)Math.Min(buffer.Length, count)]);
            if (read == 0)
                return;
            count -= read;
        }
    }

    private sealed class SegmentReader(List<ArraySegment<byte>> segments)
    {
        private int _segment;
        private int _position;

        public bool AtEnd => _segment >= segments.Count || (_segment == segments.Count - 1 && _position >= segments[_segment].Count);
        public bool AtSegmentEnd => _segment < segments.Count && _position >= segments[_segment].Count;
        public int SegmentRemaining => _segment < segments.Count ? segments[_segment].Count - _position : 0;

        public bool NextSegment()
        {
            if (_segment + 1 >= segments.Count)
            {
                _segment = segments.Count;
                return false;
            }
            _segment++;
            _position = 0;
            return true;
        }

        private bool Ensure()
        {
            while (_segment < segments.Count && _position >= segments[_segment].Count)
            {
                _segment++;
                _position = 0;
            }
            return _segment < segments.Count;
        }

        public bool TryReadByte(out byte value)
        {
            value = 0;
            if (!Ensure())
                return false;
            value = segments[_segment][_position++];
            return true;
        }

        public bool TryReadUInt16(out int value)
        {
            value = 0;
            if (!TryReadByte(out byte low) || !TryReadByte(out byte high))
                return false;
            value = low | high << 8;
            return true;
        }

        public bool TryReadUInt32(out uint value)
        {
            value = 0;
            if (!TryReadUInt16(out int low) || !TryReadUInt16(out int high))
                return false;
            value = (uint)low | (uint)high << 16;
            return true;
        }

        public bool TrySkip(long count)
        {
            while (count > 0)
            {
                if (!Ensure())
                    return false;
                int step = (int)Math.Min(count, segments[_segment].Count - _position);
                _position += step;
                count -= step;
            }
            return true;
        }

        public void ReadCharacters(int count, bool wide, StringBuilder text)
        {
            ArraySegment<byte> segment = segments[_segment];
            for (int index = 0; index < count; index++)
            {
                if (wide)
                {
                    text.Append((char)(segment[_position] | segment[_position + 1] << 8));
                    _position += 2;
                }
                else
                {
                    text.Append((char)segment[_position]);
                    _position++;
                }
            }
        }
    }
}

/// <summary>Minimal read-only reader for OLE compound files (structured storage).</summary>
internal sealed class CompoundFile : IDisposable
{
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSector = 0xFFFFFFFF;
    private const uint MaximumRegularSector = 0xFFFFFFFA;
    private static ReadOnlySpan<byte> Signature => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private readonly FileStream _stream;
    private readonly int _sectorSize;
    private readonly uint _miniCutoff;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly Entry[] _entries;
    private byte[]? _miniStream;

    internal sealed record Entry(int Id, string Name, byte Type, int Left, int Right, int Child, uint Start, long Size);

    private CompoundFile(FileStream stream, int sectorSize, uint miniCutoff, uint[] fat, uint[] miniFat, Entry[] entries)
    {
        _stream = stream;
        _sectorSize = sectorSize;
        _miniCutoff = miniCutoff;
        _fat = fat;
        _miniFat = miniFat;
        _entries = entries;
    }

    public static CompoundFile? TryOpen(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        try
        {
            Span<byte> header = stackalloc byte[512];
            if (stream.ReadAtLeast(header, 512, throwOnEndOfStream: false) < 512 || !header[..8].SequenceEqual(Signature))
            {
                stream.Dispose();
                return null;
            }

            int sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header[0x1E..]);
            if (sectorShift is not (9 or 12) || BinaryPrimitives.ReadUInt16LittleEndian(header[0x20..]) != 6)
                throw new InvalidDataException("Unsupported compound file sector size.");
            int sectorSize = 1 << sectorShift;
            uint fatSectors = BinaryPrimitives.ReadUInt32LittleEndian(header[0x2C..]);
            uint firstDirectory = BinaryPrimitives.ReadUInt32LittleEndian(header[0x30..]);
            uint miniCutoff = BinaryPrimitives.ReadUInt32LittleEndian(header[0x38..]);
            uint firstMiniFat = BinaryPrimitives.ReadUInt32LittleEndian(header[0x3C..]);
            uint firstDifat = BinaryPrimitives.ReadUInt32LittleEndian(header[0x44..]);
            uint difatSectors = BinaryPrimitives.ReadUInt32LittleEndian(header[0x48..]);
            long sectorCount = Math.Max(1, (stream.Length + sectorSize - 1) / sectorSize);
            if (fatSectors > sectorCount || difatSectors > sectorCount)
                throw new InvalidDataException("Compound file header is out of range.");

            var fatLocations = new List<uint>();
            for (int index = 0; index < 109 && fatLocations.Count < fatSectors; index++)
                fatLocations.Add(BinaryPrimitives.ReadUInt32LittleEndian(header[(0x4C + index * 4)..]));

            var sector = new byte[sectorSize];
            uint difat = firstDifat;
            for (uint count = 0; count < difatSectors && difat <= MaximumRegularSector && fatLocations.Count < fatSectors; count++)
            {
                ReadSector(stream, sectorSize, difat, sector);
                int perSector = sectorSize / 4 - 1;
                for (int index = 0; index < perSector && fatLocations.Count < fatSectors; index++)
                    fatLocations.Add(BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(index * 4)));
                difat = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(perSector * 4));
            }

            var fat = new uint[fatLocations.Count * (sectorSize / 4)];
            for (int index = 0; index < fatLocations.Count; index++)
            {
                ReadSector(stream, sectorSize, fatLocations[index], sector);
                for (int entry = 0; entry < sectorSize / 4; entry++)
                    fat[index * (sectorSize / 4) + entry] = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(entry * 4));
            }

            byte[] directory = ReadChain(stream, sectorSize, fat, firstDirectory, 64L * 1024 * 1024);
            var entries = new Entry[directory.Length / 128];
            for (int index = 0; index < entries.Length; index++)
            {
                ReadOnlySpan<byte> raw = directory.AsSpan(index * 128, 128);
                int nameLength = Math.Clamp((int)BinaryPrimitives.ReadUInt16LittleEndian(raw[0x40..]), 0, 64);
                string name = nameLength >= 2 ? Encoding.Unicode.GetString(raw[..(nameLength - 2)]) : string.Empty;
                long size = sectorSize == 512
                    ? BinaryPrimitives.ReadUInt32LittleEndian(raw[0x78..])
                    : (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(raw[0x78..]), long.MaxValue);
                entries[index] = new Entry(index, name, raw[0x42],
                    BinaryPrimitives.ReadInt32LittleEndian(raw[0x44..]),
                    BinaryPrimitives.ReadInt32LittleEndian(raw[0x48..]),
                    BinaryPrimitives.ReadInt32LittleEndian(raw[0x4C..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(raw[0x74..]), size);
            }
            if (entries.Length == 0 || entries[0].Type != 5)
                throw new InvalidDataException("Compound file has no root entry.");

            byte[] miniFatBytes = firstMiniFat <= MaximumRegularSector
                ? ReadChain(stream, sectorSize, fat, firstMiniFat, 64L * 1024 * 1024)
                : [];
            var miniFat = new uint[miniFatBytes.Length / 4];
            for (int index = 0; index < miniFat.Length; index++)
                miniFat[index] = BinaryPrimitives.ReadUInt32LittleEndian(miniFatBytes.AsSpan(index * 4));

            return new CompoundFile(stream, sectorSize, miniCutoff == 0 ? 4096 : miniCutoff, fat, miniFat, entries);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Finds a direct child of the root storage, or of the given storage path.</summary>
    public Entry? Find(params string[] path)
    {
        Entry current = _entries[0];
        foreach (string name in path)
        {
            Entry? next = Children(current).FirstOrDefault(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (next is null)
                return null;
            current = next;
        }
        return current;
    }

    public IEnumerable<Entry> Children(Entry storage)
    {
        var result = new List<Entry>();
        var visited = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(storage.Child);
        while (pending.Count > 0)
        {
            int id = pending.Pop();
            if (id < 0 || id >= _entries.Length || !visited.Add(id))
                continue;
            Entry entry = _entries[id];
            result.Add(entry);
            pending.Push(entry.Left);
            pending.Push(entry.Right);
        }
        return result;
    }

    public byte[] ReadStream(Entry entry, long maximumBytes)
    {
        long size = Math.Min(entry.Size, maximumBytes);
        if (size <= 0)
            return [];
        if (entry.Size < _miniCutoff && entry.Type != 5)
        {
            _miniStream ??= ReadChain(_stream, _sectorSize, _fat, _entries[0].Start, Math.Min(_entries[0].Size, 256L * 1024 * 1024));
            var result = new byte[size];
            uint sector = entry.Start;
            long written = 0;
            int guard = 0;
            while (written < size && sector <= MaximumRegularSector && sector < _miniFat.Length && guard++ <= _miniFat.Length)
            {
                long offset = (long)sector * 64;
                if (offset + 64 > _miniStream.Length)
                    break;
                int count = (int)Math.Min(64, size - written);
                Array.Copy(_miniStream, offset, result, written, count);
                written += count;
                sector = _miniFat[sector];
            }
            return written == size ? result : result.AsSpan(0, (int)written).ToArray();
        }
        return ReadChain(_stream, _sectorSize, _fat, entry.Start, size);
    }

    private static byte[] ReadChain(FileStream stream, int sectorSize, uint[] fat, uint start, long maximumBytes)
    {
        using var output = new MemoryStream();
        var sector = new byte[sectorSize];
        uint current = start;
        int guard = 0;
        while (current <= MaximumRegularSector && current < fat.Length && output.Length < maximumBytes && guard++ <= fat.Length)
        {
            ReadSector(stream, sectorSize, current, sector);
            output.Write(sector, 0, (int)Math.Min(sectorSize, maximumBytes - output.Length));
            current = fat[current];
        }
        if (current is not (EndOfChain or FreeSector) && output.Length < maximumBytes)
            throw new InvalidDataException("Broken compound file sector chain.");
        return output.ToArray();
    }

    private static void ReadSector(FileStream stream, int sectorSize, uint sector, byte[] buffer)
    {
        long offset = ((long)sector + 1) * sectorSize;
        if (offset >= stream.Length)
            throw new InvalidDataException("Compound file sector is outside the file.");
        stream.Position = offset;
        int read = stream.ReadAtLeast(buffer, sectorSize, throwOnEndOfStream: false);
        if (read < sectorSize)
            Array.Clear(buffer, read, sectorSize - read);
    }

    public void Dispose() => _stream.Dispose();
}
