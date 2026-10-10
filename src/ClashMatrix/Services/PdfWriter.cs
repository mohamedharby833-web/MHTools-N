using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ClashMatrix.Services
{
    /// <summary>
    /// A minimal PDF 1.4 writer - text in the standard Helvetica fonts, filled boxes,
    /// lines and JPEG images - with no third-party dependencies. Coordinates are in
    /// points (1/72 in) from the TOP-left of the page, as the report lays itself out.
    /// Text outside Windows-1252 (Arabic, for instance) prints as '?': the standard
    /// fonts have no such glyphs.
    /// </summary>
    public sealed class PdfWriter
    {
        /// <summary>A4 portrait.</summary>
        public const double PageWidth = 595.28, PageHeight = 841.89;

        private static readonly Encoding WinAnsi = Encoding.GetEncoding(1252,
            new EncoderReplacementFallback("?"), new DecoderReplacementFallback("?"));

        private readonly List<StringBuilder> _pages = new List<StringBuilder>();
        private readonly List<byte[]> _images = new List<byte[]>();
        private readonly List<int[]> _imageSizes = new List<int[]>();
        private StringBuilder _page;

        public int PageCount { get { return _pages.Count; } }

        public void NewPage()
        {
            _page = new StringBuilder();
            _pages.Add(_page);
        }

        /// <summary>Switches back to an earlier page (1-based), e.g. to stamp "page n of N".</summary>
        public void SelectPage(int number)
        {
            _page = _pages[number - 1];
        }

        // ------------------------------------------------------------- drawing

        public void FillRect(double x, double y, double w, double h, string rgb)
        {
            _page.Append(Rgb(rgb)).Append(" rg ")
                 .Append(N(x)).Append(' ').Append(N(PageHeight - y - h)).Append(' ')
                 .Append(N(w)).Append(' ').Append(N(h)).Append(" re f\n");
        }

        public void StrokeRect(double x, double y, double w, double h, string rgb, double width = 0.5)
        {
            _page.Append(Rgb(rgb)).Append(" RG ").Append(N(width)).Append(" w ")
                 .Append(N(x)).Append(' ').Append(N(PageHeight - y - h)).Append(' ')
                 .Append(N(w)).Append(' ').Append(N(h)).Append(" re S\n");
        }

        public void Line(double x1, double y1, double x2, double y2, string rgb, double width = 0.5)
        {
            _page.Append(Rgb(rgb)).Append(" RG ").Append(N(width)).Append(" w ")
                 .Append(N(x1)).Append(' ').Append(N(PageHeight - y1)).Append(" m ")
                 .Append(N(x2)).Append(' ').Append(N(PageHeight - y2)).Append(" l S\n");
        }

        /// <summary>One line of text; <paramref name="y"/> is its baseline. Cut with "..." to fit <paramref name="maxWidth"/> when given.</summary>
        public void Text(double x, double y, string text, double size, bool bold = false, string rgb = "17202A",
            double maxWidth = 0)
        {
            if (string.IsNullOrEmpty(text))
                return;
            if (maxWidth > 0)
                text = Fit(text, size, bold, maxWidth);

            _page.Append("BT ").Append(Rgb(rgb)).Append(" rg /").Append(bold ? "F2" : "F1").Append(' ')
                 .Append(N(size)).Append(" Tf ").Append(N(x)).Append(' ').Append(N(PageHeight - y))
                 .Append(" Td (").Append(Escape(text)).Append(") Tj ET\n");
        }

        /// <summary>Right-aligned text ending at <paramref name="right"/>.</summary>
        public void TextRight(double right, double y, string text, double size, bool bold = false, string rgb = "17202A")
        {
            Text(right - Width(text, size, bold), y, text, size, bold, rgb);
        }

        /// <summary>Wrapped text in a box; returns the number of lines drawn (at most <paramref name="maxLines"/>).</summary>
        public int TextWrapped(double x, double y, double width, string text, double size, bool bold = false,
            string rgb = "17202A", int maxLines = 99, double leading = 0)
        {
            if (string.IsNullOrEmpty(text))
                return 0;
            if (leading <= 0) leading = size * 1.25;

            List<string> lines = Wrap(text, size, bold, width);
            int count = Math.Min(lines.Count, maxLines);
            for (int i = 0; i < count; i++)
            {
                string line = lines[i];
                if (i == count - 1 && lines.Count > count)
                    line = Fit(line + " ...", size, bold, width);
                Text(x, y + i * leading, line, size, bold, rgb);
            }
            return count;
        }

        /// <summary>A JPEG image fitted (aspect kept) into the box.</summary>
        public void Jpeg(byte[] jpeg, int pixelWidth, int pixelHeight, double x, double y, double maxW, double maxH)
        {
            if (jpeg == null || pixelWidth <= 0 || pixelHeight <= 0)
                return;

            _images.Add(jpeg);
            _imageSizes.Add(new[] { pixelWidth, pixelHeight });
            int index = _images.Count - 1;

            double scale = Math.Min(maxW / pixelWidth, maxH / pixelHeight);
            double w = pixelWidth * scale, h = pixelHeight * scale;
            double ox = x + (maxW - w) / 2, oy = y + (maxH - h) / 2;
            _page.Append("q ").Append(N(w)).Append(" 0 0 ").Append(N(h)).Append(' ')
                 .Append(N(ox)).Append(' ').Append(N(PageHeight - oy - h)).Append(" cm /Im")
                 .Append(index).Append(" Do Q\n");
        }

        // ------------------------------------------------------------- measuring

        public static double Width(string text, double size, bool bold)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int[] widths = bold ? HelveticaBold : Helvetica;
            double units = 0;
            foreach (char c in text)
                units += c >= 32 && c <= 126 ? widths[c - 32] : 556;
            return units * size / 1000.0;
        }

        public static string Fit(string text, double size, bool bold, double maxWidth)
        {
            if (Width(text, size, bold) <= maxWidth) return text;
            const string dots = "...";
            int len = text.Length;
            while (len > 0 && Width(text.Substring(0, len) + dots, size, bold) > maxWidth) len--;
            return len > 0 ? text.Substring(0, len).TrimEnd() + dots : dots;
        }

        public static List<string> Wrap(string text, double size, bool bold, double width)
        {
            var lines = new List<string>();
            foreach (string paragraph in text.Replace("\r", "").Split('\n'))
            {
                string line = "";
                foreach (string word in paragraph.Split(' ').SelectMany(w => Pieces(w, size, bold, width)))
                {
                    string candidate = line.Length == 0 ? word : line + " " + word;
                    if (Width(candidate, size, bold) <= width || line.Length == 0)
                    {
                        line = Width(candidate, size, bold) <= width ? candidate : Fit(candidate, size, bold, width);
                    }
                    else
                    {
                        lines.Add(line);
                        line = Width(word, size, bold) <= width ? word : Fit(word, size, bold, width);
                    }
                }
                lines.Add(line);
            }
            return lines;
        }

        /// <summary>
        /// A word too long for one line, cut into pieces that fit - after a hyphen where
        /// there is one ("PRJ-ZZ-01-1A0-" / "BM-000100.nwc"), else anywhere.
        /// </summary>
        private static IEnumerable<string> Pieces(string word, double size, bool bold, double width)
        {
            while (word.Length > 1 && Width(word, size, bold) > width)
            {
                int fit = 1;
                while (fit < word.Length && Width(word.Substring(0, fit + 1), size, bold) <= width) fit++;
                int hyphen = word.LastIndexOf('-', fit - 1, fit);
                int cut = hyphen > 0 ? hyphen + 1 : fit;
                yield return word.Substring(0, cut);
                word = word.Substring(cut);
            }
            yield return word;
        }

        // ------------------------------------------------------------- output

        public void Save(string path)
        {
            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                Save(file);
        }

        public void Save(Stream output)
        {
            var offsets = new List<long>();
            var body = new MemoryStream();
            Action<string> write = s => { byte[] b = Encoding.ASCII.GetBytes(s); body.Write(b, 0, b.Length); };
            Action<int> begin = n => { offsets.Add(body.Position); write(n + " 0 obj\n"); };

            write("%PDF-1.4\n%âãÏÓ\n");

            // 1 catalog, 2 pages, 3-4 fonts, then images, then page + content pairs.
            int firstImage = 5;
            int firstPage = firstImage + _images.Count;

            begin(1); write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

            var kids = new StringBuilder();
            for (int i = 0; i < _pages.Count; i++) kids.Append(firstPage + i * 2).Append(" 0 R ");
            begin(2); write("<< /Type /Pages /Kids [" + kids + "] /Count " + _pages.Count + " >>\nendobj\n");

            begin(3); write("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
            begin(4); write("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>\nendobj\n");

            for (int i = 0; i < _images.Count; i++)
            {
                begin(firstImage + i);
                write("<< /Type /XObject /Subtype /Image /Width " + _imageSizes[i][0] + " /Height " + _imageSizes[i][1] +
                      " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " + _images[i].Length + " >>\nstream\n");
                body.Write(_images[i], 0, _images[i].Length);
                write("\nendstream\nendobj\n");
            }

            var xobjects = new StringBuilder();
            for (int i = 0; i < _images.Count; i++) xobjects.Append("/Im").Append(i).Append(' ').Append(firstImage + i).Append(" 0 R ");
            string resources = "<< /Font << /F1 3 0 R /F2 4 0 R >>" +
                               (_images.Count > 0 ? " /XObject << " + xobjects + ">>" : "") + " >>";

            for (int i = 0; i < _pages.Count; i++)
            {
                int pageObj = firstPage + i * 2;
                byte[] content = WinAnsi.GetBytes(_pages[i].ToString());

                begin(pageObj);
                write("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + N(PageWidth) + " " + N(PageHeight) + "] /Resources " +
                      resources + " /Contents " + (pageObj + 1) + " 0 R >>\nendobj\n");
                begin(pageObj + 1);
                write("<< /Length " + content.Length + " >>\nstream\n");
                body.Write(content, 0, content.Length);
                write("\nendstream\nendobj\n");
            }

            long xref = body.Position;
            int count = offsets.Count + 1;
            write("xref\n0 " + count + "\n0000000000 65535 f \n");
            foreach (long o in offsets) write(o.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
            write("trailer\n<< /Size " + count + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");

            body.Position = 0;
            body.CopyTo(output);
        }

        // ------------------------------------------------------------- helpers

        private static string N(double v)
        {
            return v.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string Rgb(string hex)
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 8) hex = hex.Substring(2);
            double r = Convert.ToInt32(hex.Substring(0, 2), 16) / 255.0;
            double g = Convert.ToInt32(hex.Substring(2, 2), 16) / 255.0;
            double b = Convert.ToInt32(hex.Substring(4, 2), 16) / 255.0;
            return r.ToString("0.###", CultureInfo.InvariantCulture) + " " +
                   g.ToString("0.###", CultureInfo.InvariantCulture) + " " +
                   b.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string Escape(string text)
        {
            // Through Windows-1252 so the bytes match /WinAnsiEncoding; anything it
            // cannot hold becomes '?'.
            string ansi = WinAnsi.GetString(WinAnsi.GetBytes(text));
            var sb = new StringBuilder(ansi.Length + 8);
            foreach (char c in ansi)
            {
                if (c == '(' || c == ')' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 32) sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // Standard Type 1 metrics (1/1000 em) for characters 32..126.
        private static readonly int[] Helvetica =
        {
            278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
            556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
            1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
            667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
            333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
            556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584
        };

        private static readonly int[] HelveticaBold =
        {
            278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
            556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
            975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778,
            667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,
            333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611,
            611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584
        };
    }
}
