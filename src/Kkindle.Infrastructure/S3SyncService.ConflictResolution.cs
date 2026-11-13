using System.Text.Json;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

public sealed partial class S3SyncService
{
    // Field-level conflict resolution for the two entities whose fields are
    // edited independently in the UI (annotation note/style and book metadata).
    // The previous local snapshot acts as the common ancestor. Every remote
    // variant of the same row is considered together, so a third device cannot
    // lose one remote field by first picking the other remote row by UpdatedAt.

    private readonly record struct RowCandidate<T>(
        T Value,
        string? Version,
        DateTimeOffset Time);

    private static S3SyncAnnotation MergeAnnotationRow(
        S3SyncAnnotation local,
        S3SyncAnnotation? baseline,
        S3SyncAnnotation remote) =>
        MergeAnnotationRows(local, baseline, [remote], null)!;

    private static S3SyncAnnotation? MergeAnnotationRows(
        S3SyncAnnotation? local,
        S3SyncAnnotation? baseline,
        IReadOnlyList<S3SyncAnnotation> remotes,
        S3SyncClock? clock)
    {
        var candidates = new List<RowCandidate<S3SyncAnnotation>>();
        if (local is not null)
            candidates.Add(new(local, local.SyncVersion, local.UpdatedAt));
        foreach (var remote in remotes)
        {
            if (remote is null) continue;
            if (local is not null && ReferenceEquals(remote, local)) continue;
            candidates.Add(new(remote, remote.SyncVersion, remote.UpdatedAt));
        }
        if (candidates.Count == 0) return null;
        if (baseline is null && candidates.Count == 1)
        {
            var single = CloneAnnotation(candidates[0].Value);
            return single;
        }

        var anchorFallback = local ?? candidates[0].Value;
        var baseAnchor = baseline is null
            ? (anchorFallback.ChapterPath, anchorFallback.Fragment, anchorFallback.StartOffset,
                anchorFallback.EndOffset, anchorFallback.SelectedText, anchorFallback.Prefix,
                anchorFallback.Suffix)
            : (baseline.ChapterPath, baseline.Fragment, baseline.StartOffset, baseline.EndOffset,
                baseline.SelectedText, baseline.Prefix, baseline.Suffix);
        var anchor = ChooseFieldN(
            candidates.Select(item => new RowCandidate<(string, string?, int, int, string, string, string)>(
                (item.Value.ChapterPath, item.Value.Fragment, item.Value.StartOffset,
                    item.Value.EndOffset, item.Value.SelectedText, item.Value.Prefix, item.Value.Suffix),
                item.Version, item.Time)),
            baseAnchor, baseline is not null, baseAnchor);

        var baseColor = baseline?.Color ?? anchorFallback.Color;
        var color = ChooseFieldN(
            candidates.Select(item => new RowCandidate<string>(
                item.Value.Color, item.Version, item.Time)),
            baseColor, baseline is not null, baseColor);
        // Color and underline can be changed independently, so they must be
        // merged as separate fields. Grouping them lets a color edit on one
        // device overwrite an underline edit from another device.
        var baseUnderline = baseline?.UnderlineStyle ?? anchorFallback.UnderlineStyle;
        var underline = ChooseFieldN(
            candidates.Select(item => new RowCandidate<string>(
                item.Value.UnderlineStyle, item.Version, item.Time)),
            baseUnderline, baseline is not null, baseUnderline);

        var baseNote = baseline?.Note ?? anchorFallback.Note;
        var note = ChooseFieldN(
            candidates.Select(item => new RowCandidate<string>(
                item.Value.Note, item.Version, item.Time)),
            baseNote, baseline is not null, baseNote);

        var merged = new S3SyncAnnotation
        {
            Id = local?.Id ?? candidates[0].Value.Id,
            BookId = local?.BookId ?? candidates[0].Value.BookId,
            BookFileId = local?.BookFileId ?? candidates[0].Value.BookFileId,
            ChapterPath = anchor.Item1,
            Fragment = anchor.Item2,
            StartOffset = anchor.Item3,
            EndOffset = anchor.Item4,
            SelectedText = anchor.Item5,
            Prefix = anchor.Item6,
            Suffix = anchor.Item7,
            Color = color,
            UnderlineStyle = underline,
            Note = note,
            CreatedAt = candidates.Min(item => item.Value.CreatedAt),
            UpdatedAt = local?.UpdatedAt ?? candidates[0].Value.UpdatedAt
        };
        CopyOpaqueData(merged, local ?? candidates[0].Value);
        foreach (var candidate in candidates
                     .Where(item => !ReferenceEquals(item.Value, local))
                     .OrderBy(item => item, Comparer<RowCandidate<S3SyncAnnotation>>.Create(CompareCandidate)))
            MergeOpaqueFields(merged, candidate.Value, candidate.Value.UpdatedAt);

        AssignMergedAnnotationVersion(merged, local, candidates, clock);
        return merged;
    }

