using SkiaSharp;

namespace Libzora.Layout;

internal sealed partial class HorizontalComposer
{
    /// <summary>A laid-out line of cells.</summary>
    private sealed class LineBox
    {
        public List<LayoutCell> Cells { get; } = new();
        private float _advance;
        public float Used => _advance - (Cells.Count > 0 ? Cells[^1].TrailingLetterSpacing : 0f);
        public float WidthWith(LayoutCell cell) => _advance + cell.Advance - cell.TrailingLetterSpacing;
        public float Ascent { get; private set; }
        public float Descent { get; private set; }
        public float Indent { get; set; }
        public bool EndsWithForcedBreak { get; set; }
        public bool StartsAnchoredPage { get; set; }
        /// <summary>True when the final cell is a closing CJK mark intentionally hanging into the trailing margin.</summary>
        public bool HasHangingPunctuation { get; set; }
        /// <summary>Optical protrusion into the trailing margin (e.g. half a Latin hyphen or period).</summary>
        public float OpticalTrailingProtrusion { get; set; }
        /// <summary>Additional left inset on this line (e.g. beside a floated drop cap).</summary>
        public float FloatLeftInset { get; set; }

        public void Add(LayoutCell cell)
        {
            Cells.Add(cell);
            _advance += cell.Advance;
            Ascent = Math.Max(Ascent, cell.Ascent);
            Descent = Math.Max(Descent, cell.Descent);
        }

        public void RemoveLast()
        {
            if (Cells.Count == 0)
            {
                return;
            }

            var removed = Cells[^1];
            Cells.RemoveAt(Cells.Count - 1);
            _advance -= removed.Advance;
            Ascent = 0f;
            Descent = 0f;
            foreach (var cell in Cells)
            {
                Ascent = Math.Max(Ascent, cell.Ascent);
                Descent = Math.Max(Descent, cell.Descent);
            }
        }

