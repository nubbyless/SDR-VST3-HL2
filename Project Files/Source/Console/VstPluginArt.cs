//=================================================================
// VstPluginArt.cs
//=================================================================
// Resolves and caches the vendor-supplied preview artwork ("snapshot")
// shipped inside a VST3 plugin bundle, for display in the VST chain rack.
//
// The VST3 SDK stores snapshots at:
//     <bundle>/Contents/Resources/Snapshots/<32-hex-CID>_snapshot[_<f>x].png
//
// where <32-hex-CID> matches the class ID written to moduleinfo.json, and the
// optional _<f>x suffix carries a scale factor (e.g. _2.0x). A file with no
// scale suffix is the 1.0x default. See the vendored SDK at
// lib/vst3sdk/public.sdk/source/vst/hosting/module.cpp (decodeUID /
// rangeOfScaleFactor) and module_win32.cpp for the reference implementation.
//
// VST2 plugins are plain DLLs with no bundle layout, so they never have art.
//=================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;

namespace Thetis
{
    /// <summary>
    /// Loads VST3 bundle snapshot artwork off the UI thread and caches the
    /// decoded bitmaps. All cached images are owned by this class — callers may
    /// draw them but must never dispose them.
    /// </summary>
    internal static class VstPluginArt
    {
        private const int MaxCachedImages = 64;
        private const string SnapshotMarker = "_snapshot";
        private const int ClassIdLength = 32;

        // Marks a plugin we have already probed and found to have no artwork,
        // so a miss costs one dictionary lookup rather than a directory scan.
        private static readonly Image NoArtSentinel = new Bitmap(1, 1);

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Image> _cache =
            new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> _cacheOrder = new List<string>();
        private static readonly HashSet<string> _pending =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Returns cached artwork for a plugin, or null when none is cached
        /// yet. When nothing is cached and no load is already running, a
        /// background load is started and <paramref name="onLoaded"/> is
        /// invoked on completion (on a thread pool thread) if artwork was
        /// found. Never blocks. The class ID distinguishes plugins that share a
        /// bundle path (WaveShell effects), so each keeps its own artwork.
        /// </summary>
        public static Image GetOrRequest(string pluginPath, string classId, Action onLoaded)
        {
            string key = VstHost.GetPluginArtKey(pluginPath, classId);

            if (string.IsNullOrWhiteSpace(key))
                return null;

            lock (_lock)
            {
                Image cached;
                if (_cache.TryGetValue(key, out cached))
                    return ReferenceEquals(cached, NoArtSentinel) ? null : cached;

                if (_pending.Contains(key))
                    return null;

                _pending.Add(key);
            }

            Task.Run(() =>
            {
                Image loaded = null;

                try
                {
                    loaded = LoadSnapshot(pluginPath, classId);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine("VST snapshot load failed: " + ex.Message);
                }

                bool found;

                lock (_lock)
                {
                    _pending.Remove(key);

                    // A concurrent Clear() may have run while we were decoding;
                    // in that case drop the bitmap rather than repopulating a
                    // cache the owner just tore down.
                    if (_cache.ContainsKey(key))
                    {
                        if (loaded != null)
                            loaded.Dispose();
                        return;
                    }

                    Store(key, loaded ?? NoArtSentinel);
                    found = loaded != null;
                }

                if (found && onLoaded != null)
                    onLoaded();
            });

            return null;
        }

        /// <summary>
        /// Disposes every cached bitmap. Safe to call repeatedly; loads still in
        /// flight discard their results rather than repopulating the cache.
        /// </summary>
        public static void Clear()
        {
            lock (_lock)
            {
                foreach (KeyValuePair<string, Image> entry in _cache)
                {
                    if (!ReferenceEquals(entry.Value, NoArtSentinel) && entry.Value != null)
                        entry.Value.Dispose();
                }

                _cache.Clear();
                _cacheOrder.Clear();
            }
        }

        // Caller must hold _lock.
        private static void Store(string key, Image image)
        {
            while (_cacheOrder.Count >= MaxCachedImages)
            {
                string oldestKey = _cacheOrder[0];
                Image oldest;

                _cacheOrder.RemoveAt(0);

                if (_cache.TryGetValue(oldestKey, out oldest))
                {
                    _cache.Remove(oldestKey);
                    if (!ReferenceEquals(oldest, NoArtSentinel) && oldest != null)
                        oldest.Dispose();
                }
            }

            _cache[key] = image;
            _cacheOrder.Add(key);
        }

