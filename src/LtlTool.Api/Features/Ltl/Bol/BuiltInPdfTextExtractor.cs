using System.IO.Compression;
using System.Text;

namespace LtlTool.Api.Features.Ltl.Bol;

/// <summary>
/// Dependency-free <see cref="IPdfTextExtractor"/> — the always-registered default. It reads a PDF's
/// embedded text layer using only the BCL (<see cref="ZLibStream"/> for <c>FlateDecode</c> content
/// streams plus a small content-stream tokenizer for text-showing operators). No third-party PDF
/// library, so there is no license question (see the PR body) and no NuGet-restore risk in CI / a
/// fresh clone / the offline demo.
///
/// <para><b>Deliberately conservative.</b> It extracts literal <c>( )</c> and hex <c>&lt; &gt;</c>
/// show-strings from <c>Tj</c>/<c>TJ</c>/<c>'</c>/<c>"</c> operators and inserts line breaks on text
/// newline operators so the output reads as lines. It does NOT rasterize, OCR, or resolve CID-font
/// glyph maps: a scanned/image-only BOL yields no text and the read fails closed rather than
/// guessing. That trade is intentional — a wrong number on a BOL is worse than an honest "couldn't
/// read this document".</para>
/// </summary>
public sealed class BuiltInPdfTextExtractor : IPdfTextExtractor
{
    public string Name => "builtin-pdf-textlayer";

    // Latin-1 preserves every byte 1:1 as a char, so byte offsets and char offsets stay aligned
    // while we scan the raw PDF for stream boundaries.
    private static readonly Encoding Latin1 = Encoding.Latin1;

    public Task<string?> ExtractTextAsync(byte[] content, string? contentType, CancellationToken ct = default)
    {
        if (content is null || content.Length == 0)
            return Task.FromResult<string?>(null);

        // A PDF starts with "%PDF-". If it doesn't, this isn't a text-layer PDF we can read; fail
        // closed rather than emitting garbage from a JPEG/PNG/other content type.
        if (!StartsWithPdfHeader(content))
            return Task.FromResult<string?>(null);

        try
        {
            var raw = Latin1.GetString(content);
            var sb = new StringBuilder();

            foreach (var streamBytes in EnumerateContentStreams(raw, content, ct))
            {
                var decoded = TryInflate(streamBytes) ?? streamBytes;
                AppendShownText(Latin1.GetString(decoded), sb);
            }

            var text = sb.ToString().Trim();
            return Task.FromResult<string?>(text.Length == 0 ? null : text);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or IndexOutOfRangeException or ArgumentException)
        {
            throw new PdfTextExtractionException(
                $"The document could not be parsed as a text-layer PDF ({ex.GetType().Name}).", ex);
        }
    }

    private static bool StartsWithPdfHeader(byte[] content)
    {
        // "%PDF" — allow a small leading BOM/whitespace offset some producers emit.
        for (var i = 0; i < Math.Min(content.Length - 4, 8); i++)
            if (content[i] == '%' && content[i + 1] == 'P' && content[i + 2] == 'D' && content[i + 3] == 'F')
                return true;
        return false;
    }