        /// <summary>
        /// Distributes <paramref name="extra"/> pixels. Latin-majority lines
        /// only expand their word spaces: expanding every glyph gap (the old
        /// behavior) pulled English words apart and produced the tell-tale
        /// "loose line" when a long word wrapped. Mobile prose starts with
        /// proportional spaces and fills only as far as the spacing cap permits;
        /// legacy layouts leave a line ragged when full justification is too loose.
        /// CJK and mixed lines keep inter-character justification, where the
        /// per-gap amount is shared by CJK characters and gaps around intact
        /// Latin words, never by the letters inside those words.
        /// </summary>
        public bool TryJustify(float extra, float fontSize, bool naturalLatin = false)
        {
            var end = Cells.Count;
            if (HasHangingPunctuation && end > 1)
            {
                // Align the body up to the trailing margin while letting the
                // hanging punctuation sit outside the margin.
                extra += Cells[end - 1].Advance;
                end--;
            }
            else if (OpticalTrailingProtrusion > 0f)
            {
                // Optical margin alignment: let trailing hyphens/periods peek
                // slightly past the right margin so the text column looks straight.
                extra += OpticalTrailingProtrusion;
            }

            if (extra < -0.01f)
            {
                if (!naturalLatin)
                {
                    return true;
                }

                while (end > 0 && Cells[end - 1].IsSpace)
                {
                    end--;
                }

                var shrinkStart = 0;
                while (shrinkStart < end && Cells[shrinkStart].TextStart < 0 && Cells[shrinkStart].Style.NoWrap) shrinkStart++;
                var minSpaceWidth = fontSize * 0.20f;
                var totalShrinkCap = 0f;
                for (var index = shrinkStart; index < end; index++)
                {
                    var cell = Cells[index];
                    if (cell.IsSpace)
                    {
                        totalShrinkCap += Math.Max(0f, cell.Advance - minSpaceWidth);
                    }
                }

                if (totalShrinkCap <= 0.01f)
                {
                    return true;
                }

                var neededShrink = Math.Min(-extra, totalShrinkCap);
                var shrinkFraction = neededShrink / totalShrinkCap;
                var shrunk = 0f;
                for (var index = shrinkStart; index < end; index++)
                {
                    var cell = Cells[index];
                    if (cell.IsSpace)
                    {
                        var room = Math.Max(0f, cell.Advance - minSpaceWidth);
                        var delta = room * shrinkFraction;
                        Cells[index] = cell with { Advance = cell.Advance - delta };
                        shrunk += delta;
                    }
                }

                _advance -= shrunk;
                return shrunk >= -extra - 0.01f;
            }

            if (extra <= 0.01f)
            {
                return true;
            }

            while (end > 0 && Cells[end - 1].IsSpace)
            {
                end--;
            }

            // The generated marker and its trailing space define a fixed
            // hanging indent. Justify the body, not those synthetic cells.
            var start = 0;
            while (start < end && Cells[start].TextStart < 0 && Cells[start].Style.NoWrap) start++;
            if (end - start <= 1)
            {
                return true;
            }

            var latinGlyphs = 0;
            var inkGlyphs = 0;
            var totalGlyphs = 0;
            for (var index = start; index < end; index++)
            {
                var cell = Cells[index];
                var glyphCount = Math.Max(1, cell.Glyphs.Length);
                totalGlyphs += cell.JustifyAsUnit ? 1 : glyphCount;
                if (cell.IsSpace)
                {
                    continue;
                }

                inkGlyphs += glyphCount;
                if (cell.IsLatinWord)
                {
                    latinGlyphs += glyphCount;
                }
            }

            // Only a line whose visible ink is majority Latin takes the
            // space-only path. A Chinese sentence with an English name or an
            // acronym is still justified between characters, which keeps the
            // right edge flush instead of leaving it conspicuously ragged.
            if (naturalLatin || (latinGlyphs > 0 && latinGlyphs * 2 >= inkGlyphs))
            {
                var spaceCount = 0;
                for (var index = start; index < end; index++)
                {
                    if (Cells[index].IsSpace)
                    {
                        spaceCount++;
                    }
                }

                if (spaceCount == 0
                    || (!naturalLatin && extra / spaceCount > fontSize * 0.2f))
                {
                    return false;
                }

                var perSpace = extra / spaceCount;
                if (naturalLatin)
                {
                    // Expand word spaces up to the safe typographic cap (0.65em).
                    // If a small residual gap remains (e.g. a narrow mobile line
                    // with only 3-5 spaces), absorb it via subtle micro-tracking
                    // (<= 0.045em per letter gap) so the right margin stays flush
                    // without creating rivers or visibly pulling letters apart.
                    var maxSpaceWidth = fontSize * 0.65f;
                    var maxSpaceGain = 0f;
                    for (var index = start; index < end; index++)
                    {
                        var cell = Cells[index];
                        if (cell.IsSpace)
                        {
                            maxSpaceGain += Math.Max(0f, maxSpaceWidth - cell.Advance);
                        }
                    }

                    var unabsorbed = extra - maxSpaceGain;
                    var trackGaps = unabsorbed > 0.01f ? CountLineMicroTrackGaps(start, end) : 0;
                    var maxTrackPerGap = fontSize * 0.045f;
                    if (unabsorbed > 0.01f && trackGaps > 0 && unabsorbed <= trackGaps * maxTrackPerGap + 0.01f)
                    {
                        var spaceAdded = 0f;
                        for (var index = start; index < end; index++)
                        {
                            var cell = Cells[index];
                            if (cell.IsSpace)
                            {
                                var expansion = Math.Max(0f, maxSpaceWidth - cell.Advance);
                                Cells[index] = cell with { Advance = cell.Advance + expansion };
                                spaceAdded += expansion;
                            }
                        }

                        var residual = Math.Max(0f, extra - spaceAdded);
                        var perTrack = residual / trackGaps;
                        var trackAdded = ApplyLineMicroTracking(start, end, perTrack);
                        _advance += spaceAdded + trackAdded;
                        return spaceAdded + trackAdded >= extra - 0.01f;
                    }

                    if (unabsorbed > fontSize * 5.0f)
                    {
                        return false;
                    }

                    var effectiveMaxSpace = unabsorbed > fontSize * 2.5f
                        ? fontSize * 0.55f
                        : maxSpaceWidth;
                    var added = 0f;
                    for (var index = start; index < end; index++)
                    {
                        var cell = Cells[index];
                        if (cell.IsSpace)
                        {
                            var expansion = Math.Min(perSpace, Math.Max(0f, effectiveMaxSpace - cell.Advance));
                            Cells[index] = cell with { Advance = cell.Advance + expansion };
                            added += expansion;
                        }
                    }

                    _advance += added;
                    return added >= extra - 0.01f;
                }

                var legacyAdded = 0f;
                for (var index = start; index < end; index++)
                {
                    var cell = Cells[index];
                    if (cell.IsSpace)
                    {
                        Cells[index] = cell with { Advance = cell.Advance + perSpace };
                        legacyAdded += perSpace;
                    }
                }

                _advance += legacyAdded;
                return legacyAdded >= extra - 0.01f;
            }

            // Never stretch the gap before a footnote marker or closing/sentence
            // punctuation (。，、；：！？）」』》】〕), or after opening punctuation
            // (（「『《【〔), so marks stay attached to their host characters.
            var gaps = 0;
            for (var index = start; index < end; index++)
            {
                var cell = Cells[index];
                var glyphCount = Math.Max(1, cell.Glyphs.Length);
                if (!cell.JustifyAsUnit && glyphCount > 1)
                {
                    gaps += glyphCount - 1;
                }
                if (index + 1 < end && CanExpandGapBetween(cell, Cells[index + 1]))
                {
                    gaps++;
                }
            }
            if (gaps <= 0)
            {
                return true;
            }

            var perGap = extra / gaps;
            if (perGap > fontSize * 0.55f)
            {
                return false;
            }

            for (var index = start; index < end; index++)
            {
                var cell = Cells[index];
                var shiftedX = new float[cell.GlyphX.Length];
                if (!cell.JustifyAsUnit)
                {
                    for (var i = 0; i < cell.GlyphX.Length; i++)
                    {
                        // Gaps inside a shaped cell belong in its local glyph
                        // positions. The gap after a cell belongs in that
                        // cell's advance, so PlaceLine does not count the same
                        // distance twice when it advances to the next cell.
                        shiftedX[i] = cell.GlyphX[i] + perGap * i;
                    }
                }
                else
                {
                    // Markers and ruby bases stay intact. Justification adds
                    // space after the unit, not between its own glyphs.
                    Array.Copy(cell.GlyphX, shiftedX, cell.GlyphX.Length);
                }

                var glyphCount = Math.Max(1, cell.Glyphs.Length);
                var gapsAfter = cell.JustifyAsUnit ? 0 : glyphCount - 1;
                if (index + 1 < end && CanExpandGapBetween(cell, Cells[index + 1])) gapsAfter++;
                Cells[index] = cell with
                {
                    GlyphX = shiftedX,
                    Advance = cell.Advance + perGap * gapsAfter,
                };
            }

            _advance += extra;
            return true;
        }

