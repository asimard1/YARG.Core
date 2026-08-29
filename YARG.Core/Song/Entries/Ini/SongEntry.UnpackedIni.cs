using System;
using System.Collections.Generic;
using System.IO;
using YARG.Core.Song.Cache;
using YARG.Core.IO;
using YARG.Core.IO.Ini;
using YARG.Core.Audio;
using YARG.Core.Venue;
using System.Linq;
using YARG.Core.Logging;
using YARG.Core.Extensions;

namespace YARG.Core.Song
{
    internal sealed class UnpackedIniEntry : IniSubEntry
    {
        private readonly DateTime? _iniLastWrite;
        private readonly string? _shortname;
        public string? Shortname => _shortname;
        private readonly string? _updateMidiPath;
        internal override string? UpdateMidiPath => _updateMidiPath;
        private readonly string? _updateMoggPath;
        private readonly string? _updateImagePath;
        private RBAudio<int> _indices = RBAudio<int>.Empty;
        private RBAudio<float> _panning = RBAudio<float>.Empty;

        public override EntryType SubType => EntryType.Ini;

        internal override void Serialize(MemoryStream stream, CacheWriteIndices node)
        {
            stream.WriteByte((byte) _chartFormat);
            stream.Write(_chartLastWrite.ToBinary(), Endianness.Little);
            stream.Write(_iniLastWrite.HasValue);
            if (_iniLastWrite.HasValue)
            {
                stream.Write(_iniLastWrite.Value.ToBinary(), Endianness.Little);
            }

            stream.Write(_shortname != null);
            if (_shortname != null)
            {
                stream.Write(_shortname);
            }

            stream.Write(_updateMidiPath != null);
            if (_updateMidiPath != null)
            {
                stream.Write(_updateMidiPath);
            }

            stream.Write(_updateMoggPath != null);
            if (_updateMoggPath != null)
            {
                stream.Write(_updateMoggPath);
            }

            stream.Write(_updateImagePath != null);
            if (_updateImagePath != null)
            {
                stream.Write(_updateImagePath);
            }

            IniAudioSerializer.WriteAudio(in _indices, stream);
            IniAudioSerializer.WriteAudio(in _panning, stream);

            base.Serialize(stream, node);
        }

        public override StemMixer? LoadAudio(float speed, double volume, bool enableCensoring, params SongStem[] ignoreStems)
        {
            if (_updateMoggPath != null && File.Exists(_updateMoggPath))
            {
                var updateMoggMixer = LoadUpdateMoggAudio(speed, volume, ignoreStems);
                if (updateMoggMixer != null)
                {
                    return updateMoggMixer;
                }
                YargLogger.LogFormatError("Update mogg at {0} failed to load, falling back to loose audio files", _updateMoggPath);
            }

            var subFiles = GetSubFiles();
            bool clampStemVolume = GlobalAudioHandler.CLAMPED_AUDIO_SOURCES.Contains(_metadata.Source.ToLowerInvariant());

            // Prefer a raw multi-channel .mogg (+ its channel-map sidecar) over split
            // stem files, when both are present.
            var looseMoggMixer = TryLoadMoggAudio(subFiles, speed, volume, clampStemVolume, ignoreStems);
            if (looseMoggMixer != null)
            {
                return looseMoggMixer;
            }

            var mixer = GlobalAudioHandler.CreateMixer(ToString(), speed, volume, clampStemVolume: clampStemVolume,
                normalize: true);
            if (mixer == null)
            {
                YargLogger.LogError("Failed to create mixer!");
                return null;
            }

            var addedCleanStems = new HashSet<SongStem>();
            if (enableCensoring)
            {
                foreach (var stem in IniAudio.SupportedCleanStems)
                {
                    var stemEnum = AudioHelpers.SupportedStems[stem];

                    if (ignoreStems.Contains(stemEnum))
                    {
                        continue;
                    }

                    if (TryLoadStem(stem, stemEnum, subFiles, mixer))
                    {
                        addedCleanStems.Add(stemEnum);
                    }
                }
            }

            foreach (var stem in IniAudio.SupportedStems)
            {
                var stemEnum = AudioHelpers.SupportedStems[stem];

                if (ignoreStems.Contains(stemEnum) || addedCleanStems.Contains(stemEnum))
                {
                    continue;
                }
                TryLoadStem(stem, stemEnum, subFiles, mixer);
            }

            if (!enableCensoring)
            {
                foreach (var stem in IniAudio.SupportedExplicitStems)
                {
                    var stemEnum = AudioHelpers.SupportedStems[stem];

                    if (ignoreStems.Contains(stemEnum))
                    {
                        continue;
                    }
                    TryLoadStem(stem, stemEnum, subFiles, mixer);
                }
            }

            if (mixer.Channels.Count == 0)
            {
                YargLogger.LogFormatError("Failed to add any stems! ({0})", ToString());
                mixer.Dispose();
                return null;
            }

            if (GlobalAudioHandler.LogMixerStatus)
            {
                YargLogger.LogFormatInfo("Loaded {0} stems", mixer.Channels.Count);
            }
            return mixer;
        }