        /// <summary>
        /// Drops any cached entry for a plugin so the next request re-reads from
        /// disk. Called after a fresh editor capture is written.
        /// </summary>
        public static void Invalidate(string pluginPath, string classId)
        {
            string key = VstHost.GetPluginArtKey(pluginPath, classId);

            if (string.IsNullOrWhiteSpace(key))
                return;

            lock (_lock)
            {
                Image existing;

                if (!_cache.TryGetValue(key, out existing))
                    return;

                _cache.Remove(key);
                _cacheOrder.Remove(key);

                if (!ReferenceEquals(existing, NoArtSentinel) && existing != null)
                    existing.Dispose();
            }
        }

        private static Image LoadSnapshot(string pluginPath, string classId)
        {
            // A capture of the user's own editor reflects their actual settings,
            // so it wins over the vendor's generic marketing snapshot.
            string snapshotFile = FindCapturedFile(pluginPath, classId) ?? FindSnapshotFile(pluginPath, classId);

            if (snapshotFile == null)
                return null;

            // Decode via a stream copy so the PNG file is not left locked on
            // disk for the lifetime of the cached bitmap, which is what
            // Image.FromFile would do.
            byte[] raw = File.ReadAllBytes(snapshotFile);

            using (MemoryStream stream = new MemoryStream(raw))
            using (Image decoded = Image.FromStream(stream))
            {
                return new Bitmap(decoded);
            }
        }

        private static string FindCapturedFile(string pluginPath, string classId)
        {
            try
            {
                string capturePath = VstEditorCapture.GetCaptureFilePath(pluginPath, classId);

                return capturePath != null && File.Exists(capturePath) ? capturePath : null;
            }
            catch
            {
                return null;
            }
        }

        private static string FindSnapshotFile(string pluginPath, string classId)
        {
            string snapshotsDir = VstHost.GetSnapshotsDirectory(pluginPath);

            if (snapshotsDir == null)
                return null;

            string[] candidates;

            try
            {
                candidates = Directory.GetFiles(snapshotsDir, "*.png");
            }
            catch
            {
                return null;
            }

            if (candidates == null || candidates.Length == 0)
                return null;

            // Prefer the plugin's own class ID so a bundle shipping several
            // plugins (WaveShell) resolves each one's own snapshot. Without a
            // class ID fall back to the bundle's primary audio-module class.
            string classIdToMatch = !string.IsNullOrWhiteSpace(classId)
                ? classId
                : VstHost.TryGetPrimaryClassId(pluginPath);
            string best = null;
            double bestScale = -1.0;
            bool bestMatchesClassId = false;

            for (int i = 0; i < candidates.Length; i++)
            {
                string fileName = Path.GetFileNameWithoutExtension(candidates[i]);

                if (string.IsNullOrEmpty(fileName))
                    continue;

                int markerIndex = fileName.IndexOf(SnapshotMarker, StringComparison.OrdinalIgnoreCase);

                // The SDK requires the marker at exactly offset 32, immediately
                // after the class ID. Anything else is not a snapshot file.
                if (markerIndex != ClassIdLength)
                    continue;

                bool matchesClassId = classIdToMatch != null &&
                    string.Equals(fileName.Substring(0, ClassIdLength), classIdToMatch, StringComparison.OrdinalIgnoreCase);

                // A file naming our own class always beats one that does not,
                // which matters for bundles shipping several plugins.
                if (bestMatchesClassId && !matchesClassId)
                    continue;

                double scale = ParseScaleFactor(fileName.Substring(markerIndex + SnapshotMarker.Length));

                if (matchesClassId && !bestMatchesClassId)
                {
                    best = candidates[i];
                    bestScale = scale;
                    bestMatchesClassId = true;
                    continue;
                }

                // Prefer the highest resolution available; it is downscaled to
                // fit the rack unit, and rack units can be fairly wide.
                if (scale > bestScale)
                {
                    best = candidates[i];
                    bestScale = scale;
                }
            }

            return best;
        }

        /// <summary>
        /// Parses the "_2.0x" style suffix that follows "_snapshot". Returns
        /// 1.0 for the suffix-less default form.
        /// </summary>
        private static double ParseScaleFactor(string suffix)
        {
            if (string.IsNullOrEmpty(suffix))
                return 1.0;

            if (suffix[0] == '_')
                suffix = suffix.Substring(1);

            if (suffix.Length < 2)
                return 1.0;
            if (suffix[suffix.Length - 1] != 'x' && suffix[suffix.Length - 1] != 'X')
                return 1.0;

            double parsed;
            string number = suffix.Substring(0, suffix.Length - 1);

            if (double.TryParse(number, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed) && parsed > 0.0)
                return parsed;

            return 1.0;
        }
    }
}