        internal static int CountMicroTrackGaps(LayoutCell cell)
        {
            if (cell.IsSpace || cell.FootnoteMarker || cell.RubyAnnotation is not null || cell.ImagePath is not null)
            {
                return 0;
            }

            var glyphCount = cell.GlyphX.Length;
            if (glyphCount <= 1)
            {
                return 0;
            }

            var gaps = 0;
            var hasClusters = cell.Clusters.Length == glyphCount;
            for (var i = 1; i < glyphCount; i++)
            {
                if (!hasClusters || cell.Clusters[i] != cell.Clusters[i - 1])
                {
                    gaps++;
                }
            }

            return gaps;
        }

        private int CountLineMicroTrackGaps(int start, int end)
        {
            var gaps = 0;
            for (var index = start; index < end; index++)
            {
                var cell = Cells[index];
                gaps += CountMicroTrackGaps(cell);
                if (index + 1 < end
                    && !cell.IsSpace
                    && !Cells[index + 1].IsSpace
                    && CanExpandGapBetween(cell, Cells[index + 1]))
                {
                    gaps++;
                }
            }

            return gaps;
        }

        private float ApplyLineMicroTracking(int start, int end, float perGap)
        {
            var added = 0f;
            for (var index = start; index < end; index++)
            {
                var cell = Cells[index];
                var internalGaps = CountMicroTrackGaps(cell);
                var afterGap = index + 1 < end
                    && !cell.IsSpace
                    && !Cells[index + 1].IsSpace
                    && CanExpandGapBetween(cell, Cells[index + 1])
                    ? 1
                    : 0;
                if (internalGaps == 0 && afterGap == 0)
                {
                    continue;
                }

                var glyphCount = cell.GlyphX.Length;
                var shiftedX = cell.GlyphX;
                if (internalGaps > 0)
                {
                    shiftedX = new float[glyphCount];
                    var hasClusters = cell.Clusters.Length == glyphCount;
                    var cumulative = 0;
                    shiftedX[0] = cell.GlyphX[0];
                    for (var i = 1; i < glyphCount; i++)
                    {
                        if (!hasClusters || cell.Clusters[i] != cell.Clusters[i - 1])
                        {
                            cumulative++;
                        }
                        shiftedX[i] = cell.GlyphX[i] + perGap * cumulative;
                    }
                }

                var delta = perGap * (internalGaps + afterGap);
                Cells[index] = cell with
                {
                    GlyphX = shiftedX,
                    Advance = cell.Advance + delta,
                };
                added += delta;
            }

            return added;
        }

