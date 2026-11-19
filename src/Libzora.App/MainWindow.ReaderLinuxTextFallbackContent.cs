using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using System.Xml;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora;

/// <summary>
/// Linux plain-text fallback content: pagination, blocks, images,
/// annotations and selection synchronisation.
/// </summary>
public partial class MainWindow
{
    private static List<string> PaginateReaderPlainText(
        string text,
        double lineUnits,
        int linesPerPage)
    {
        var pages = new List<string>();
        var page = new StringBuilder();
        var line = new StringBuilder();
        var lines = 0;

        void CommitLine()
        {
            if (page.Length > 0) page.AppendLine();
            page.Append(line);
            line.Clear();
            lines++;
            if (lines < linesPerPage) return;
            pages.Add(page.ToString().TrimEnd());
            page.Clear();
            lines = 0;
        }

        void CommitBlankLine()
        {
            if (line.Length > 0) CommitLine();
            if (page.Length > 0 && lines + 1 < linesPerPage)
            {
                page.AppendLine();
                lines++;
            }
        }

        var paragraphs = System.Text.RegularExpressions.Regex.Split(
            text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim(),
            @"\n{2,}");
        foreach (var paragraph in paragraphs)
        {
            var linesInParagraph = paragraph.Split('\n');
            if (linesInParagraph.All(lineText => string.IsNullOrWhiteSpace(lineText)))
            {
                CommitBlankLine();
                continue;
            }

            foreach (var paragraphLine in linesInParagraph)
            {
                var remaining = paragraphLine.Trim();
                if (remaining.Length == 0)
                {
                    CommitBlankLine();
                    continue;
                }

                var currentUnits = 0d;
                for (var index = 0; index < remaining.Length; index++)
                {
                    var ch = remaining[index];
                    var unit = GetReaderPlainTextCharUnits(ch);
                    if (line.Length > 0 && currentUnits + unit > lineUnits)
                    {
                        CommitLine();
                        currentUnits = 0;
                    }
                    line.Append(ch);
                    currentUnits += unit;
                }
                CommitLine();
            }
            CommitBlankLine();
        }

        if (line.Length > 0) CommitLine();
        if (page.Length > 0) pages.Add(page.ToString().TrimEnd());
        if (pages.Count == 0) pages.Add(string.Empty);
        return pages;
    }

    private static List<string> PaginateReaderPlainTextWithMeasuredLayout(
        string text,
        double pageWidth,
        int linesPerPage,
        double fontSize,
        double lineHeight,
        FontFamily fontFamily)
    {
        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (normalized.Length == 0)
            return [string.Empty];

        try
        {
            var layout = new TextLayout(
                normalized,
                new Typeface(fontFamily, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal),
                Math.Max(1, fontSize),
                Brushes.Transparent,
                TextAlignment.Left,
                TextWrapping.Wrap,
                TextTrimming.None,
                null,
                FlowDirection.LeftToRight,
                Math.Max(1, pageWidth),
                double.PositiveInfinity,
                Math.Max(1, lineHeight),
                0,
                0,
                null,
                null);
            var lines = layout.TextLines
                .Select(line =>
                {
                    var start = Math.Clamp(line.FirstTextSourceIndex, 0, normalized.Length);
                    var end = Math.Clamp(start + line.Length, start, normalized.Length);
                    return (Start: start, End: end);
                })
                .Where(line => line.End > line.Start)
                .ToArray();
            if (lines.Length == 0)
                return [string.Empty];

            var pages = new List<string>();
            var safeLinesPerPage = Math.Max(1, linesPerPage);
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex += safeLinesPerPage)
            {
                var start = lines[lineIndex].Start;
                var nextLineIndex = Math.Min(lineIndex + safeLinesPerPage, lines.Length);
                var end = nextLineIndex < lines.Length
                    ? lines[nextLineIndex].Start
                    : normalized.Length;
                end = Math.Clamp(end, start, normalized.Length);
                var page = normalized[start..end].TrimEnd();
                if (page.Length > 0)
                    pages.Add(page);
            }

            return pages.Count > 0 ? pages : [string.Empty];
        }
        catch
        {
            // TextLayout is unavailable before an Avalonia text backend is
            // initialized (for example during a headless page estimate). The
            // deterministic unit fallback still keeps every character and
            // is used only for that exceptional path.
            return PaginateReaderPlainText(
                normalized,
                Math.Max(8, pageWidth / Math.Max(1, fontSize)),
                linesPerPage);
        }
    }
}
