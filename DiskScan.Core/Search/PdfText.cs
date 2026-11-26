using System.IO.Compression;
using System.Text;
using ReadStatus = DiskScan.Core.ContentSearchService.ReadStatus;
using ContentMatcher = DiskScan.Core.ContentSearchService.ContentMatcher;

namespace DiskScan.Core;

/// <summary>
/// Text from PDF pages: Flate/ASCII-hex/ASCII85 content streams, object streams,
/// ToUnicode maps (Identity-H CID fonts) and Korean UCS2/UHC CMaps. Encrypted
/// PDFs and scanned pages without a text layer are not readable.
/// </summary>
internal static class PdfText
{
    private const long MaximumFileBytes = 64L * 1024 * 1024;
    private const int MaximumStreamBytes = 64 * 1024 * 1024;
    private static readonly SemaphoreSlim LargeFileGate = new(2, 2);

    public static ReadStatus Read(string path, ContentMatcher matcher, CancellationToken cancellationToken)
    {
        bool gated = false;
        try
        {
            byte[] data;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                       64 * 1024, FileOptions.SequentialScan))
            {
                if (stream.Length > MaximumFileBytes || stream.Length < 16)
                    return ReadStatus.NotSearchable;
                if (stream.Length > 8 * 1024 * 1024)
                {
                    LargeFileGate.Wait(cancellationToken);
                    gated = true;
                }
                data = new byte[stream.Length];
                stream.ReadExactly(data);
            }
            if (data.AsSpan(0, Math.Min(1024, data.Length)).IndexOf("%PDF"u8) < 0)
                return ReadStatus.NotSearchable;

            var document = new PdfDocument(data, cancellationToken);
            if (document.IsEncrypted)
                return ReadStatus.NotSearchable;

