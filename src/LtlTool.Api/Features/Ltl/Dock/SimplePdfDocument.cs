using System.Globalization;
using System.Text;

namespace LtlTool.Api.Features.Ltl.Dock;

/// <summary>
/// A tiny, dependency-free PDF writer covering exactly what the dock BOL packet / manifest needs:
/// US-Letter pages, the PDF standard-14 Helvetica / Helvetica-Bold fonts (no embedded font files,
/// so no font licensing to clear), left-aligned headings and body lines, and simple fixed-column
/// tables with per-cell truncation. Auto-paginates when the cursor runs off the bottom margin.
///
/// <para>
/// Chosen over a NuGet PDF library on purpose: QuestPDF's community license is a compliance risk for
/// a commercial product, and this environment has no .NET SDK to verify a fresh package restore. The
/// output is a valid, printable PDF — deliberately plain (no images, no embedded fonts, WinAnsi/ASCII
/// text only). Non-ASCII input is down-converted to an ASCII fallback so the byte stream stays valid.
/// </para>
/// </summary>
public sealed class SimplePdfDocument
{
    // US Letter at 72 dpi.
    private const double PageWidth = 612;
    private const double PageHeight = 792;
    private const double Margin = 54;
    private const double Leading = 4;

    // Helvetica has no metrics table here; approximate every glyph at 0.5em. Good enough for a
    // manifest whose cells are truncated to their column width — never used for justified text.
    private const double AvgCharWidthEm = 0.5;

    private readonly List<string> _pages = [];
    private readonly StringBuilder _current = new();
    private double _cursorY = PageHeight - Margin;
    private bool _hasContent;

    /// <summary>A bold heading line, larger than body text, with a little space above it.</summary>
    public SimplePdfDocument Heading(string text, double fontSize = 15)
    {
        EnsureRoom(fontSize + 6);
        _cursorY -= 6;
        WriteText(text, fontSize, bold: true);
        return this;
    }

    /// <summary>A normal body line. <paramref name="bold"/> for emphasis, <paramref name="muted"/> for the small print.</summary>
    public SimplePdfDocument Line(string text, double fontSize = 10, bool bold = false)
    {
        EnsureRoom(fontSize + Leading);
        WriteText(text, fontSize, bold);
        return this;
    }

    /// <summary>Blank vertical space.</summary>
    public SimplePdfDocument Gap(double points = 8)
    {
        _cursorY -= points;
        return this;
    }

    /// <summary>
    /// A fixed-column table. <paramref name="columnWidths"/> are fractions of the usable line width
    /// (they are normalized, so any positive weights work). Header row is bold. Every cell is
    /// truncated to fit its column so columns never collide.
    /// </summary>
    public SimplePdfDocument Table(
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows,
        IReadOnlyList<double> columnWidths,
        double fontSize = 9)
    {
        var usable = PageWidth - (2 * Margin);
        var totalWeight = columnWidths.Sum();
        var widths = columnWidths.Select(w => usable * (w / totalWeight)).ToArray();

        WriteRow(headers, widths, fontSize, bold: true);
        foreach (var row in rows)
        {
            WriteRow(row, widths, fontSize, bold: false);
        }
        return this;
    }

    private void WriteRow(IReadOnlyList<string> cells, double[] widths, double fontSize, bool bold)
    {
        EnsureRoom(fontSize + Leading);
        var y = _cursorY - fontSize;
        var x = Margin;
        var font = bold ? "F2" : "F1";
        var sb = new StringBuilder();
        sb.Append("BT\n").Append(font).Append(' ').Append(Num(fontSize)).Append(" Tf\n");
        for (var i = 0; i < widths.Length; i++)
        {
            var cell = i < cells.Count ? cells[i] : "";
            var clipped = Clip(cell, widths[i], fontSize);
            sb.Append(Num(x)).Append(' ').Append(Num(y)).Append(" Td (")
              .Append(Escape(clipped)).Append(") Tj\n");
            // Td is relative; reset by moving back to origin for the next absolute placement.
            sb.Append(Num(-x)).Append(' ').Append(Num(-y)).Append(" Td\n");
            x += widths[i];
        }
        sb.Append("ET\n");
        _current.Append(sb);
        _cursorY = y - Leading;
        _hasContent = true;
    }