        private static bool CanExpandGapBetween(LayoutCell left, LayoutCell right) =>
            !KeepCellsTogether(left, right) && !right.FootnoteMarker
            && !TypesetText.IsProhibitedAtLineStart(right.Text)
            && !TypesetText.IsOpeningPunctuation(left.Text);
    }

    /// <summary>Accumulates consecutive compatible cells into one placed run.</summary>
    private sealed class RunAccumulator
    {
        private readonly float _baseline;
        private readonly List<ushort> _glyphs = new();
        private readonly List<float> _x = new();
        private readonly List<float> _y = new();
        private readonly List<float> _inkShiftX = new();
        private readonly List<int> _clusters = new();
        private LayoutCell? _first;
        private LayoutCell? _last;
        private int _cellClusterBase;
        private float _flowAdvance;
        private float _leadingInset;
        private bool _syntheticHyphen;

        public RunAccumulator(float baseline)
        {
            _baseline = baseline;
        }

        public bool CanAppend(LayoutCell cell, float baseline)
        {
            return _first is not null
                && _last is not null
                && cell.FontPath == _first.FontPath
                && Math.Abs(cell.FontSize - _first.FontSize) < 0.01f
                && cell.Style.Equals(_last.Style)
                && Math.Abs(baseline - _baseline) < 0.01f
                && cell.TextStart >= 0
                && _last.TextStart >= 0
                && cell.TextStart == _last.TextStart + _last.TextLength;
        }

        public void BeginCell(LayoutCell cell, float leadingInset)
        {
            if (_first is null) _leadingInset = leadingInset;
            _first ??= cell;
            _last = cell;
            _syntheticHyphen |= cell.SyntheticHyphen;
            _cellClusterBase = _first.TextStart >= 0 && cell.TextStart >= 0
                ? cell.TextStart - _first.TextStart
                : 0;
            _flowAdvance += cell.Advance;
        }

        public void AddGlyph(ushort glyph, float x, float y, int cluster, float inkShiftX = 0f)
        {
            _glyphs.Add(glyph);
            _x.Add(x);
            _y.Add(y);
            _inkShiftX.Add(inkShiftX);
            _clusters.Add(_cellClusterBase + cluster);
        }

        public void Flush(PageBuilder pages)
        {
            if (_first is null || _glyphs.Count == 0)
            {
                Reset();
                return;
            }

            var originX = _x[0];
            var glyphs = _glyphs.ToArray();
            var x = new float[_x.Count];
            var y = new float[_y.Count];
            for (var i = 0; i < _x.Count; i++)
            {
                x[i] = _x[i] - originX;
                // Cell glyph offsets are already relative to the baseline.
                // Subtracting the absolute line baseline here moves every
                // horizontal glyph far above the page.
                y[i] = _y[i];
            }

            var textStart = _first.TextStart;
            var textLength = _first.TextStart >= 0 && _last is not null && _last.TextStart >= 0
                ? Math.Max(0, _last.TextStart + _last.TextLength - textStart)
                : 0;

            pages.AddRun(new PlacedRun
            {
                FontPath = _first.FontPath,
                FontSize = _first.FontSize,
                Glyphs = glyphs,
                X = x,
                Y = y,
                InkShiftX = _inkShiftX.Any(shift => shift != 0f) ? _inkShiftX.ToArray() : null,
                OriginX = originX,
                OriginY = _baseline,
                Ascent = _first.Ascent,
                Descent = _first.Descent,
                FlowAdvance = _flowAdvance - _leadingInset,
                TextStart = textStart,
                TextLength = textLength,
                Clusters = _clusters.ToArray(),
                SyntheticHyphen = _syntheticHyphen,
                Style = _first.Style,
            });

            Reset();
        }

        private void Reset()
        {
            _first = null;
            _last = null;
            _flowAdvance = 0f;
            _leadingInset = 0f;
            _syntheticHyphen = false;
            _glyphs.Clear();
            _x.Clear();
            _y.Clear();
            _inkShiftX.Clear();
            _clusters.Clear();
        }
    }
}