    private static S3SyncBook MergeBookRow(
        S3SyncBook local,
        S3SyncBook? baseline,
        S3SyncBook remote) =>
        MergeBookRows(local, baseline, [remote], null)!;

    private static S3SyncBook? MergeBookRows(
        S3SyncBook? local,
        S3SyncBook? baseline,
        IReadOnlyList<S3SyncBook> remotes,
        S3SyncClock? clock)
    {
        var candidates = new List<RowCandidate<S3SyncBook>>();
        if (local is not null)
            candidates.Add(new(local, local.SyncVersion, local.UpdatedAt));
        foreach (var remote in remotes)
        {
            if (remote is null) continue;
            if (local is not null && ReferenceEquals(remote, local)) continue;
            candidates.Add(new(remote, remote.SyncVersion, remote.UpdatedAt));
        }
        if (candidates.Count == 0) return null;
        if (baseline is null && candidates.Count == 1)
            return CloneBook(candidates[0].Value);

        var fallback = local ?? candidates[0].Value;
        // Every book field is merged independently. Metadata, tags, category,
        // favourite and reading status are edited by different UI actions, so
        // grouping them would let one device's field overwrite another
        // device's concurrent change to a neighbouring field.
        T Choose<T>(Func<S3SyncBook, T> selector)
        {
            var fallbackValue = selector(fallback);
            var baseValue = baseline is null ? fallbackValue : selector(baseline);
            return ChooseFieldN(
                candidates.Select(item => new RowCandidate<T>(
                    selector(item.Value), item.Version, item.Time)),
                baseValue, baseline is not null, fallbackValue);
        }

        var title = Choose(book => book.Title);
        var authors = Choose(book => book.Authors);
        var series = Choose(book => book.Series);
        var seriesIndex = Choose(book => book.SeriesIndex);
        var description = Choose(book => book.Description);
        var publisher = Choose(book => book.Publisher);
        var publishDate = Choose(book => book.PublishDate);
        var isbn = Choose(book => book.Isbn);
        var pageCount = Choose(book => book.PageCount);
        var binding = Choose(book => book.Binding);
        var onlineRating = Choose(book => book.OnlineRating);
        var onlineRatingCount = Choose(book => book.OnlineRatingCount);
        var tags = Choose(book => book.Tags);
        var category = Choose(book => book.Category);
        var isFavorite = Choose(book => book.IsFavorite);
        var readingStatus = Choose(book => book.ReadingStatus);

        // A cover is binary metadata; keep the versioned winner's hash so the
        // existing cover download path can fetch the matching object.
        var cover = candidates
            .Where(item => !string.IsNullOrWhiteSpace(item.Value.CoverHash))
            .OrderBy(item => item, Comparer<RowCandidate<S3SyncBook>>.Create(CompareCandidate))
            .LastOrDefault();
        if (string.IsNullOrWhiteSpace(cover.Value?.CoverHash))
            cover = default;

        var merged = new S3SyncBook
        {
            Id = local?.Id ?? candidates[0].Value.Id,
            Title = title,
            Authors = authors,
            Series = series,
            SeriesIndex = seriesIndex,
            Description = description,
            Publisher = publisher,
            PublishDate = publishDate,
            Isbn = isbn,
            PageCount = pageCount,
            Binding = binding,
            OnlineRating = onlineRating,
            OnlineRatingCount = onlineRatingCount,
            Tags = tags,
            Category = category,
            IsFavorite = isFavorite,
            ReadingStatus = readingStatus,
            CreatedAt = candidates.Min(item => item.Value.CreatedAt),
            UpdatedAt = local?.UpdatedAt ?? candidates[0].Value.UpdatedAt,
            CoverHash = cover.Value?.CoverHash ?? local?.CoverHash ?? fallback.CoverHash,
            CoverFileName = cover.Value?.CoverFileName ?? local?.CoverFileName ?? fallback.CoverFileName,
            LocalCoverPath = local?.LocalCoverPath ?? fallback.LocalCoverPath
        };
        CopyOpaqueData(merged, local ?? candidates[0].Value);
        foreach (var candidate in candidates
                     .Where(item => !ReferenceEquals(item.Value, local))
                     .OrderBy(item => item, Comparer<RowCandidate<S3SyncBook>>.Create(CompareCandidate)))
            MergeOpaqueFields(merged, candidate.Value, candidate.Value.UpdatedAt);

        AssignMergedBookVersion(merged, local, candidates, clock);
        return merged;
    }