            var output = new TextOutput(matcher);
            var pages = document.Pages();
            if (pages.Count == 0)
                return ReadStatus.NotSearchable;
            foreach (PdfDictionary page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] content = document.PageContent(page);
                if (content.Length > 0)
                    new ContentInterpreter(document, output, cancellationToken).Run(content, document.PageResources(page), 0);
                if (output.Boundary())
                    return ReadStatus.Decided;
            }
            return output.Flush() ? ReadStatus.Decided : ReadStatus.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ReadStatus.NotSearchable;
        }
        finally
        {
            if (gated)
                LargeFileGate.Release();
        }
    }

    // ───────────────────────── objects ─────────────────────────

    internal sealed class PdfDictionary : Dictionary<string, object?>
    {
        public int StreamStart { get; set; } = -1;
        public int StreamLength { get; set; }
        public byte[]? Source { get; set; }
    }

    internal sealed record PdfName(string Value);
    internal sealed record PdfString(byte[] Bytes);
    internal sealed record PdfOperator(string Value);
    internal readonly record struct PdfReference(int Number);
    private sealed class EndOfData { public static readonly EndOfData Instance = new(); }

    private sealed class PdfDocument
    {
        private readonly byte[] _data;
        private readonly CancellationToken _cancellationToken;
        private readonly Dictionary<int, int> _offsets = new();
        private readonly Dictionary<int, object?> _objects = new();
        private readonly HashSet<int> _loading = new();
        private readonly Dictionary<PdfDictionary, FontDecoder> _fonts = new(ReferenceEqualityComparer.Instance);

        public PdfDocument(byte[] data, CancellationToken cancellationToken)
        {
            _data = data;
            _cancellationToken = cancellationToken;
            IndexObjects();
            IsEncrypted = CheckTrailersForEncryption();
            foreach (int number in _offsets.Keys.Order().ToArray())
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (Get(number) is PdfDictionary dictionary)
                {
                    string? type = NameOf(dictionary, "Type");
                    if (type == "XRef" && dictionary.ContainsKey("Encrypt"))
                        IsEncrypted = true;
                    else if (type == "ObjStm")
                        ExpandObjectStream(dictionary);
                }
            }
        }

        public bool IsEncrypted { get; private set; }

        private void IndexObjects()
        {
            ReadOnlySpan<byte> data = _data;
            int position = 0;
            while (position < data.Length)
            {
                int found = data[position..].IndexOf("obj"u8);
                if (found < 0)
                    break;
                int at = position + found;
                position = at + 3;
                if (position < data.Length && !PdfLexer.IsWhite(data[position]) && !PdfLexer.IsDelimiter(data[position]))
                    continue;

                int cursor = at - 1;
                if (cursor < 0 || !PdfLexer.IsWhite(data[cursor]))
                    continue;
                while (cursor >= 0 && PdfLexer.IsWhite(data[cursor])) cursor--;
                int generationEnd = cursor + 1;
                while (cursor >= 0 && char.IsAsciiDigit((char)data[cursor])) cursor--;
                if (cursor + 1 == generationEnd || cursor < 0 || !PdfLexer.IsWhite(data[cursor]))
                    continue;
                while (cursor >= 0 && PdfLexer.IsWhite(data[cursor])) cursor--;
                int numberEnd = cursor + 1;
                while (cursor >= 0 && char.IsAsciiDigit((char)data[cursor])) cursor--;
                int numberStart = cursor + 1;
                if (numberStart == numberEnd || numberEnd - numberStart > 9)
                    continue;
                if (cursor >= 0 && !PdfLexer.IsWhite(data[cursor]) && !PdfLexer.IsDelimiter(data[cursor]))
                    continue;
                int number = 0;
                for (int index = numberStart; index < numberEnd; index++)
                    number = number * 10 + (data[index] - '0');
                _offsets[number] = position;
            }
        }

        private bool CheckTrailersForEncryption()
        {
            ReadOnlySpan<byte> data = _data;
            int position = 0;
            while (position < data.Length)
            {
                int found = data[position..].IndexOf("trailer"u8);
                if (found < 0)
                    return false;
                position += found + 7;
                var lexer = new PdfLexer(_data, position, _data.Length);
                if (lexer.ReadObject() is PdfDictionary trailer && trailer.ContainsKey("Encrypt"))
                    return true;
            }
            return false;
        }

        private void ExpandObjectStream(PdfDictionary dictionary)
        {
            byte[]? content = StreamData(dictionary);
            if (content is null || Resolve(dictionary.GetValueOrDefault("N")) is not double count ||
                Resolve(dictionary.GetValueOrDefault("First")) is not double first || count < 0 || count > 1_000_000)
                return;

            var header = new PdfLexer(content, 0, content.Length);
            var entries = new List<(int Number, int Offset)>();
            for (int index = 0; index < (int)count; index++)
            {
                if (header.ReadObject() is not double number || header.ReadObject() is not double offset)
                    break;
                entries.Add(((int)number, (int)offset));
            }
            foreach ((int number, int offset) in entries)
            {
                if (_offsets.ContainsKey(number) || _objects.ContainsKey(number))
                    continue;
                int start = (int)first + offset;
                if (start < 0 || start >= content.Length)
                    continue;
                _objects[number] = new PdfLexer(content, start, content.Length).ReadObject() switch
                {
                    EndOfData => null,
                    var value => value
                };
            }
        }

        public object? Get(int number)
        {
            if (_objects.TryGetValue(number, out object? cached))
                return cached;
            if (_loading.Count >= 64 || !_offsets.TryGetValue(number, out int offset) || !_loading.Add(number))
                return null;
            try
            {
                var lexer = new PdfLexer(_data, offset, _data.Length);
                object? value = lexer.ReadObject();
                if (value is EndOfData)
                    value = null;
                if (value is PdfDictionary dictionary && lexer.TryReadStreamStart(out int streamStart))
                {
                    int length = Resolve(dictionary.GetValueOrDefault("Length")) is double declared ? (int)declared : -1;
                    if (length < 0 || streamStart + length > _data.Length ||
                        _data.AsSpan(streamStart + length, Math.Min(32, _data.Length - streamStart - length)).IndexOf("endstream"u8) < 0)
                    {
                        int end = _data.AsSpan(streamStart).IndexOf("endstream"u8);
                        length = end < 0 ? _data.Length - streamStart : end;
                    }
                    dictionary.StreamStart = streamStart;
                    dictionary.StreamLength = Math.Max(0, length);
                    dictionary.Source = _data;
                }
                _objects[number] = value;
                return value;
            }
            finally
            {
                _loading.Remove(number);
            }
        }

        public object? Resolve(object? value)
        {
            for (int guard = 0; guard < 8 && value is PdfReference reference; guard++)
                value = Get(reference.Number);
            return value;
        }

        public static string? NameOf(PdfDictionary dictionary, string key) =>
            dictionary.GetValueOrDefault(key) is PdfName name ? name.Value : null;

        public byte[]? StreamData(PdfDictionary dictionary)
        {
            if (dictionary.StreamStart < 0 || dictionary.Source is null)
                return null;
            byte[] data = dictionary.Source.AsSpan(dictionary.StreamStart,
                Math.Min(dictionary.StreamLength, dictionary.Source.Length - dictionary.StreamStart)).ToArray();

            object? filters = Resolve(dictionary.GetValueOrDefault("Filter"));
            object? parameters = Resolve(dictionary.GetValueOrDefault("DecodeParms"));
            var names = filters switch
            {
                PdfName name => new List<string> { name.Value },
                List<object?> list => list.Select(item => (Resolve(item) as PdfName)?.Value ?? "?").ToList(),
                _ => new List<string>()
            };
            for (int index = 0; index < names.Count; index++)
            {
                object? parameter = parameters is List<object?> parameterList
                    ? Resolve(index < parameterList.Count ? parameterList[index] : null)
                    : parameters;
                if (parameter is PdfDictionary decodeParameters &&
                    Resolve(decodeParameters.GetValueOrDefault("Predictor")) is double predictor && predictor > 1)
                    return null;
                data = names[index] switch
                {
                    "FlateDecode" or "Fl" => Inflate(data),
                    "ASCIIHexDecode" or "AHx" => AsciiHex(data),
                    "ASCII85Decode" or "A85" => Ascii85(data),
                    _ => null!
                };
                if (data is null)
                    return null;
            }
            return data;
        }

        private static byte[] Inflate(byte[] data)
        {
            using var output = new MemoryStream();
            try
            {
                using var input = new ZLibStream(new MemoryStream(data, writable: false), CompressionMode.Decompress);
                CopyLimited(input, output);
            }
            catch (InvalidDataException) when (output.Length == 0 && data.Length > 2)
            {
                try
                {
                    using var raw = new DeflateStream(new MemoryStream(data, 2, data.Length - 2, writable: false), CompressionMode.Decompress);
                    CopyLimited(raw, output);
                }
                catch (InvalidDataException)
                {
                    // Keep what was decoded before the damage.
                }
            }
            catch (InvalidDataException)
            {
                // Truncated streams still yield their decoded beginning.
            }
            return output.ToArray();
        }

        private static void CopyLimited(Stream input, MemoryStream output)
        {
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > MaximumStreamBytes)
                    throw new InvalidDataException("PDF stream is too large.");
            }
        }

        private static byte[] AsciiHex(byte[] data)
        {
            var output = new List<byte>(data.Length / 2);
            int high = -1;
            foreach (byte value in data)
            {
                if (value == '>')
                    break;
                int digit = PdfLexer.HexValue(value);
                if (digit < 0)
                    continue;
                if (high < 0) high = digit;
                else { output.Add((byte)(high << 4 | digit)); high = -1; }
            }
            if (high >= 0)
                output.Add((byte)(high << 4));
            return output.ToArray();
        }

        private static byte[] Ascii85(byte[] data)
        {
            var output = new List<byte>(data.Length);
            uint tuple = 0;
            int count = 0;
            for (int index = 0; index < data.Length; index++)
            {
                byte value = data[index];
                if (value == '~')
                    break;
                if (PdfLexer.IsWhite(value))
                    continue;
                if (value == 'z' && count == 0)
                {
                    output.AddRange([0, 0, 0, 0]);
                    continue;
                }
                if (value < '!' || value > 'u')
                    continue;
                tuple = tuple * 85 + (uint)(value - '!');
                if (++count == 5)
                {
                    output.Add((byte)(tuple >> 24)); output.Add((byte)(tuple >> 16)); output.Add((byte)(tuple >> 8)); output.Add((byte)tuple);
                    tuple = 0;
                    count = 0;
                }
            }
            if (count > 1)
            {
                for (int pad = count; pad < 5; pad++)
                    tuple = tuple * 85 + 84;
                for (int index = 0; index < count - 1; index++)
                    output.Add((byte)(tuple >> (24 - index * 8)));
            }
            return output.ToArray();
        }

        public List<PdfDictionary> Pages()
        {
            var pages = new List<PdfDictionary>();
            var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            PdfDictionary? catalog = AllDictionaries().FirstOrDefault(dictionary => NameOf(dictionary, "Type") == "Catalog");
            if (catalog is not null && Resolve(catalog.GetValueOrDefault("Pages")) is PdfDictionary root)
                CollectPages(root, pages, visited, 0);
            if (pages.Count == 0)
                pages.AddRange(AllDictionaries().Where(dictionary => NameOf(dictionary, "Type") == "Page"));
            return pages;
        }

        private IEnumerable<PdfDictionary> AllDictionaries()
        {
            foreach (int number in _offsets.Keys.Concat(_objects.Keys).Distinct().Order().ToArray())
            {
                if (Get(number) is PdfDictionary dictionary)
                    yield return dictionary;
            }
        }

        private void CollectPages(PdfDictionary node, List<PdfDictionary> pages, HashSet<PdfDictionary> visited, int depth)
        {
            if (depth > 64 || !visited.Add(node) || pages.Count > 100_000)
                return;
            _cancellationToken.ThrowIfCancellationRequested();
            if (Resolve(node.GetValueOrDefault("Kids")) is List<object?> kids)
            {
                foreach (object? kid in kids)
                {
                    if (Resolve(kid) is PdfDictionary child)
                        CollectPages(child, pages, visited, depth + 1);
                }
            }
            else if (NameOf(node, "Type") != "Pages")
            {
                pages.Add(node);
            }
        }

        public PdfDictionary? PageResources(PdfDictionary page)
        {
            PdfDictionary? current = page;
            for (int depth = 0; current is not null && depth < 32; depth++)
            {
                if (Resolve(current.GetValueOrDefault("Resources")) is PdfDictionary resources)
                    return resources;
                current = Resolve(current.GetValueOrDefault("Parent")) as PdfDictionary;
            }
            return null;
        }

        public byte[] PageContent(PdfDictionary page)
        {
            object? contents = Resolve(page.GetValueOrDefault("Contents"));
            var parts = new List<byte[]>();
            if (contents is PdfDictionary single)
            {
                if (StreamData(single) is { } data)
                    parts.Add(data);
            }
            else if (contents is List<object?> list)
            {
                foreach (object? item in list)
                {
                    if (Resolve(item) is PdfDictionary stream && StreamData(stream) is { } data)
                        parts.Add(data);
                }
            }
            if (parts.Count == 1)
                return parts[0];
            using var output = new MemoryStream();
            foreach (byte[] part in parts)
            {
                output.Write(part);
                output.WriteByte((byte)'\n');
            }
            return output.ToArray();
        }

        public FontDecoder? Font(PdfDictionary? resources, string name)
        {
            if (resources is null || Resolve(resources.GetValueOrDefault("Font")) is not PdfDictionary fonts ||
                Resolve(fonts.GetValueOrDefault(name)) is not PdfDictionary font)
                return null;
            if (!_fonts.TryGetValue(font, out FontDecoder? decoder))
            {
                decoder = FontDecoder.Create(this, font);
                _fonts[font] = decoder;
            }
            return decoder;
        }

        public PdfDictionary? FormXObject(PdfDictionary? resources, string name)
        {
            if (resources is null || Resolve(resources.GetValueOrDefault("XObject")) is not PdfDictionary objects ||
                Resolve(objects.GetValueOrDefault(name)) is not PdfDictionary form || NameOf(form, "Subtype") != "Form")
                return null;
            return form;
        }
    }

    // ───────────────────────── fonts ─────────────────────────

    private sealed class FontDecoder
    {
        private Dictionary<ulong, string>? _map;
        private List<(int Length, uint Low, uint High)> _codespace = [];
        private bool _twoByte;
        private Encoding? _encoding;

        public static FontDecoder Create(PdfDocument document, PdfDictionary font)
        {
            var decoder = new FontDecoder { _twoByte = PdfDocument.NameOf(font, "Subtype") == "Type0" };
            if (document.Resolve(font.GetValueOrDefault("ToUnicode")) is PdfDictionary toUnicode &&
                document.StreamData(toUnicode) is { } cmap)
                decoder.ParseCMap(cmap);

            if (decoder._map is null || decoder._map.Count == 0)
            {
                decoder._map = null;
                string? encoding = document.Resolve(font.GetValueOrDefault("Encoding")) is PdfName name ? name.Value : null;
                if (encoding is not null && decoder._twoByte)
                {
                    if (encoding.Contains("UCS2", StringComparison.Ordinal) || encoding.Contains("UTF16", StringComparison.Ordinal))
                        decoder._encoding = Encoding.BigEndianUnicode;
                    else if (encoding.StartsWith("KSC", StringComparison.Ordinal))
                        decoder._encoding = Encoding.GetEncoding(949);
                }
            }
            return decoder;
        }

        public string Decode(byte[] bytes)
        {
            if (bytes.Length == 0)
                return string.Empty;
            if (_map is null)
            {
                if (_encoding is not null)
                    return _encoding.GetString(bytes);
                return _twoByte ? string.Empty : Encoding.Latin1.GetString(bytes);
            }

            var text = new StringBuilder(bytes.Length);
            int position = 0;
            while (position < bytes.Length)
            {
                bool consumed = false;
                for (int length = 1; length <= 4 && position + length <= bytes.Length; length++)
                {
                    uint code = 0;
                    for (int index = 0; index < length; index++)
                        code = code << 8 | bytes[position + index];
                    bool inCodespace = _codespace.Count == 0
                        ? length == (_twoByte ? 2 : 1)
                        : _codespace.Any(range => range.Length == length && code >= range.Low && code <= range.High);
                    if (!inCodespace)
                        continue;
                    if (_map.TryGetValue(Key(length, code), out string? mapped))
                        text.Append(mapped);
                    else if (length == 1 && code >= 32)
                        text.Append((char)code);
                    position += length;
                    consumed = true;
                    break;
                }
                if (!consumed)
                    position += _twoByte ? 2 : 1;
            }
            return text.ToString();
        }

        private static ulong Key(int length, uint code) => (ulong)length << 32 | code;

        private void ParseCMap(byte[] cmap)
        {
            _map = new Dictionary<ulong, string>();
            var lexer = new PdfLexer(cmap, 0, cmap.Length);
            var pending = new List<object?>();
            while (true)
            {
                object? token = lexer.ReadObject();
                if (token is EndOfData)
                    break;
                if (token is not PdfOperator op)
                {
                    pending.Add(token);
                    if (pending.Count > 4096)
                        pending.RemoveRange(0, 2048);
                    continue;
                }
                switch (op.Value)
                {
                    case "endcodespacerange":
                        for (int index = 0; index + 1 < pending.Count; index += 2)
                        {
                            if (pending[index] is PdfString low && pending[index + 1] is PdfString high && low.Bytes.Length is >= 1 and <= 4)
                                _codespace.Add((low.Bytes.Length, ToCode(low.Bytes), ToCode(high.Bytes)));
                        }
                        break;
                    case "endbfchar":
                        for (int index = 0; index + 1 < pending.Count; index += 2)
                        {
                            if (pending[index] is PdfString source && source.Bytes.Length is >= 1 and <= 4 && pending[index + 1] is PdfString target)
                                _map[Key(source.Bytes.Length, ToCode(source.Bytes))] = Utf16(target.Bytes);
                        }
                        break;
                    case "endbfrange":
                        for (int index = 0; index + 2 < pending.Count; index += 3)
                        {
                            if (pending[index] is not PdfString low || pending[index + 1] is not PdfString high || low.Bytes.Length is < 1 or > 4)
                                continue;
                            uint start = ToCode(low.Bytes), end = ToCode(high.Bytes);
                            if (end < start || end - start > 65535)
                                continue;
                            if (pending[index + 2] is PdfString target && target.Bytes.Length >= 2)
                            {
                                string baseText = Utf16(target.Bytes);
                                for (uint code = start; code <= end; code++)
                                {
                                    char last = (char)(baseText[^1] + (code - start));
                                    _map[Key(low.Bytes.Length, code)] = baseText[..^1] + last;
                                }
                            }
                            else if (pending[index + 2] is List<object?> targets)
                            {
                                for (uint code = start; code <= end && code - start < targets.Count; code++)
                                {
                                    if (targets[(int)(code - start)] is PdfString item)
                                        _map[Key(low.Bytes.Length, code)] = Utf16(item.Bytes);
                                }
                            }
                        }
                        break;
                }
                pending.Clear();
            }
        }

        private static uint ToCode(byte[] bytes)
        {
            uint code = 0;
            foreach (byte value in bytes.AsSpan(0, Math.Min(4, bytes.Length)))
                code = code << 8 | value;
            return code;
        }

        private static string Utf16(byte[] bytes) =>
            bytes.Length >= 2 ? Encoding.BigEndianUnicode.GetString(bytes, 0, bytes.Length & ~1) : Encoding.Latin1.GetString(bytes);
    }

    // ───────────────────────── page contents ─────────────────────────

    private sealed class ContentInterpreter(PdfDocument document, TextOutput output, CancellationToken cancellationToken)
    {
        public void Run(byte[] content, PdfDictionary? resources, int depth)
        {
            var lexer = new PdfLexer(content, 0, content.Length);
            var operands = new List<object?>();
            FontDecoder? font = null;
            double? lastLineY = null;
            int tokens = 0;
            while (true)
            {
                if ((++tokens & 0xFFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                object? token = lexer.ReadObject();
                if (token is EndOfData)
                    return;
                if (token is not PdfOperator op)
                {
                    operands.Add(token);
                    if (operands.Count > 64)
                        operands.RemoveAt(0);
                    continue;
                }

                switch (op.Value)
                {
                    case "Tf":
                        if (operands.Count >= 1 && operands[0] is PdfName fontName)
                            font = document.Font(resources, fontName.Value);
                        break;
                    case "Tj":
                        if (operands.Count >= 1 && operands[^1] is PdfString shown)
                            output.Append(Decode(font, shown));
                        break;
                    case "'":
                    case "\"":
                        output.Space();
                        if (operands.Count >= 1 && operands[^1] is PdfString quoted)
                            output.Append(Decode(font, quoted));
                        break;
                    case "TJ":
                        if (operands.Count >= 1 && operands[^1] is List<object?> items)
                        {
                            foreach (object? item in items)
                            {
                                if (item is PdfString part)
                                    output.Append(Decode(font, part));
                                else if (item is double adjustment && adjustment < -180)
                                    output.Space();
                            }
                        }
                        break;
                    case "Td":
                    case "TD":
                        if (operands.Count >= 2 && operands[^1] is double moveY && Math.Abs(moveY) > 0.01)
                            output.Space();
                        break;
                    case "T*":
                    case "ET":
                        output.Space();
                        break;
                    case "Tm":
                        if (operands.Count >= 6 && operands[^1] is double lineY)
                        {
                            if (lastLineY is not { } previous || Math.Abs(previous - lineY) > 0.01)
                                output.Space();
                            lastLineY = lineY;
                        }
                        break;
                    case "Do":
                        if (depth < 8 && operands.Count >= 1 && operands[^1] is PdfName formName &&
                            document.FormXObject(resources, formName.Value) is { } form &&
                            document.StreamData(form) is { } formContent)
                        {
                            PdfDictionary? formResources = document.Resolve(form.GetValueOrDefault("Resources")) as PdfDictionary ?? resources;
                            Run(formContent, formResources, depth + 1);
                            output.Space();
                        }
                        break;
                    case "BI":
                        lexer.SkipInlineImage();
                        break;
                }
                operands.Clear();
                if (output.IsDecided)
                    return;
            }
        }

        private static string Decode(FontDecoder? font, PdfString value) =>
            font is null ? Encoding.Latin1.GetString(value.Bytes) : font.Decode(value.Bytes);
    }

    private sealed class TextOutput(ContentMatcher matcher)
    {
        private readonly StringBuilder _text = new(16 * 1024);
        private bool _decided;

        public bool IsDecided => _decided;

        public void Append(string value)
        {
            if (_decided || value.Length == 0)
                return;
            foreach (char character in value)
                _text.Append(character < 32 ? ' ' : character);
            if (_text.Length >= 16 * 1024)
                Flush();
        }

        public void Space()
        {
            if (!_decided && _text.Length > 0 && _text[^1] != ' ')
                _text.Append(' ');
        }

        public bool Boundary()
        {
            Space();
            return Flush();
        }

        public bool Flush()
        {
            if (_decided)
                return true;
            if (_text.Length == 0)
                return false;
            foreach (ReadOnlyMemory<char> chunk in _text.GetChunks())
            {
                if (matcher.Feed(chunk.Span))
                {
                    _decided = true;
                    break;
                }
            }
            _text.Clear();
            return _decided;
        }
    }

    // ───────────────────────── lexer ─────────────────────────

    private sealed class PdfLexer(byte[] data, int position, int end)
    {
        private const int MaximumDepth = 128;
        private int _position = position;

        public static bool IsWhite(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32;

        public static bool IsDelimiter(byte value) =>
            value is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

        public static int HexValue(byte value) => value switch
        {
            >= (byte)'0' and <= (byte)'9' => value - '0',
            >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
            >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
            _ => -1
        };

        private void SkipWhite()
        {
            while (_position < end)
            {
                byte value = data[_position];
                if (IsWhite(value))
                {
                    _position++;
                }
                else if (value == '%')
                {
                    while (_position < end && data[_position] is not ((byte)'\r' or (byte)'\n'))
                        _position++;
                }
                else
                {
                    return;
                }
            }
        }

        public bool TryReadStreamStart(out int start)
        {
            start = -1;
            SkipWhite();
            if (end - _position < 6 || !data.AsSpan(_position, 6).SequenceEqual("stream"u8))
                return false;
            _position += 6;
            if (_position < end && data[_position] == '\r')
                _position++;
            if (_position < end && data[_position] == '\n')
                _position++;
            start = _position;
            return true;
        }

        public void SkipInlineImage()
        {
            // Parameters up to ID, then binary data up to a whitespace-delimited EI.
            for (int guard = 0; guard < 4096; guard++)
            {
                object? token = ReadObject();
                if (token is EndOfData)
                    return;
                if (token is PdfOperator { Value: "ID" })
                    break;
            }
            _position++;
            while (_position + 2 <= end)
            {
                if (data[_position] == 'E' && data[_position + 1] == 'I' &&
                    (_position == 0 || IsWhite(data[_position - 1])) &&
                    (_position + 2 == end || IsWhite(data[_position + 2]) || IsDelimiter(data[_position + 2])))
                {
                    _position += 2;
                    return;
                }
                _position++;
            }
            _position = end;
        }

        public object? ReadObject(int depth = 0)
        {
            if (depth > MaximumDepth)
                throw new InvalidDataException("PDF object nesting is too deep.");
            SkipWhite();
            if (_position >= end)
                return EndOfData.Instance;

            byte value = data[_position];
            switch (value)
            {
                case (byte)'/':
                    return new PdfName(ReadName());
                case (byte)'(':
                    return new PdfString(ReadLiteral());
                case (byte)'<' when _position + 1 < end && data[_position + 1] == '<':
                {
                    _position += 2;
                    var dictionary = new PdfDictionary();
                    while (true)
                    {
                        SkipWhite();
                        if (_position >= end)
                            return dictionary;
                        if (data[_position] == '>' && _position + 1 < end && data[_position + 1] == '>')
                        {
                            _position += 2;
                            return dictionary;
                        }
                        object? key = ReadObject(depth + 1);
                        if (key is EndOfData)
                            return dictionary;
                        if (key is not PdfName name)
                            continue;
                        object? item = ReadObject(depth + 1);
                        if (item is EndOfData)
                            return dictionary;
                        dictionary[name.Value] = item;
                    }
                }
                case (byte)'<':
                    return new PdfString(ReadHex());
                case (byte)'[':
                {
                    _position++;
                    var list = new List<object?>();
                    while (true)
                    {
                        SkipWhite();
                        if (_position >= end)
                            return list;
                        if (data[_position] == ']')
                        {
                            _position++;
                            return list;
                        }
                        object? item = ReadObject(depth + 1);
                        if (item is EndOfData)
                            return list;
                        list.Add(item);
                    }
                }
                case (byte)']' or (byte)'>' or (byte)')' or (byte)'{' or (byte)'}':
                    _position++;
                    return new PdfOperator(((char)value).ToString());
            }

            if (value is (byte)'+' or (byte)'-' or (byte)'.' || char.IsAsciiDigit((char)value))
            {
                double number = ReadNumber(out bool isInteger);
                if (isInteger && number >= 0)
                {
                    int save = _position;
                    SkipWhite();
                    if (_position < end && char.IsAsciiDigit((char)data[_position]))
                    {
                        ReadNumber(out bool generationInteger);
                        SkipWhite();
                        if (generationInteger && _position < end && data[_position] == 'R' &&
                            (_position + 1 == end || IsWhite(data[_position + 1]) || IsDelimiter(data[_position + 1])))
                        {
                            _position++;
                            return new PdfReference((int)Math.Min(number, int.MaxValue));
                        }
                    }
                    _position = save;
                }
                return number;
            }

            int start = _position;
            while (_position < end && !IsWhite(data[_position]) && !IsDelimiter(data[_position]))
                _position++;
            if (_position == start)
            {
                _position++;
                return new PdfOperator(((char)value).ToString());
            }
            string keyword = Encoding.Latin1.GetString(data, start, _position - start);
            return keyword switch
            {
                "true" => true,
                "false" => false,
                "null" => null,
                _ => new PdfOperator(keyword)
            };
        }

        private double ReadNumber(out bool isInteger)
        {
            bool negative = false;
            if (data[_position] is (byte)'+' or (byte)'-')
            {
                negative = data[_position] == '-';
                _position++;
            }
            double result = 0;
            isInteger = true;
            while (_position < end && char.IsAsciiDigit((char)data[_position]))
                result = result * 10 + (data[_position++] - '0');
            if (_position < end && data[_position] == '.')
            {
                isInteger = false;
                _position++;
                double scale = 0.1;
                while (_position < end && char.IsAsciiDigit((char)data[_position]))
                {
                    result += (data[_position++] - '0') * scale;
                    scale *= 0.1;
                }
            }
            return negative ? -result : result;
        }

        private string ReadName()
        {
            _position++;
            var bytes = new List<byte>(16);
            while (_position < end && !IsWhite(data[_position]) && !IsDelimiter(data[_position]))
            {
                byte value = data[_position++];
                if (value == '#' && _position + 1 < end && HexValue(data[_position]) >= 0 && HexValue(data[_position + 1]) >= 0)
                {
                    value = (byte)(HexValue(data[_position]) << 4 | HexValue(data[_position + 1]));
                    _position += 2;
                }
                bytes.Add(value);
            }
            return Encoding.Latin1.GetString(bytes.ToArray());
        }

        private byte[] ReadLiteral()
        {
            _position++;
            var bytes = new List<byte>(32);
            int nesting = 1;
            while (_position < end)
            {
                byte value = data[_position++];
                if (value == '\\' && _position < end)
                {
                    byte escaped = data[_position++];
                    switch (escaped)
                    {
                        case (byte)'n': bytes.Add((byte)'\n'); break;
                        case (byte)'r': bytes.Add((byte)'\r'); break;
                        case (byte)'t': bytes.Add((byte)'\t'); break;
                        case (byte)'b': bytes.Add(8); break;
                        case (byte)'f': bytes.Add(12); break;
                        case (byte)'\r':
                            if (_position < end && data[_position] == '\n')
                                _position++;
                            break;
                        case (byte)'\n':
                            break;
                        case >= (byte)'0' and <= (byte)'7':
                        {
                            int octal = escaped - '0';
                            for (int digit = 0; digit < 2 && _position < end && data[_position] is >= (byte)'0' and <= (byte)'7'; digit++)
                                octal = octal * 8 + (data[_position++] - '0');
                            bytes.Add((byte)octal);
                            break;
                        }
                        default:
                            bytes.Add(escaped);
                            break;
                    }
                    continue;
                }
                if (value == '(')
                {
                    nesting++;
                }
                else if (value == ')' && --nesting == 0)
                {
                    break;
                }
                bytes.Add(value);
            }
            return bytes.ToArray();
        }

        private byte[] ReadHex()
        {
            _position++;
            var bytes = new List<byte>(16);
            int high = -1;
            while (_position < end && data[_position] != '>')
            {
                int digit = HexValue(data[_position++]);
                if (digit < 0)
                    continue;
                if (high < 0) high = digit;
                else { bytes.Add((byte)(high << 4 | digit)); high = -1; }
            }
            if (_position < end)
                _position++;
            if (high >= 0)
                bytes.Add((byte)(high << 4));
            return bytes.ToArray();
        }
    }
}
