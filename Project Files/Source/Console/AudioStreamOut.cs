/*  AudioStreamOut.cs

    Streaming Output feature.

    Taps the post-VST RX and TX audio in cmaster.cs (OnVstRxProcess / OnVstTxProcess)
    and plays it out through a user-chosen Windows audio output device, so that
    applications such as OBS Studio can pick the radio audio up without needing a
    virtual audio cable.

    Two completely separate streams are provided (RX and TX), each with its own
    enable switch and device selection.  This is a copy-only tap: the normal radio
    audio paths are never touched.

    Design:
      - Producer = the native audio callback thread (PortAudio).  It converts the
        interleaved stereo doubles to float bytes and writes them into a thread-safe
        BufferedWaveProvider.  It never blocks, never allocates, and never calls into
        the audio device.
      - Consumer = the NAudio WasapiOut (shared mode) or WaveOutEvent (MME) playback
        thread, which reads from the provider.
      - The provider feeds a WdlResamplingSampleProvider when the device rate differs.
      - A maintenance timer (background) recreates the playback chain when the source
        rate changes or the device faults/goes missing.

    Settings are managed from the Setup form "Streaming" tab and persisted in the
    Options database via the normal TS-control save machinery.
*/

using System;
using System.Globalization;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Thetis
{
    public static class AudioStreamOut
    {
        public enum StreamId { Rx = 0, Tx = 1 }

        private const int PcAudioWasapiOutputIdBase = 200000;   // must match clsAudioRecordPlayback
        private const int MaintenancePeriodMs = 2000;

        private static readonly StreamSink _rx = new StreamSink(StreamId.Rx);
        private static readonly StreamSink _tx = new StreamSink(StreamId.Tx);

        private static Timer _maintenanceTimer;
        private static int _shutdown;

        private sealed class StreamSink
        {
            public readonly object Gate = new object();

            public volatile bool Enabled;
            public int DeviceId;
            public float Volume = 1f;
            public bool Faulted;

            public string LastError;
            public long LastFeedMs;
            public float Peak;

            public BufferedWaveProvider Buffer;
            public IWaveProvider Provider;
            public IWavePlayer Player;

            public int SourceRate;
            public int PendingSourceRate;
            public bool RestartPending;

            public byte[] Scratch;

            public readonly StreamId Id;

            public StreamSink(StreamId id)
            {
                Id = id;
            }
        }

        public static bool IsEnabled(StreamId id)
        {
            StreamSink s = id == StreamId.Rx ? _rx : _tx;
            return s != null && s.Enabled;
        }

        // Peak audio level of the last captured block, 0..1 (post volume, linear).
        public static float GetLevel(StreamId id)
        {
            StreamSink s = id == StreamId.Rx ? _rx : _tx;
            lock (s.Gate)
            {
                return s.Enabled ? s.Peak : 0f;
            }
        }

        // Human-readable live status for the Setup form Streaming tab.
        public static string StreamStatus(StreamId id)
        {
            StreamSink s = id == StreamId.Rx ? _rx : _tx;

            lock (s.Gate)
            {
                if (!s.Enabled) return "OFF";

                if (s.Faulted) return "FAULTED (retrying) - " + (s.LastError ?? "playback device failed");

                if (s.Player == null || s.Buffer == null) return "starting...";

                string rate = (s.SourceRate > 0 ? s.SourceRate : 48000).ToString(CultureInfo.InvariantCulture);

                if (s.LastFeedMs == 0 || (int)(Environment.TickCount - s.LastFeedMs) > 1500)
                    return "ON @ " + rate + " Hz - no tap audio (is the radio receiving?)";

                return "ON @ " + rate + " Hz";
            }
        }

        // Called from the native audio callback thread.  Must be fast, non-blocking
        // and allocation-free.  OnVstRxProcess / OnVstTxProcess wrap this in try/catch.
        public static unsafe void FeedRx(double* buffer, int frames, int rate)
        {
            Feed(_rx, buffer, frames, rate);
        }

        public static unsafe void FeedTx(double* buffer, int frames, int rate)
        {
            Feed(_tx, buffer, frames, rate);
        }

        private static unsafe void Feed(StreamSink sink, double* buffer, int frames, int rate)
        {
            lock (sink.Gate)
            {
                if (!sink.Enabled) return;

                float vol = sink.Volume;
                if (vol <= 0.0001f)
                {
                    // muted: skip conversion, but keep the meter alive so the
                    // status line shows "level 0.00" rather than "no tap audio"
                    sink.Peak = 0f;
                    sink.LastFeedMs = Environment.TickCount;
                    return;
                }

                if (rate > 0 && rate != sink.SourceRate)
                {
                    // source sample rate changed: the maintenance timer rebuilds at the new rate
                    sink.PendingSourceRate = rate;
                    sink.RestartPending = true;
                }

                BufferedWaveProvider buf = sink.Buffer;
                if (buf == null) return;

                int samples = frames * 2;                       // interleaved stereo
                int bytes = samples * 4;

                byte[] scratch = sink.Scratch;
                if (scratch == null || scratch.Length < bytes)
                {
                    sink.Scratch = scratch = new byte[bytes];
                }

                fixed (byte* p = scratch)
                {
                    float* f = (float*)p;
                    float peak = 0f;
                    for (int i = 0; i < samples; i++)
                    {
                        float v = (float)buffer[i] * vol;
                        f[i] = v;
                        if (v < 0f) v = -v;
                        if (v > peak) peak = v;
                    }
                    sink.Peak = peak;
                    sink.LastFeedMs = Environment.TickCount;
                }

                try
                {
                    buf.AddSamples(scratch, 0, bytes);
                }
                catch
                {
                }
            }
        }

        // Applies the settings from the Setup form Streaming tab.  Volume is a linear
        // amplitude gain (0..N, 1 = unity) and only affects these stream outputs - the
        // normal radio audio paths are untouched.
        public static void ApplyConfig(bool rxEnable, int rxDeviceId, float rxVolume, bool txEnable, int txDeviceId, float txVolume)
        {
            ApplyConfig(_rx, rxEnable, rxDeviceId, rxVolume);
            ApplyConfig(_tx, txEnable, txDeviceId, txVolume);
            EnsureMaintenanceTimer();
        }

        private static void ApplyConfig(StreamSink sink, bool enable, int deviceId, float volume)
        {
            lock (sink.Gate)
            {
                bool configChanged = sink.Enabled != enable || sink.DeviceId != deviceId;
                if (!configChanged && Math.Abs(sink.Volume - volume) < 0.001f && !sink.RestartPending)
                    return;                                     // nothing has changed

                sink.Volume = volume;

                if (configChanged)
                {
                    sink.Enabled = enable;
                    sink.DeviceId = deviceId;
                    sink.Faulted = false;
                    sink.LastError = null;
                    sink.SourceRate = 0;
                    sink.PendingSourceRate = 0;
                    sink.Buffer = null;                         // stop feeding until rebuilt
                    sink.RestartPending = true;
                }
            }

            if (!enable)
            {
                Teardown(sink);
            }
        }

        private static void EnsureMaintenanceTimer()
        {
            if (_maintenanceTimer != null) return;
            if (Interlocked.CompareExchange(ref _shutdown, 0, 0) != 0) return;

            Timer t = new Timer(MaintenanceTick, null, MaintenancePeriodMs, MaintenancePeriodMs);
            if (Interlocked.CompareExchange(ref _maintenanceTimer, t, null) != null)
                t.Dispose();
        }

        private static void MaintenanceTick(object state)
        {
            if (Interlocked.CompareExchange(ref _shutdown, 0, 0) != 0) return;

            Maintain(_rx);
            Maintain(_tx);
        }

        private static void Maintain(StreamSink sink)
        {
            bool needsRestart = false;

            lock (sink.Gate)
            {
                if (!sink.Enabled) return;

                if (sink.RestartPending || sink.Faulted || sink.Player == null)
                {
                    // recreate the playback chain (also retries after a fault / missing device)
                    sink.Buffer = null;
                    sink.RestartPending = true;
                    needsRestart = true;
                }
            }

            if (needsRestart)
            {
                Teardown(sink);
                TryStart(sink);
            }
        }

        private static void TryStart(StreamSink sink)
        {
            int deviceId;
            int sourceRate;

            lock (sink.Gate)
            {
                if (!sink.Enabled) return;
                if (sink.Player != null) return;

                deviceId = sink.DeviceId;
                sourceRate = sink.PendingSourceRate > 0 ? sink.PendingSourceRate : (sink.SourceRate > 0 ? sink.SourceRate : 48000);
                sink.RestartPending = false;
            }

            if (sourceRate <= 0 || sourceRate > 384000) sourceRate = 48000;

            IWavePlayer player = null;
            BufferedWaveProvider buffer = null;

            try
            {
                buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(sourceRate, 2));
                buffer.BufferDuration = TimeSpan.FromSeconds(2);
                buffer.DiscardOnBufferOverflow = true;

                int deviceRate;
                bool wantFloat;

                if (deviceId >= PcAudioWasapiOutputIdBase)
                {
                    deviceRate = 48000;
                    wantFloat = true;
                    GetDeviceMixFormat(deviceId - PcAudioWasapiOutputIdBase, ref deviceRate, ref wantFloat);
                }
                else
                {
                    deviceRate = 48000;
                    wantFloat = false;
                }

                IWaveProvider provider = BuildProvider(buffer, sourceRate, deviceRate, wantFloat);
                player = CreatePlayer(deviceId, provider);

                lock (sink.Gate)
                {
                    if (!sink.Enabled || sink.DeviceId != deviceId)
                    {
                        // configuration changed while we were opening
                        DisposeSafe(player);
                        return;
                    }

                    sink.SourceRate = sourceRate;
                    sink.PendingSourceRate = 0;
                    sink.Buffer = buffer;
                    sink.Provider = provider;
                    sink.Player = player;
                    sink.Faulted = false;
                    sink.LastError = null;
                }

                if (player is WasapiOut w)
                    w.PlaybackStopped += OnPlaybackStopped;
                else if (player is WaveOutEvent we)
                    we.PlaybackStopped += OnPlaybackStopped;

                player.Play();
            }
            catch (Exception ex)
            {
                DisposeSafe(player);
                buffer = null;   // not disposable; let the GC reclaim it

                lock (sink.Gate)
                {
                    sink.Buffer = null;
                    sink.Provider = null;
                    sink.Player = null;
                    sink.Faulted = sink.Enabled;
                    sink.RestartPending = sink.Enabled;
                    sink.LastError = ex.Message;
                }

                LogStartFailure(sink.Id, ex.Message);
            }
        }

        private static void OnPlaybackStopped(object sender, StoppedEventArgs e)
        {
            StreamSink sink = ReferenceEquals(sender, _rx.Player) ? _rx : _tx;

            lock (sink.Gate)
            {
                if (!sink.Enabled) return;
                if (sink.Player != sender) return;

                // playback chain went away (device removed / error) - rebuild on the maintenance timer
                sink.Player = null;
                sink.Provider = null;
                sink.Buffer = null;
                sink.Faulted = true;
                sink.RestartPending = true;
            }
        }

        private static void GetDeviceMixFormat(int deviceIndex, ref int deviceRate, ref bool wantFloat)
        {
            try
            {
                using (MMDeviceEnumerator en = new MMDeviceEnumerator())
                {
                    MMDeviceCollection devs = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                    if (deviceIndex < 0 || deviceIndex >= devs.Count) return;

                    MMDevice dev = devs[deviceIndex];
                    WaveFormat mf = dev.AudioClient.MixFormat;
                    if (mf != null)
                    {
                        if (mf.SampleRate > 0) deviceRate = mf.SampleRate;
                        wantFloat = mf.Encoding == WaveFormatEncoding.IeeeFloat;
                    }
                }
            }
            catch
            {
            }
        }

        private static IWaveProvider BuildProvider(BufferedWaveProvider buffer, int sourceRate, int deviceRate, bool wantFloat)
        {
            ISampleProvider samples = buffer.ToSampleProvider();
            if (deviceRate > 0 && deviceRate != sourceRate)
            {
                samples = new WdlResamplingSampleProvider(samples, deviceRate);
            }
            return wantFloat ? samples.ToWaveProvider() : samples.ToWaveProvider16();
        }

        private static IWavePlayer CreatePlayer(int deviceId, IWaveProvider provider)
        {
            if (deviceId >= PcAudioWasapiOutputIdBase)
            {
                int index = deviceId - PcAudioWasapiOutputIdBase;
                MMDeviceEnumerator en = new MMDeviceEnumerator();
                MMDeviceCollection devs = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                if (index < 0 || index >= devs.Count)
                    throw new IndexOutOfRangeException("WASAPI output device index is out of range.");

                MMDevice dev = devs[index];
                WasapiOut wasapi = new WasapiOut(dev, AudioClientShareMode.Shared, false, 200);
                wasapi.Init(provider);
                return wasapi;
            }
            else
            {
                WaveOutEvent waveOut = new WaveOutEvent();
                waveOut.DeviceNumber = deviceId;
                waveOut.Init(provider);
                return waveOut;
            }
        }

        private static void Teardown(StreamSink sink)
        {
            IWavePlayer player = null;

            lock (sink.Gate)
            {
                player = sink.Player;
                sink.Player = null;
                sink.Provider = null;
                sink.Buffer = null;
            }

            if (player is WasapiOut wasapi)
                wasapi.PlaybackStopped -= OnPlaybackStopped;
            else if (player is WaveOutEvent waveOut)
                waveOut.PlaybackStopped -= OnPlaybackStopped;

            DisposeSafe(player);
        }

        public static void Shutdown()
        {
            if (Interlocked.Exchange(ref _shutdown, 1) != 0) return;

            Timer t = _maintenanceTimer;
            _maintenanceTimer = null;
            if (t != null)
            {
                try { t.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
                t.Dispose();
            }

            lock (_rx.Gate) _rx.Enabled = false;
            lock (_tx.Gate) _tx.Enabled = false;

            Teardown(_rx);
            Teardown(_tx);
        }

        private static void DisposeSafe(IDisposable d)
        {
            if (d == null) return;
            try { d.Dispose(); } catch { }
        }

        private static void LogStartFailure(StreamId id, string message)
        {
            try
            {
                LogTool.AddLogEntry("Streaming " + (id == StreamId.Rx ? "RX" : "TX") + " output could not start: " + message, "STREAMOUT");
            }
            catch
            {
            }
        }
    }
}