    // Yields the raw bytes between each "stream"/"endstream" keyword pair. We slice from the original
    // byte[] (not the Latin-1 string) so binary stream contents are byte-exact for inflation.
    private static IEnumerable<byte[]> EnumerateContentStreams(string raw, byte[] content, CancellationToken ct)
    {
        var searchFrom = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var streamKw = raw.IndexOf("stream", searchFrom, StringComparison.Ordinal);
            if (streamKw < 0) yield break;

            // Content begins after the "stream" keyword and its EOL (CRLF or LF per the PDF spec).
            var dataStart = streamKw + "stream".Length;
            if (dataStart < raw.Length && raw[dataStart] == '\r') dataStart++;
            if (dataStart < raw.Length && raw[dataStart] == '\n') dataStart++;

            var endKw = raw.IndexOf("endstream", dataStart, StringComparison.Ordinal);
            if (endKw < 0) yield break;

            var length = endKw - dataStart;
            // Trim a single trailing EOL that precedes "endstream".
            if (length > 0 && raw[dataStart + length - 1] == '\n') length--;
            if (length > 0 && raw[dataStart + length - 1] == '\r') length--;

            if (length > 0)
            {
                var slice = new byte[length];
                Array.Copy(content, dataStart, slice, 0, length);
                yield return slice;
            }

            searchFrom = endKw + "endstream".Length;
        }
    }

    // FlateDecode streams are zlib-wrapped. Try zlib first, then raw deflate (some producers omit the
    // 2-byte header). Returns null when the bytes are not inflatable (e.g. an uncompressed stream).
    private static byte[]? TryInflate(byte[] data)
    {
        foreach (var skip in stackalloc[] { 0, 2 })
        {
            if (data.Length <= skip) continue;
            try
            {
                using var input = new MemoryStream(data, skip, data.Length - skip);
                using var deflate = skip == 0
                    ? (Stream)new ZLibStream(input, CompressionMode.Decompress)
                    : new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                deflate.CopyTo(output);
                if (output.Length > 0) return output.ToArray();
            }
            catch (InvalidDataException)
            {
                // Not this framing; try the next.
            }
        }
        return null;
    }

    // Pulls text-showing operands out of a decoded content stream. Handles literal "( )" strings
    // (with PDF backslash escapes) and hex "< >" strings, and breaks lines on the text newline
    // operators so the result reads as lines rather than one run-on span.
    private static void AppendShownText(string content, StringBuilder sb)
    {
        var i = 0;
        while (i < content.Length)
        {
            var c = content[i];
            if (c == '(')
            {
                i = ReadLiteralString(content, i + 1, sb);
                sb.Append(' ');
            }
            else if (c == '<' && i + 1 < content.Length && content[i + 1] != '<')
            {
                i = ReadHexString(content, i + 1, sb);
                sb.Append(' ');
            }
            else if (c == 'T' && i + 1 < content.Length && (content[i + 1] == '*'))
            {
                sb.Append('\n');
                i += 2;
            }
            else if ((c == '\'' || c == '"') && IsOperatorBoundary(content, i))
            {
                sb.Append('\n');
                i++;
            }
            else
            {
                i++;
            }
        }
    }

    private static bool IsOperatorBoundary(string s, int i) =>
        (i == 0 || char.IsWhiteSpace(s[i - 1]) || s[i - 1] == ')') &&
        (i + 1 >= s.Length || char.IsWhiteSpace(s[i + 1]));

    // Reads a literal PDF string starting after the '('; returns the index just past the closing ')'.
    private static int ReadLiteralString(string s, int i, StringBuilder sb)
    {
        var depth = 1;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                var n = s[i + 1];
                switch (n)
                {
                    case 'n': sb.Append('\n'); i += 2; continue;
                    case 'r': sb.Append('\r'); i += 2; continue;
                    case 't': sb.Append('\t'); i += 2; continue;
                    case 'b': case 'f': i += 2; continue;
                    case '(': sb.Append('('); i += 2; continue;
                    case ')': sb.Append(')'); i += 2; continue;
                    case '\\': sb.Append('\\'); i += 2; continue;
                    default:
                        // Octal escape \ddd, or an escaped EOL (line continuation).
                        if (n is >= '0' and <= '7')
                        {
                            var j = i + 1;
                            var oct = 0; var count = 0;
                            while (j < s.Length && count < 3 && s[j] is >= '0' and <= '7')
                            { oct = oct * 8 + (s[j] - '0'); j++; count++; }
                            sb.Append((char)oct);
                            i = j; continue;
                        }
                        i += 2; continue;
                }
            }
            if (c == '(') { depth++; sb.Append(c); i++; continue; }
            if (c == ')')
            {
                depth--;
                if (depth == 0) return i + 1;
                sb.Append(c); i++; continue;
            }
            sb.Append(c);
            i++;
        }
        return i;
    }

    // Reads a hex string starting after the '<'; returns the index just past the closing '>'.
    private static int ReadHexString(string s, int i, StringBuilder sb)
    {
        var hex = new StringBuilder();
        while (i < s.Length && s[i] != '>')
        {
            if (Uri.IsHexDigit(s[i])) hex.Append(s[i]);
            i++;
        }
        if (hex.Length % 2 == 1) hex.Append('0'); // pad per spec
        for (var k = 0; k + 1 < hex.Length; k += 2)
        {
            var b = Convert.ToInt32(hex.ToString(k, 2), 16);
            if (b != 0) sb.Append((char)b);
        }
        return i < s.Length ? i + 1 : i;
    }
}
