using YARG.Core.IO;

namespace YARG.Core.Song.Cache
{
    /// <summary>
    /// Everything found in a shortname's songs_updates/&lt;shortname&gt; folder that can be applied
    /// to an ini-format song: the update midi, album art, and
    /// whatever DTA-declared metadata exists for that shortname.
    /// </summary>
    internal struct IniUpdateInfo
    {
        public string? MidiPath;
        public string? ImagePath;
        public DTAEntry Dta;
    }

    /// <summary>
    /// Applies the metadata-relevant fields of a songs_updates.dta entry onto an ini song's
    /// SongMetadata. Mirrors the metadata portion of RBCONEntry.ParseDTA, but only the fields
    /// SongMetadata actually has — RBCON-only concerns (rank/intensity, hopo threshold, tuning,
    /// venue metadata, etc.) don't apply to ini songs and are left alone.
    /// </summary>
    internal static class IniDtaMetadataApplier
    {
        public static void Apply(in DTAEntry dta, ref SongMetadata metadata)
        {
            if (dta.Name != null)          { metadata.Name          = YARGDTAReader.DecodeString(dta.Name.Value, dta.MetadataEncoding); }
            if (dta.Artist != null)        { metadata.Artist        = YARGDTAReader.DecodeString(dta.Artist.Value, dta.MetadataEncoding); }
            if (dta.CoveredBy != null)     { metadata.CoveredBy     = YARGDTAReader.DecodeString(dta.CoveredBy.Value, dta.MetadataEncoding); }
            if (dta.Album != null)         { metadata.Album         = YARGDTAReader.DecodeString(dta.Album.Value, dta.MetadataEncoding); }
            if (dta.Charter != null)       { metadata.Charter       = YARGDTAReader.DecodeString(dta.Charter.Value, dta.MetadataEncoding); }
            if (dta.LoadingPhrase != null) { metadata.LoadingPhrase = YARGDTAReader.DecodeString(dta.LoadingPhrase.Value, dta.MetadataEncoding); }
            if (dta.Playlist != null)      { metadata.Playlist      = YARGDTAReader.DecodeString(dta.Playlist.Value, dta.MetadataEncoding); }

            if (dta.Genre != null)
            {
                metadata.Genre = dta.Genre;
                metadata.Subgenre = string.Empty;
            }
            if (dta.Subgenre != null) { metadata.Subgenre = dta.Subgenre.Replace("subgenre_", ""); }

            if (dta.YearAsNumber != null)          { metadata.Year          = dta.YearAsNumber.Value.ToString("D4"); }
            if (dta.YearSecondaryAsNumber != null) { metadata.YearSecondary = dta.YearSecondaryAsNumber.Value.ToString("D4"); }

            if (dta.Source != null)
            {
                // Simplified from RBCONEntry's version, which also special-cases "UGC_"-prefixed
                // node names — not meaningful for ini shortnames.
                metadata.Source = dta.Source is "ugc" or "ugc_plus" ? "customs" : dta.Source;
            }

            if (dta.SongLength != null)  { metadata.SongLength = dta.SongLength.Value; }
            if (dta.IsMaster != null)    { metadata.IsMaster    = dta.IsMaster.Value; }
            if (dta.AlbumTrack != null)  { metadata.AlbumTrack  = dta.AlbumTrack.Value; }
            if (dta.Preview != null)     { metadata.Preview     = dta.Preview.Value; }
            if (dta.SongRating != null)  { metadata.SongRating  = dta.SongRating.Value; }
            if (dta.VocalGender != null) { metadata.VocalGender = DTAEntry.ConvertVocalGender(dta.VocalGender); }
        }
    }
}
