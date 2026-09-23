using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Projectionist.Services.Handoff;

/// <summary>The fields of a MediaStream jellyfin-web's track choice looks at.</summary>
internal sealed record HandoffStream(
    int Index,
    MediaStreamType Type,
    string? Codec,
    string? DisplayTitle,
    string? Language,
    bool IsExternal = false,
    bool IsForced = false,
    bool IsDefault = false);

/// <summary>A static media source as the item DTO shows it to this user (user-specific default tracks).</summary>
internal sealed record HandoffSource(
    string Id,
    int? DefaultAudioStreamIndex,
    int? DefaultSubtitleStreamIndex,
    IReadOnlyList<HandoffStream> Streams);

/// <summary>
/// What predicts the tracks of a jellyfin-web details-page Play: the feature's first static
/// source (the one the page selects), the preroll the player comes from (the last intro), and the
/// user's "remember audio / subtitle selections" settings.
/// </summary>
internal sealed record HandoffTrackContext(
    HandoffSource Feature,
    HandoffSource? LastIntro,
    bool RememberAudio,
    bool RememberSubtitles);

/// <summary>The MediaSourceId / AudioStreamIndex / SubtitleStreamIndex a Play will send.</summary>
internal readonly record struct HandoffTrackChoice(string MediaSourceId, int? AudioStreamIndex, int SubtitleStreamIndex);

/// <summary>
/// Predicts the explicit-tracks PlaybackInfo of jellyfin-web 10.11's details-page Play (R3-COR-1,
/// R3-COR-2), which the rr-C1 probe must ask in the same shape:
///   - the source is item.MediaSources[0] - the STATIC order (VideoFile, non-3D, width descending,
///     DtoService -> GetStaticMediaSources), not the PlaybackInfo response's, which the server
///     re-sorts direct-playable first (itemDetails/index.js reloadSelections);
///   - the audio / subtitle dropdowns start at that source's user-specific
///     DefaultAudioStreamIndex / DefaultSubtitleStreamIndex (a missing subtitle default is Off,
///     -1; a missing audio default is the first track in itemHelper.sortTracks order), NOT the
///     PlaybackInfo response's, which the server overwrites with its own playable pick
///     (MediaInfoHelper.SetDeviceSpecificData);
///   - then, because the feature is played as the next item after the preroll,
///     jellyfin-web carries the preroll's audio / subtitle choice over when the user remembers
///     selections and a feature track is similar enough to the preroll's (the carry-over rule
///     implemented by Rank below).
/// </summary>
internal static class HandoffTracks
{
    public static HandoffTrackChoice PredictDetailsPage(HandoffTrackContext context)
    {
        var feature = context.Feature;
        int? audio = DropdownAudio(feature);
        var subtitle = feature.DefaultSubtitleStreamIndex is { } s
                       && feature.Streams.Any(t => t.Type == MediaStreamType.Subtitle && t.Index == s)
            ? s
            : -1;

        var intro = context.LastIntro;
        if (intro is not null)
        {
            if (context.RememberAudio && intro.DefaultAudioStreamIndex is { } prevAudio)
            {
                var carried = Rank(prevAudio, intro, feature.Streams, MediaStreamType.Audio);
                if (carried is not null)
                {
                    audio = carried;
                }
            }

            if (context.RememberSubtitles && intro.DefaultSubtitleStreamIndex is { } prevSubtitle)
            {
                var carried = prevSubtitle == -1 ? -1 : Rank(prevSubtitle, intro, feature.Streams, MediaStreamType.Subtitle);
                if (carried is not null)
                {
                    subtitle = carried.Value;
                }
            }
        }

        return new HandoffTrackChoice(feature.Id, audio, subtitle);
    }

    /// <summary>The value of the details page's audio dropdown for <paramref name="source"/>.</summary>
    private static int? DropdownAudio(HandoffSource source)
    {
        var tracks = source.Streams.Where(t => t.Type == MediaStreamType.Audio).ToList();
        if (tracks.Count == 0)
        {
            return null;
        }

        if (source.DefaultAudioStreamIndex is { } d && tracks.Any(t => t.Index == d))
        {
            return d;
        }

        // No option is marked selected: the browser selects the first, in itemHelper.sortTracks order.
        return tracks
            .OrderBy(t => t.IsExternal ? 1 : 0)
            .ThenBy(t => t.IsForced ? 0 : 1)
            .ThenBy(t => t.IsDefault ? 0 : 1)
            .ThenBy(t => t.Index)
            .First()
            .Index;
    }