    private void WriteText(string text, double fontSize, bool bold)
    {
        var y = _cursorY - fontSize;
        var font = bold ? "F2" : "F1";
        _current.Append("BT\n").Append(font).Append(' ').Append(Num(fontSize)).Append(" Tf\n")
                .Append(Num(Margin)).Append(' ').Append(Num(y)).Append(" Td (")
                .Append(Escape(text)).Append(") Tj\nET\n");
        _cursorY = y - Leading;
        _hasContent = true;
    }

    private void EnsureRoom(double needed)
    {
        if (_cursorY - needed < Margin)
        {
            FlushPage();
        }
    }

    private void FlushPage()
    {
        if (_current.Length > 0)
        {
            _pages.Add(_current.ToString());
            _current.Clear();
        }
        _cursorY = PageHeight - Margin;
    }

    /// <summary>Serializes the accumulated content into a complete PDF byte stream.</summary>
    public byte[] Build()
    {
        if (_hasContent || _current.Length > 0)
        {
            FlushPage();
        }
        if (_pages.Count == 0)
        {
            _pages.Add("");
        }

        // Object layout: 1 Catalog, 2 Pages, 3 F1, 4 F2, then per page a Page obj + a Contents obj.
        var objects = new List<string>();
        var pageObjNumbers = new List<int>();
        var firstPageObj = 5;
        for (var i = 0; i < _pages.Count; i++)
        {
            pageObjNumbers.Add(firstPageObj + (i * 2));
        }

        var kids = string.Join(" ", pageObjNumbers.Select(n => $"{n} 0 R"));
        objects.Add($"<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add($"<< /Type /Pages /Kids [{kids}] /Count {_pages.Count} >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");

        for (var i = 0; i < _pages.Count; i++)
        {
            var pageObj = firstPageObj + (i * 2);
            var contentObj = pageObj + 1;
            objects.Add(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Num(PageWidth)} {Num(PageHeight)}] " +
                $"/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentObj} 0 R >>");
            var bytes = Encoding.ASCII.GetByteCount(_pages[i]);
            objects.Add($"<< /Length {bytes} >>\nstream\n{_pages[i]}\nendstream");
        }

        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append("xref\n0 ").Append(objects.Count + 1).Append('\n');
        pdf.Append("0000000000 65535 f \n");
        for (var i = 1; i <= objects.Count; i++)
        {
            pdf.Append(offsets[i].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }
        pdf.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\n");
        pdf.Append("startxref\n").Append(xrefOffset).Append("\n%%EOF");

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static string Clip(string text, double width, double fontSize)
    {
        var normalized = ToAscii(text);
        var maxChars = Math.Max(1, (int)(width / (fontSize * AvgCharWidthEm)));
        if (normalized.Length <= maxChars)
        {
            return normalized;
        }
        return maxChars <= 1 ? normalized[..1] : normalized[..(maxChars - 1)] + "…";
    }

    private static string Escape(string text)
    {
        var ascii = ToAscii(text);
        return ascii
            .Replace("\\", "\\\\")
            .Replace("(", "\\(")
            .Replace(")", "\\)");
    }

    // WinAnsi/ASCII byte stream only — map the few non-ASCII glyphs this document uses to safe
    // ASCII so the stream stays valid and predictable, and drop anything else.
    private static string ToAscii(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '—': // em dash
                case '–': // en dash
                    sb.Append('-');
                    break;
                case '…': // ellipsis
                    sb.Append("...");
                    break;
                case '‘':
                case '’':
                    sb.Append('\'');
                    break;
                case '“':
                case '”':
                    sb.Append('"');
                    break;
                default:
                    if (ch is '\n' or '\r' or '\t')
                    {
                        sb.Append(' ');
                    }
                    else if (ch is >= ' ' and < (char)127)
                    {
                        sb.Append(ch);
                    }
                    // else: drop non-ASCII (never fabricate a substitute glyph).
                    break;
            }
        }
        return sb.ToString();
    }

    private static string Num(double value) =>
        Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
}
