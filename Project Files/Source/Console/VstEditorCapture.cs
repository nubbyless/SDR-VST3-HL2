//=================================================================
// VstEditorCapture.cs
//=================================================================
// Captures a screenshot of a plugin's editor window so plugins that ship no
// vendor snapshot still get artwork in the rack view.
//
// The editor window lives in VstAudioHost.exe, not in Thetis.exe — live plugin
// hosting is deliberately out of process. We do NOT load the plugin here to
// take a picture; that would put third-party code back inside the radio
// process, which is exactly what the out-of-process design exists to prevent.
//
// Instead this finds the window the host already created and asks Windows to
// render it. The host's editor windows are identifiable by window class:
//     VST3 -> ThetisVstEditorHostWindow   (vst_runtime.cpp)
// and their title is the plugin's own name, so a specific plugin's editor can
// be singled out even with several editors open.
//
// Captures are keyed by class ID (or a path hash when there is none) and
// written to the "art" cache directory, using the same lookup VstPluginArt
// applies to vendor snapshots. A future batch-capture pass in the scanner can
// populate the same directory without changing anything here.
//=================================================================

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Thetis
{
    internal static class VstEditorCapture
    {
        private const string Vst3EditorWindowClass = "ThetisVstEditorHostWindow";

        // PrintWindow flag that renders DWM-composited content. Without it many
        // modern plugin GUIs come back as an empty frame.
        private const uint PW_RENDERFULLCONTENT = 2;

        private const int MinimumUsefulDimension = 32;

        #region Native

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder buffer, int maxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        #endregion

        /// <summary>
        /// Returns the cache file path for a plugin's captured artwork, or null
        /// when the cache directory is unavailable. The class ID is the
        /// distinguishing key so plugins that share a bundle path (WaveShell
        /// effects) each keep their own capture.
        /// </summary>
        public static string GetCaptureFilePath(string pluginPath, string classId)
        {
            string artDir = VstHost.GetCapturedArtDirectory();

            if (artDir == null)
                return null;

            string key = VstHost.GetPluginArtKey(pluginPath, classId);

            return key == null ? null : Path.Combine(artDir, key + ".png");
        }

        /// <summary>
        /// Finds the open editor window for <paramref name="plugin"/> and writes
        /// a PNG of it to the art cache. Returns the file path on success, or
        /// null if the window could not be found or produced a blank image.
        /// Safe to call from a background thread; does no plugin loading.
        /// </summary>
        public static string TryCaptureEditor(VstPluginState plugin)
        {
            if (plugin == null || string.IsNullOrWhiteSpace(plugin.Path))
                return null;

            string outputPath = GetCaptureFilePath(plugin.Path, plugin.ClassId);

            if (outputPath == null)
                return null;

            IntPtr hwnd = FindEditorWindow(plugin);

            if (hwnd == IntPtr.Zero)
                return null;

            try
            {
                using (Bitmap bitmap = CaptureWindow(hwnd))
                {
                    if (bitmap == null)
                        return null;

                    // A plugin drawing through the GPU (WebView/Chromium, some
                    // OpenGL editors) commonly yields an empty frame. Caching
                    // that would replace a readable placeholder with a black
                    // box, so treat it as a failure.
                    if (IsEffectivelyBlank(bitmap))
                    {
                        System.Diagnostics.Trace.WriteLine(
                            "VST editor capture produced a blank image for " + plugin.Path);
                        return null;
                    }

                    SaveAtomically(bitmap, outputPath);
                }

                return outputPath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("VST editor capture failed: " + ex.Message);
                return null;
            }
        }

        private static IntPtr FindEditorWindow(VstPluginState plugin)
        {
            string wantedClass = Vst3EditorWindowClass;
            string wantedTitle = VstHost.GetPluginDisplayName(plugin);

            IntPtr match = IntPtr.Zero;
            IntPtr classOnlyMatch = IntPtr.Zero;

            EnumWindows(delegate(IntPtr hwnd, IntPtr param)
            {
                if (!IsWindowVisible(hwnd))
                    return true;

                StringBuilder className = new StringBuilder(256);
                GetClassName(hwnd, className, className.Capacity);

                if (!string.Equals(className.ToString(), wantedClass, StringComparison.Ordinal))
                    return true;

                StringBuilder title = new StringBuilder(512);
                GetWindowText(hwnd, title, title.Capacity);

                // The host titles the editor with the plugin's own name, so
                // prefer an exact title match when several editors are open.
                if (string.Equals(title.ToString(), wantedTitle, StringComparison.OrdinalIgnoreCase))
                {
                    match = hwnd;
                    return false;
                }

                if (classOnlyMatch == IntPtr.Zero)
                    classOnlyMatch = hwnd;

                return true;
            }, IntPtr.Zero);

            return match != IntPtr.Zero ? match : classOnlyMatch;
        }

        private static Bitmap CaptureWindow(IntPtr hwnd)
        {
            NativeRect rect;

            if (!GetWindowRect(hwnd, out rect))
                return null;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;

            if (width < MinimumUsefulDimension || height < MinimumUsefulDimension)
                return null;

            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);

            try
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    IntPtr hdc = graphics.GetHdc();

                    try
                    {
                        if (!PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT))
                        {
                            // Older/odd windows may only answer the plain form.
                            if (!PrintWindow(hwnd, hdc, 0))
                            {
                                graphics.ReleaseHdc(hdc);
                                hdc = IntPtr.Zero;
                                bitmap.Dispose();
                                return null;
                            }
                        }
                    }
                    finally
                    {
                        if (hdc != IntPtr.Zero)
                            graphics.ReleaseHdc(hdc);
                    }
                }

                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Samples a coarse grid and reports whether the image carries any real
        /// variation. Catches both all-black GPU failures and uniformly filled
        /// frames without walking every pixel.
        /// </summary>
        private static bool IsEffectivelyBlank(Bitmap bitmap)
        {
            const int steps = 16;

            int stepX = Math.Max(1, bitmap.Width / steps);
            int stepY = Math.Max(1, bitmap.Height / steps);

            int minLuma = int.MaxValue;
            int maxLuma = int.MinValue;

            for (int y = 0; y < bitmap.Height; y += stepY)
            {
                for (int x = 0; x < bitmap.Width; x += stepX)
                {
                    Color pixel = bitmap.GetPixel(x, y);
                    int luma = (pixel.R * 30 + pixel.G * 59 + pixel.B * 11) / 100;

                    if (luma < minLuma) minLuma = luma;
                    if (luma > maxLuma) maxLuma = luma;
                }
            }

            if (minLuma == int.MaxValue)
                return true;

            // Near-uniform in either direction means nothing was rendered.
            return (maxLuma - minLuma) < 8;
        }

        private static void SaveAtomically(Bitmap bitmap, string outputPath)
        {
            string tempPath = outputPath + ".tmp";

            bitmap.Save(tempPath, ImageFormat.Png);

            try
            {
                if (File.Exists(outputPath))
                    File.Delete(outputPath);

                File.Move(tempPath, outputPath);
            }
            catch
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                }

                throw;
            }
        }
    }
}