    private static T ChooseFieldN<T>(
        IEnumerable<RowCandidate<T>> candidates,
        T baseline,
        bool hasBaseline,
        T fallback)
    {
        var rows = candidates.ToList();
        if (rows.Count == 0) return fallback;
        if (!hasBaseline)
        {
            var best = rows[0];
            foreach (var candidate in rows.Skip(1))
                if (CompareCandidate(candidate, best) > 0) best = candidate;
            return best.Value;
        }

        var chosen = baseline;
        foreach (var candidate in rows.OrderBy(item => item, Comparer<RowCandidate<T>>.Create(CompareCandidate)))
        {
            if (!EqualityComparer<T>.Default.Equals(candidate.Value, baseline))
                chosen = candidate.Value;
        }
        return chosen;
    }

    private static readonly JsonSerializerOptions TieBreakJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        IncludeFields = true
    };

    private static int CompareCandidate<T>(RowCandidate<T> left, RowCandidate<T> right)
    {
        var comparison = S3SyncVersion.Compare(
            left.Version, left.Time, right.Version, right.Time);
        if (comparison != 0) return comparison;
        var versionOrder = string.CompareOrdinal(
            left.Version ?? string.Empty, right.Version ?? string.Empty);
        if (versionOrder != 0) return versionOrder;
        // Exact ties (legacy rows without versions sharing a timestamp) are
        // resolved by serialized content so every device keeps the same field
        // value instead of preferring whichever device happens to be local.
        return string.CompareOrdinal(
            JsonSerializer.Serialize(left.Value, TieBreakJsonOptions),
            JsonSerializer.Serialize(right.Value, TieBreakJsonOptions));
    }

    private static void AssignMergedAnnotationVersion(
        S3SyncAnnotation merged,
        S3SyncAnnotation? local,
        IReadOnlyList<RowCandidate<S3SyncAnnotation>> candidates,
        S3SyncClock? clock)
    {
        if (local is not null && SameAnnotationPayload(merged, local))
        {
            merged.SyncVersion = local.SyncVersion;
            merged.UpdatedAt = local.UpdatedAt;
            return;
        }

        var matching = candidates
            .Where(item => SameAnnotationPayload(merged, item.Value)
                && !string.IsNullOrWhiteSpace(item.Version))
            .OrderBy(item => item, Comparer<RowCandidate<S3SyncAnnotation>>.Create(CompareCandidate))
            .LastOrDefault();
        if (matching.Value is not null)
        {
            merged.SyncVersion = matching.Version;
            merged.UpdatedAt = matching.Time;
            return;
        }

        var latest = candidates.Count == 0
            ? DateTimeOffset.UtcNow
            : candidates.Max(item => item.Time);
        merged.UpdatedAt = latest.AddMilliseconds(1);
        if (clock is not null)
        {
            foreach (var candidate in candidates) clock.ObserveVersion(candidate.Version);
            merged.SyncVersion = clock.Next(latest);
        }
        else
        {
            merged.SyncVersion = candidates
                .OrderBy(item => item, Comparer<RowCandidate<S3SyncAnnotation>>.Create(CompareCandidate))
                .LastOrDefault().Version;
        }
    }

    private static void AssignMergedBookVersion(
        S3SyncBook merged,
        S3SyncBook? local,
        IReadOnlyList<RowCandidate<S3SyncBook>> candidates,
        S3SyncClock? clock)
    {
        if (local is not null && SameBookPayload(merged, local))
        {
            merged.SyncVersion = local.SyncVersion;
            merged.UpdatedAt = local.UpdatedAt;
            return;
        }

        var matching = candidates
            .Where(item => SameBookPayload(merged, item.Value)
                && !string.IsNullOrWhiteSpace(item.Version))
            .OrderBy(item => item, Comparer<RowCandidate<S3SyncBook>>.Create(CompareCandidate))
            .LastOrDefault();
        if (matching.Value is not null)
        {
            merged.SyncVersion = matching.Version;
            merged.UpdatedAt = matching.Time;
            return;
        }

        var latest = candidates.Count == 0
            ? DateTimeOffset.UtcNow
            : candidates.Max(item => item.Time);
        merged.UpdatedAt = latest.AddMilliseconds(1);
        if (clock is not null)
        {
            foreach (var candidate in candidates) clock.ObserveVersion(candidate.Version);
            merged.SyncVersion = clock.Next(latest);
        }
        else
        {
            merged.SyncVersion = candidates
                .OrderBy(item => item, Comparer<RowCandidate<S3SyncBook>>.Create(CompareCandidate))
                .LastOrDefault().Version;
        }
    }

    private static bool SameAnnotationPayload(S3SyncAnnotation a, S3SyncAnnotation b) =>
        a.BookId == b.BookId
        && a.BookFileId == b.BookFileId
        && a.ChapterPath == b.ChapterPath
        && a.Fragment == b.Fragment
        && a.StartOffset == b.StartOffset
        && a.EndOffset == b.EndOffset
        && a.SelectedText == b.SelectedText
        && a.Prefix == b.Prefix
        && a.Suffix == b.Suffix
        && a.Color == b.Color
        && a.UnderlineStyle == b.UnderlineStyle
        && a.Note == b.Note;

    private static bool SameBookPayload(S3SyncBook a, S3SyncBook b) =>
        a.Title == b.Title
        && a.Authors == b.Authors
        && a.Series == b.Series
        && a.SeriesIndex == b.SeriesIndex
        && a.Description == b.Description
        && a.Publisher == b.Publisher
        && a.PublishDate == b.PublishDate
        && a.Isbn == b.Isbn
        && a.PageCount == b.PageCount
        && a.Binding == b.Binding
        && a.OnlineRating == b.OnlineRating
        && a.OnlineRatingCount == b.OnlineRatingCount
        && a.Tags == b.Tags
        && a.Category == b.Category
        && a.IsFavorite == b.IsFavorite
        && a.ReadingStatus == b.ReadingStatus
        && a.CoverHash == b.CoverHash
        && a.CoverFileName == b.CoverFileName;

    private static S3SyncLayout MergeRemoteLayouts(IEnumerable<S3SyncLayout> variants)
    {
        var rows = variants.ToArray();
        var layout = LatestVersioned(
            rows, LayoutSyncVersion, item => item.LayoutPartVersion)!;
        var profile = LatestVersioned(
            rows.Where(item => item.ProfileUpdatedAt is not null),
            item => item.ProfileUpdatedAt!.Value, item => item.ProfilePartVersion);
        var merged = CloneLayout(layout);
        if (profile is not null)
        {
            CopyProfilePart(merged, profile);
            merged.UpdatedAt = Latest(merged.UpdatedAt, profile.UpdatedAt);
            merged.SyncVersion = LatestVersioned(
                new[] { layout, profile }, LayoutSyncVersion, item => item.SyncVersion)?.SyncVersion;
        }
        return merged;
    }

    private static S3SyncLayout MergeLayoutRows(S3SyncLayout local, S3SyncLayout remote)
    {
        var layoutNewer = IsRowNewer(
            remote.LayoutPartVersion, LayoutPartTime(remote),
            local.LayoutPartVersion, LayoutPartTime(local));
        var profileNewer = remote.ProfileUpdatedAt is not null
            && (local.ProfileUpdatedAt is null
                || IsRowNewer(
                    remote.ProfilePartVersion, remote.ProfileUpdatedAt.Value,
                    local.ProfilePartVersion, local.ProfileUpdatedAt.Value));
        if (!layoutNewer && !profileNewer) return local;

        var merged = CloneLayout(layoutNewer ? remote : local);
        if (profileNewer) CopyProfilePart(merged, remote);
        merged.LayoutPartVersion = layoutNewer ? remote.LayoutPartVersion : local.LayoutPartVersion;
        merged.LayoutUpdatedAt = layoutNewer ? remote.LayoutUpdatedAt : local.LayoutUpdatedAt;
        merged.ProfilePartVersion = profileNewer ? remote.ProfilePartVersion : local.ProfilePartVersion;
        merged.ProfileUpdatedAt = profileNewer ? remote.ProfileUpdatedAt : local.ProfileUpdatedAt;
        merged.UpdatedAt = Latest(local.UpdatedAt, remote.UpdatedAt);
        merged.SyncVersion = remote.SyncVersion ?? local.SyncVersion;
        return merged;
    }

    private static void CopyProfilePart(S3SyncLayout target, S3SyncLayout source)
    {
        target.UseBookOverride = source.UseBookOverride;
        target.AppearanceTheme = source.AppearanceTheme;
        target.AppearancePaperEnabled = source.AppearancePaperEnabled;
        target.AppearancePaperStrength = source.AppearancePaperStrength;
        target.AppearanceFibersEnabled = source.AppearanceFibersEnabled;
        target.ProfileUpdatedAt = source.ProfileUpdatedAt;
        target.ProfilePartVersion = source.ProfilePartVersion;
    }

    private static S3SyncLayout CloneLayout(S3SyncLayout source)
    {
        var clone = new S3SyncLayout
        {
            BookId = source.BookId,
            BookFileId = source.BookFileId,
            FontScale = source.FontScale,
            LineHeight = source.LineHeight,
            MaxWidth = source.MaxWidth,
            BodyPadding = source.BodyPadding,
            FontFamily = source.FontFamily,
            FlowMode = source.FlowMode,
            VerticalWriting = source.VerticalWriting,
            TwoPageMode = source.TwoPageMode,
            ParagraphIndent = source.ParagraphIndent,
            UseBookOverride = source.UseBookOverride,
            AppearanceTheme = source.AppearanceTheme,
            AppearancePaperEnabled = source.AppearancePaperEnabled,
            AppearancePaperStrength = source.AppearancePaperStrength,
            AppearanceFibersEnabled = source.AppearanceFibersEnabled,
            ProfileUpdatedAt = source.ProfileUpdatedAt,
            LayoutUpdatedAt = source.LayoutUpdatedAt,
            UpdatedAt = source.UpdatedAt,
            SyncVersion = source.SyncVersion,
            LayoutPartVersion = source.LayoutPartVersion,
            ProfilePartVersion = source.ProfilePartVersion
        };
        CopyOpaqueData(clone, source);
        return clone;
    }

    private static T ChooseField<T>(
        T local,
        T baseline,
        T remote,
        bool hasBaseline,
        DateTimeOffset localVersion,
        DateTimeOffset remoteVersion)
    {
        if (EqualityComparer<T>.Default.Equals(local, remote)) return local;
        var localChanged = !hasBaseline || !EqualityComparer<T>.Default.Equals(local, baseline);
        var remoteChanged = !hasBaseline || !EqualityComparer<T>.Default.Equals(remote, baseline);
        if (localChanged && !remoteChanged) return local;
        if (!localChanged && remoteChanged) return remote;
        return remoteVersion > localVersion ? remote : local;
    }

    private static bool SameAnnotationFields(S3SyncAnnotation a, S3SyncAnnotation b) =>
        a.ChapterPath == b.ChapterPath
        && a.Fragment == b.Fragment
        && a.StartOffset == b.StartOffset
        && a.EndOffset == b.EndOffset
        && a.SelectedText == b.SelectedText
        && a.Prefix == b.Prefix
        && a.Suffix == b.Suffix
        && a.Color == b.Color
        && a.UnderlineStyle == b.UnderlineStyle
        && a.Note == b.Note
        && a.CreatedAt == b.CreatedAt;

    private static bool SameBookFields(S3SyncBook a, S3SyncBook b) =>
        a.Title == b.Title
        && a.Authors == b.Authors
        && a.Series == b.Series
        && a.SeriesIndex == b.SeriesIndex
        && a.Description == b.Description
        && a.Publisher == b.Publisher
        && a.PublishDate == b.PublishDate
        && a.Isbn == b.Isbn
        && a.PageCount == b.PageCount
        && a.Binding == b.Binding
        && a.OnlineRating == b.OnlineRating
        && a.OnlineRatingCount == b.OnlineRatingCount
        && a.Tags == b.Tags
        && a.Category == b.Category
        && a.IsFavorite == b.IsFavorite
        && a.ReadingStatus == b.ReadingStatus
        && a.CreatedAt == b.CreatedAt;

    private static S3SyncAnnotation CloneAnnotation(S3SyncAnnotation source)
    {
        var clone = new S3SyncAnnotation
        {
            Id = source.Id,
            BookId = source.BookId,
            BookFileId = source.BookFileId,
            ChapterPath = source.ChapterPath,
            Fragment = source.Fragment,
            StartOffset = source.StartOffset,
            EndOffset = source.EndOffset,
            SelectedText = source.SelectedText,
            Prefix = source.Prefix,
            Suffix = source.Suffix,
            Color = source.Color,
            UnderlineStyle = source.UnderlineStyle,
            Note = source.Note,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
            SyncVersion = source.SyncVersion
        };
        CopyOpaqueData(clone, source);
        return clone;
    }

    private static void CopyOpaqueData(S3SyncExtensibleData target, S3SyncExtensibleData source)
    {
        if (source.ExtensionData is { Count: > 0 } data)
            target.ExtensionData = new Dictionary<string, JsonElement>(data, StringComparer.Ordinal);
        if (source.ExtensionDataVersions is { Count: > 0 } versions)
            target.ExtensionDataVersions = new Dictionary<string, DateTimeOffset>(versions, StringComparer.Ordinal);
    }

    private static DateTimeOffset Earliest(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    private static DateTimeOffset Latest(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    // Two devices can create a highlight for the same passage before either
    // one syncs. Those rows have different random IDs, so a plain ID-keyed
    // merge keeps both. This predicate deliberately stays conservative: same
    // file/chapter/anchor and either the same normalized quote or a large
    // overlap. It is only used to collapse accidental duplicates.
    private static bool IsDuplicateAnnotation(S3SyncAnnotation a, S3SyncAnnotation b)
    {
        if (a.Id == b.Id) return false;
        if (a.BookId != b.BookId) return false;
        if (a.BookFileId != Guid.Empty && b.BookFileId != Guid.Empty && a.BookFileId != b.BookFileId)
            return false;
        if (!string.Equals(a.ChapterPath, b.ChapterPath, StringComparison.Ordinal)) return false;
        if (!string.Equals(a.Fragment ?? string.Empty, b.Fragment ?? string.Empty, StringComparison.Ordinal))
            return false;

        var sameRange = a.StartOffset == b.StartOffset && a.EndOffset == b.EndOffset;
        var quoteA = NormalizeQuote(a.SelectedText);
        var quoteB = NormalizeQuote(b.SelectedText);
        if (sameRange && (quoteA == quoteB || quoteA.Length == 0 || quoteB.Length == 0))
            return true;
        if (quoteA.Length == 0 || quoteB.Length == 0) return false;
        if (!string.Equals(quoteA, quoteB, StringComparison.Ordinal))
        {
            if (!quoteA.Contains(quoteB, StringComparison.Ordinal)
                && !quoteB.Contains(quoteA, StringComparison.Ordinal)) return false;
        }

        var overlap = Math.Min(a.EndOffset, b.EndOffset) - Math.Max(a.StartOffset, b.StartOffset);
        var shorter = Math.Min(a.EndOffset - a.StartOffset, b.EndOffset - b.StartOffset);
        return overlap > 0 && shorter > 0 && overlap * 5 >= shorter * 4;
    }

    private static bool IsDuplicateBookmark(S3SyncBookmark a, S3SyncBookmark b)
    {
        if (a.Id == b.Id) return false;
        if (a.BookId != b.BookId) return false;
        if (a.BookFileId != Guid.Empty && b.BookFileId != Guid.Empty && a.BookFileId != b.BookFileId)
            return false;
        return string.Equals(a.ChapterPath, b.ChapterPath, StringComparison.Ordinal)
            && string.Equals(a.Fragment ?? string.Empty, b.Fragment ?? string.Empty, StringComparison.Ordinal)
            && a.ChapterIndex == b.ChapterIndex
            && a.ScrollPosition == b.ScrollPosition
            && a.FlowMode == b.FlowMode
            && NormalizeQuote(a.ContentPositionJson ?? string.Empty)
                == NormalizeQuote(b.ContentPositionJson ?? string.Empty)
            // Two bookmarks at the same position but with different titles or
            // quotes are intentionally distinct user records.
            && NormalizeQuote(a.Title) == NormalizeQuote(b.Title)
            && NormalizeQuote(a.Quote) == NormalizeQuote(b.Quote);
    }

    private static string NormalizeQuote(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string CombineNotes(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return right;
        if (string.IsNullOrWhiteSpace(right) || string.Equals(left, right, StringComparison.Ordinal))
            return left;
        if (left.Contains(right, StringComparison.Ordinal)) return left;
        if (right.Contains(left, StringComparison.Ordinal)) return right;
        return left.TrimEnd() + "\n\n" + right.Trim();
    }

    private static bool WinsDuplicate<T>(T candidate, T current,
        Func<T, DateTimeOffset> createdAt, Func<T, Guid> id) =>
        createdAt(candidate) < createdAt(current)
        || (createdAt(candidate) == createdAt(current)
            && string.CompareOrdinal(id(candidate).ToString("N"), id(current).ToString("N")) < 0);
}