    // Carry-over rule, as observed in jellyfin-web 10.11 when the next queue item starts
    // (independent implementation of the observed behaviour, not a copy):
    //   - the reference track is the previous source's stream at LIST POSITION prevIndex;
    //   - its ordinal is how many same-type streams precede the first same-type stream whose
    //     Index equals prevIndex (all of them if none does);
    //   - each same-type candidate scores CodecWeight for an equal codec, PositionWeight for an
    //     equal ordinal, TitleWeight for an equal non-empty DisplayTitle and LanguageWeight for
    //     an equal language other than empty/"und";
    //   - the highest score of at least MinCarryOverScore wins, the earliest candidate on a tie;
    //     no such candidate means the default track is kept.
    private const int CodecWeight = 1;
    private const int PositionWeight = 1;
    private const int TitleWeight = 2;
    private const int LanguageWeight = 2;
    private const int MinCarryOverScore = 3;

    /// <summary>
    /// The feature track that the carry-over rule above picks for the preroll's stream
    /// <paramref name="prevIndex"/>, or null to keep the default.
    /// </summary>
    internal static int? Rank(int prevIndex, HandoffSource prev, IReadOnlyList<HandoffStream> streams, MediaStreamType type)
    {
        if (prevIndex < 0 || prevIndex >= prev.Streams.Count)
        {
            return null;
        }

        var reference = prev.Streams[prevIndex];
        var previousOfType = prev.Streams.Where(s => s.Type == type).ToList();
        var ordinal = previousOfType.FindIndex(s => s.Index == prevIndex);
        var referenceOrdinal = ordinal >= 0 ? ordinal : previousOfType.Count;

        // OrderByDescending is a stable sort, so the earliest candidate wins a tie.
        return streams
            .Where(s => s.Type == type)
            .Select((candidate, position) => (candidate.Index, Score: Similarity(reference, candidate, position == referenceOrdinal)))
            .Where(c => c.Score >= MinCarryOverScore)
            .OrderByDescending(c => c.Score)
            .Select(c => (int?)c.Index)
            .FirstOrDefault();
    }

    private static int Similarity(HandoffStream reference, HandoffStream candidate, bool samePosition)
    {
        var score = samePosition ? PositionWeight : 0;
        if (string.Equals(reference.Codec, candidate.Codec, StringComparison.Ordinal))
        {
            score += CodecWeight;
        }

        if (!string.IsNullOrEmpty(reference.DisplayTitle)
            && string.Equals(reference.DisplayTitle, candidate.DisplayTitle, StringComparison.Ordinal))
        {
            score += TitleWeight;
        }

        if (!string.IsNullOrEmpty(reference.Language)
            && !string.Equals(reference.Language, "und", StringComparison.Ordinal)
            && string.Equals(reference.Language, candidate.Language, StringComparison.Ordinal))
        {
            score += LanguageWeight;
        }

        return score;
    }

    /// <summary>
    /// The context from the server's own static sources for this user (the same
    /// GetStaticMediaSources(item, true, user) call the item DTO is built from). Null when the
    /// feature or user is unknown or has no sources.
    /// </summary>
    public static HandoffTrackContext? FromServer(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IUserManager userManager,
        Guid userId,
        Guid featureId,
        IReadOnlyList<Guid> introIds)
    {
        var user = userManager.GetUserById(userId);
        if (user is null || libraryManager.GetItemById(featureId) is not { } feature || feature is not IHasMediaSources)
        {
            return null;
        }

        var sources = mediaSourceManager.GetStaticMediaSources(feature, true, user);
        if (sources.Count == 0 || string.IsNullOrEmpty(sources[0].Id))
        {
            return null;
        }

        HandoffSource? intro = null;
        if (introIds.Count > 0 && libraryManager.GetItemById(introIds[^1]) is { } introItem and IHasMediaSources)
        {
            // Approximation: the player's track index for the preroll is its PlaybackInfo's pick,
            // which for a playable preroll is this user default.
            var introSources = mediaSourceManager.GetStaticMediaSources(introItem, true, user);
            if (introSources.Count > 0)
            {
                intro = Map(introSources[0]);
            }
        }

        return new HandoffTrackContext(Map(sources[0]), intro, user.RememberAudioSelections, user.RememberSubtitleSelections);
    }

    private static HandoffSource Map(MediaSourceInfo source) => new(
        source.Id,
        source.DefaultAudioStreamIndex,
        source.DefaultSubtitleStreamIndex,
        (source.MediaStreams ?? new List<MediaStream>())
            .Select(s => new HandoffStream(s.Index, s.Type, s.Codec, s.DisplayTitle, s.Language, s.IsExternal, s.IsForced, s.IsDefault))
            .ToList());
}