        private StemMixer? LoadUpdateMoggAudio(float speed, double volume, SongStem[] ignoreStems)
        {
            var stream = new FileStream(_updateMoggPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
            bool clampStemVolume = GlobalAudioHandler.CLAMPED_AUDIO_SOURCES.Contains(_metadata.Source.ToLowerInvariant());
            return MoggAudioLoader.BuildMixer(stream, ToString(), speed, volume, clampStemVolume,
                in _indices, in _panning, ignoreStems);
        }

        /// <summary>
        /// Looks for a "*.mogg" + "*.mogg.dta" pair in the song folder and, if found,
        /// builds a mixer directly from the raw multi-channel mogg instead of split
        /// stem files. Returns null (without logging as an error) if no mogg pair is
        /// present, so the caller can fall back to split stems.
        /// </summary>
        private StemMixer? TryLoadMoggAudio(Dictionary<string, string> subFiles, float speed, double volume,
            bool clampStemVolume, SongStem[] ignoreStems)
        {
            foreach (var name in subFiles.Keys)
            {
                if (!name.EndsWith(".mogg"))
                {
                    continue;
                }

                if (!subFiles.TryGetValue(name + ".dta", out var dtaPath))
                {
                    YargLogger.LogFormatWarning("Found {0} but no matching {0}.dta channel map - falling back to split stems", name);
                    return null;
                }

                using var dtaBytes = FixedArray.LoadFile(dtaPath);
                if (!MoggAudioLoader.TryParseChannelMap(dtaBytes, out var indices, out var panning))
                {
                    return null;
                }

                var moggPath = subFiles[name];
                var stream = new FileStream(moggPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
                return MoggAudioLoader.BuildMixer(stream, ToString(), speed, volume, clampStemVolume,
                    in indices, in panning, ignoreStems);
            }
            return null;
        }

        private static bool TryLoadStem(string stem, SongStem stemEnum, Dictionary<string, string> fileDictionary, StemMixer mixer)
        {
            foreach (var format in IniAudio.SupportedFormats)
            {
                var stemName = stem + format;
                if (fileDictionary.TryGetValue(stemName, out var file))
                {
                    var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
                    if (mixer.AddChannel(stream, stemEnum))
                    {
                        // No duplicates
                        return true;
                    }
                    stream.Dispose();
                    YargLogger.LogFormatError("Failed to load stem file {0}", file);
                }
            }

            return false;
        }

        public override StemMixer? LoadPreviewAudio(float speed, bool enableCensoring)
        {
            foreach (var filename in enableCensoring ? CLEAN_PREVIEW_FILES : PREVIEW_FILES)
            {
                var audioFile = Path.Combine(_location, filename);
                if (File.Exists(audioFile))
                {
                    return GlobalAudioHandler.LoadCustomFile(audioFile, speed, 0, true, SongStem.Preview);
                }
            }
            return LoadAudio(speed, 0, enableCensoring, SongStem.Crowd);
        }

        public override YARGImage? LoadAlbumData()
        {
            var subFiles = GetSubFiles();

            // Prefer a raw DXT texture extracted straight from a CON pack over a
            // re-encoded/converted image, when both are available.
            var dxtImage = TryLoadDXTAlbumArt(subFiles);
            if (dxtImage != null)
            {
                return dxtImage;
            }

            // Same raw DXT format as above, just sourced from songs_updates instead
            // of the song's own folder - covers songs whose art only ships as an update.
            if (_updateImagePath != null && File.Exists(_updateImagePath))
            {
                var updateImage = YARGImage.LoadDXT(_updateImagePath);
                if (updateImage != null)
                {
                    return updateImage;
                }
                YargLogger.LogFormatError("Update image at {0} failed to load", _updateImagePath);
            }

            if (!string.IsNullOrEmpty(_cover) && subFiles.TryGetValue(_cover, out var cover))
            {
                var image = YARGImage.Load(cover);
                if (image != null)
                {
                    return image;
                }
                YargLogger.LogFormatError("Image at {0} failed to load", cover);
            }

            foreach (string albumName in ALBUMART_FILES)
            {
                if (subFiles.TryGetValue(albumName, out var file))
                {
                    var image = YARGImage.Load(file);
                    if (image != null)
                    {
                        return image;
                    }
                    YargLogger.LogFormatError("Image at {0} failed to load", file);
                }
            }
            return null;
        }

        /// <summary>
        /// Looks for a raw ".png_xbox" or ".png_ps3" texture in the song folder.
        /// Both formats are self-describing (dimensions/DXT variant live in the
        /// file's own header), so unlike the mogg case, no sidecar is needed.
        /// </summary>
        private static YARGImage? TryLoadDXTAlbumArt(Dictionary<string, string> subFiles)
        {
            foreach (var name in subFiles.Keys)
            {
                if (name.EndsWith(".png_xbox"))
                {
                    return YARGImage.LoadDXT(subFiles[name]);
                }
                if (name.EndsWith(".png_ps3"))
                {
                    return YARGImage.LoadPS3DXT(subFiles[name]);
                }
            }
            return null;
        }

        public override BackgroundResult? LoadBackground(bool enableCensoring, bool excludeYarground = false)
        {
            var subFiles = GetSubFiles();
            string censorSuffix = enableCensoring ? CLEAN_BACKGROUND_SUFFIX : EXPLICIT_BACKGROUND_SUFFIX;
            if (subFiles.TryGetValue("bg.yarground", out var file) && !excludeYarground)
            {
                var stream = File.OpenRead(file);
                return new BackgroundResult(BackgroundType.Yarground, stream);
            }

            if (subFiles.TryGetValue(_video, out var video))
            {
                var stream = File.OpenRead(video);
                return new BackgroundResult(BackgroundType.Video, stream);
            }

            foreach (var stem in BACKGROUND_FILENAMES)
            {
                foreach (var format in VIDEO_EXTENSIONS)
                {
                    if (subFiles.TryGetValue(stem + censorSuffix + format, out file))
                    {
                        var stream = File.OpenRead(file);
                        return new BackgroundResult(BackgroundType.Video, stream);
                    }
                    if (subFiles.TryGetValue(stem + format, out file))
                    {
                        var stream = File.OpenRead(file);
                        return new BackgroundResult(BackgroundType.Video, stream);
                    }
                }
            }

            if (subFiles.TryGetValue(_background, out file) || TryGetRandomBackgroundImage(subFiles, enableCensoring, out file))
            {
                var image = YARGImage.Load(file!);
                if (image != null)
                {
                    return new BackgroundResult(image);
                }
            }
            return null;
        }

        #nullable disable
        public override FixedArray<byte> LoadMiloData()
        {
            var subFiles = GetSubFiles();
            foreach (var name in subFiles.Keys)
            {
                if (name.EndsWith(".milo_xbox") || name.EndsWith(".milo"))
                {
                    if (subFiles.TryGetValue(name, out var file) && File.Exists(file))
                    {
                        return FixedArray.LoadFile(file);
                    }
                }
            }

            return null;
        }
        // this is included for completeness, but please do not use this
        public override FixedArray<byte> LoadVocData()
        {
            var subFiles = GetSubFiles();
            foreach (var name in subFiles.Keys)
            {
                if (name.EndsWith(".voc"))
                {
                    if (subFiles.TryGetValue(name, out var file) && File.Exists(file))
                    {
                        return FixedArray.LoadFile(file);
                    }
                }
            }

            return null;
        }

        protected override FixedArray<byte> GetChartData(string filename)
        {
            string chartPath = Path.Combine(_location, filename);
            if (!AbridgedFileInfo.Validate(chartPath, in _chartLastWrite))
            {
                return null;
            }

            string iniPath = Path.Combine(_location, "song.ini");
            if (_iniLastWrite.HasValue)
            {
                if (!AbridgedFileInfo.Validate(iniPath, _iniLastWrite.Value) && File.Exists(iniPath))
                {
                    return null;
                }
            }
            else if (File.Exists(iniPath))
            {
                return null;
            }

            return FixedArray.LoadFile(chartPath);
        }
        #nullable restore

        private Dictionary<string, string> GetSubFiles()
        {
            Dictionary<string, string> files = new();
            if (Directory.Exists(_location))
            {
                foreach (var file in Directory.EnumerateFiles(_location))
                {
                    files.Add(file[(_location.Length + 1)..].ToLower(), file);
                }
            }
            return files;
        }

        private UnpackedIniEntry(string directory, in DateTime chartLastWrite, in DateTime? iniLastWrite, in ChartFormat format,
            string? shortname, string? updateMidiPath, string? updateMoggPath, string? updateImagePath)
            : base(directory, in chartLastWrite, format)
        {
            _iniLastWrite = iniLastWrite;
            _shortname = shortname;
            _updateMidiPath = updateMidiPath;
            _updateMoggPath = updateMoggPath;
            _updateImagePath = updateImagePath;
        }

        public static ScanExpected<UnpackedIniEntry> ProcessNewEntry(string directory, FileInfo chartInfo, ChartFormat format, FileInfo? iniFile, FileInfo? dtaFile, string defaultPlaylist, IReadOnlyDictionary<string, IniUpdateInfo> iniUpdateInfos)
        {
            IniModifierCollection iniModifiers;
            DateTime? iniLastWrite = default;
            if (iniFile != null)
            {
                iniModifiers = SongIniHandler.ReadSongIniFile(iniFile.FullName);
                iniLastWrite = AbridgedFileInfo.NormalizedLastWrite(iniFile);
            }
            // No song.ini - fall back to a raw metadata dta (e.g. extracted straight from a CON pack)
            else if (dtaFile != null && TryParseDTAModifiers(dtaFile, out var dtaModifiers))
            {
                iniModifiers = dtaModifiers;
            }
            else
            {
                iniModifiers = new();
            }

            string? shortname = iniModifiers.Extract("shortname", out string sn) ? sn : null;
            string? updateMidiPath = null;
            string? updateMoggPath = null;
            string? updateImagePath = null;
            var indices = RBAudio<int>.Empty;
            var panning = RBAudio<float>.Empty;
            DTAEntry? dta = null;
            if (shortname != null && iniUpdateInfos.TryGetValue(shortname, out var updateInfo))
            {
                updateMidiPath = updateInfo.MidiPath;
                updateMoggPath = updateInfo.MoggPath;
                updateImagePath = updateInfo.ImagePath;
                RBAudioCalculator.Calculate(in updateInfo.Dta, ref indices, ref panning);
                dta = updateInfo.Dta;
            }

            var entry = new UnpackedIniEntry(directory, AbridgedFileInfo.NormalizedLastWrite(chartInfo), in iniLastWrite, format,
                shortname, updateMidiPath, updateMoggPath, updateImagePath)
            {
                _indices = indices,
                _panning = panning,
            };
            entry._metadata.Playlist = defaultPlaylist;

            using var file = FixedArray.LoadFile(chartInfo.FullName);

            var result = ScanChart(entry, file, iniModifiers);
            if (result != ScanResult.Success)
            {
                return new ScanUnexpected(result);
            }

            // Metadata declared in songs_updates.dta takes priority over the ini file, same as it
            // would for a CON-pack user, so this only runs after ScanChart has already filled
            // _metadata from the ini.
            if (dta != null)
            {
                IniDtaMetadataApplier.Apply(dta.Value, ref entry._metadata);
                (entry._parsedYear, entry._yearAsNumber) = ParseYear(entry._metadata.Year);
                entry.SetSortStrings();
            }

            return entry;
        }

        private static bool TryParseDTAModifiers(FileInfo dtaFile, out IniModifierCollection modifiers)
        {
            modifiers = new IniModifierCollection();
            try
            {
                using var dtaBytes = FixedArray.LoadFile(dtaFile.FullName);
                var container = YARGDTAReader.Create(dtaBytes);
                if (!YARGDTAReader.StartNode(ref container))
                {
                    return false;
                }

                string name = YARGDTAReader.GetNameOfNode(ref container, true);
                var dta = DTAEntry.Create(name, container);
                YARGDTAReader.EndNode(ref container);

                modifiers = DTAMetadataAdapter.BuildModifiers(dta);
                return true;
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, $"Error while parsing metadata dta {dtaFile.FullName}!");
                return false;
            }
        }

        public static UnpackedIniEntry? TryDeserialize(string baseDirectory, ref FixedArrayStream stream, CacheReadStrings strings)
        {
            string directory = Path.Combine(baseDirectory, stream.ReadString());
            ref readonly var chart = ref CHART_FILE_TYPES[stream.ReadByte()];
            var chartLastWrite = DateTime.FromBinary(stream.Read<long>(Endianness.Little));
            if (!AbridgedFileInfo.Validate(Path.Combine(directory, chart.Filename), chartLastWrite))
            {
                return null;
            }

            string iniFile = Path.Combine(directory, "song.ini");
            DateTime? iniLastWrite = default;
            if (stream.ReadBoolean())
            {
                iniLastWrite = DateTime.FromBinary(stream.Read<long>(Endianness.Little));
                if (!AbridgedFileInfo.Validate(iniFile, iniLastWrite.Value))
                {
                    return null;
                }
            }
            else if (File.Exists(iniFile))
            {
                return null;
            }

            string? shortname = stream.ReadBoolean() ? stream.ReadString() : null;
            string? updateMidiPath = stream.ReadBoolean() ? stream.ReadString() : null;
            if (updateMidiPath != null && !File.Exists(updateMidiPath))
            {
                return null; // update mid vanished since cache was written — force rescan
            }

            string? updateMoggPath = stream.ReadBoolean() ? stream.ReadString() : null;
            if (updateMoggPath != null && !File.Exists(updateMoggPath))
            {
                return null; // update mogg vanished since cache was written — force rescan
            }

            string? updateImagePath = stream.ReadBoolean() ? stream.ReadString() : null;
            if (updateImagePath != null && !File.Exists(updateImagePath))
            {
                return null; // update image vanished since cache was written — force rescan
            }

            var indices = RBAudio<int>.Empty;
            var panning = RBAudio<float>.Empty;
            IniAudioSerializer.ReadAudio(ref indices, ref stream);
            IniAudioSerializer.ReadAudio(ref panning, ref stream);

            var entry = new UnpackedIniEntry(directory, in chartLastWrite, in iniLastWrite, chart.Format, shortname, updateMidiPath, updateMoggPath, updateImagePath)
            {
                _indices = indices,
                _panning = panning,
            };
            entry.Deserialize(ref stream, strings);
            return entry;
        }

        public static UnpackedIniEntry ForceDeserialize(string baseDirectory, ref FixedArrayStream stream, CacheReadStrings strings)
        {
            string directory = Path.Combine(baseDirectory, stream.ReadString());
            ref readonly var chart = ref CHART_FILE_TYPES[stream.ReadByte()];
            var chartLastWrite = DateTime.FromBinary(stream.Read<long>(Endianness.Little));
            DateTime? iniLastWrite = stream.ReadBoolean() ? DateTime.FromBinary(stream.Read<long>(Endianness.Little)) : default;
            string? shortname = stream.ReadBoolean() ? stream.ReadString() : null;
            string? updateMidiPath = stream.ReadBoolean() ? stream.ReadString() : null;
            string? updateMoggPath = stream.ReadBoolean() ? stream.ReadString() : null;
            string? updateImagePath = stream.ReadBoolean() ? stream.ReadString() : null;

            var indices = RBAudio<int>.Empty;
            var panning = RBAudio<float>.Empty;
            IniAudioSerializer.ReadAudio(ref indices, ref stream);
            IniAudioSerializer.ReadAudio(ref panning, ref stream);

            var entry = new UnpackedIniEntry(directory, in chartLastWrite, in iniLastWrite, chart.Format, shortname, updateMidiPath, updateMoggPath, updateImagePath)
            {
                _indices = indices,
                _panning = panning,
            };
            entry.Deserialize(ref stream, strings);
            return entry;
        }
    }
}
