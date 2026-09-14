/*  WsjtAudioBridge.cs
 *
 *  Named-pipe client for the WSJT-X AUDIO channels.
 *
 *  Copyright (C) 2026  SDR-VST3 Thetis integration (WSJT-X sidecar)
 *
 *  This program is free software; you can redistribute it and/or
 *  modify it under the terms of the GNU General Public License
 *  as published by the Free Software Foundation; either version 2
 *  of the License, or (at your option) any later version.
 */

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace Thetis.WSJTX
{
    /// <summary>
    /// Drains the down-sampled RX audio from the native module
    /// (WsjtDrainRx) and pushes it into the WSJT-X AUDIO pipe as
    /// 8 kHz mono float (little-endian) -- the wire format the WSJT-X
    /// sidecar's SoundInPipe consumes.  Also drains the WSJT-X TXAUDIO
    /// pipe (8 kHz mono float32 modem audio written by the fork's
    /// SoundOutPipe server) and pushes it back into the native TX FIFO
    /// (WsjtPushTx) while WSJT-X is keyed (MOX mirror).
    /// </summary>
    internal static class WsjtAudioBridge
    {
        private static NamedPipeClientStream _stream;
        private static volatile bool _running;
        private static int _gen;        // incremented on each Start/Stop cycle
        private static Thread _writerThread;
        private static readonly object _writeLock = new object();
        private static readonly float[] _rxBuf = new float[4096];
        private static readonly byte[] _writeBytes = new byte[4096 * 4];

        private const int DRAIN_BATCH = 2048;   // samples per WsjtDrainRx call

        // TX pipe reader (P2): the sidecar *writes* 8 kHz mono float32 modem
        // audio into the TXAUDIO pipe; the managed client reads it here and
        // pushes it back into the native TX FIFO (WsjtPushTx), where the
        // native tap upsamples to the mic rate while WSJT-X is keyed (MOX).
        private static NamedPipeClientStream _txStream;
        private static Thread _txReaderThread;
        private static readonly float[] _txBuf = new float[4096];
        private static readonly byte[] _txReadBytes = new byte[4096 * 4];
        private static volatile bool _txRunning;

        public static void Start(int pid)
        {
            Stop();                              // tear down any prior session
            if (pid <= 0) return;
            _gen++;
            _running = true;
            var stream = new NamedPipeClientStream(
                ".",
                WsjtManager.AudioPipeName(pid),
                PipeDirection.Out,
                PipeOptions.Asynchronous);

            _stream = stream;

            bool connected = false;
            try
            {
                stream.Connect(3000);
                connected = true;
            }
            catch (TimeoutException)
            {
                Trace.WriteLine("WSJT-AUDIO: pipe connect timed out");
            }
            catch (IOException ex)
            {
                Trace.WriteLine("WSJT-AUDIO: pipe connect error: " + ex.Message);
            }
            if (!connected)
            {
                try { stream.Dispose(); } catch { }
                _stream = null;
                return;
            }
            Trace.WriteLine(string.Format("WSJT-AUDIO: connected (pid {0})", pid));

            _writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "wsjtx-audio-wr" };
            _writerThread.Start();

            StartTx(pid);
        }

        private static void StartTx(int pid)
        {
            var tx = new NamedPipeClientStream(
                ".",
                WsjtManager.TxAudioPipeName(pid),
                PipeDirection.In,
                PipeOptions.Asynchronous);
            _txStream = tx;

            bool connected = false;
            try
            {
                tx.Connect(3000);
                connected = true;
            }
            catch (TimeoutException)
            {
                Trace.WriteLine("WSJT-TXAUDIO: pipe connect timed out");
            }
            catch (IOException ex)
            {
                Trace.WriteLine("WSJT-TXAUDIO: pipe connect error: " + ex.Message);
            }
            if (!connected)
            {
                try { tx.Dispose(); } catch { }
                _txStream = null;
                return;
            }
            Trace.WriteLine(string.Format("WSJT-TXAUDIO: connected (pid {0})", pid));

            _txRunning = true;
            _txReaderThread = new Thread(TxReaderLoop) { IsBackground = true, Name = "wsjtx-audio-tx" };
            _txReaderThread.Start();
        }

        private static void WriterLoop()
        {
            int gen = _gen;
            while (_running)
            {
                if (_gen != gen) return;
                var s = _stream;
                if (s == null || !s.IsConnected)
                {
                    Thread.Sleep(20);
                    continue;
                }
                try
                {
                    int n = cmaster.WsjtDrainRx(0, _rxBuf, DRAIN_BATCH);
                    if (n > 0)
                    {
                        Buffer.BlockCopy(_rxBuf, 0, _writeBytes, 0, n * 4);
                        lock (_writeLock)
                        {
                            if (_stream != null && _stream.IsConnected)
                                _stream.Write(_writeBytes, 0, n * 4);
                        }
                    }
                }
                catch (ObjectDisposedException) { return; }
                catch (IOException ex)
                {
                    Trace.WriteLine("WSJT-AUDIO: write exception: " + ex.Message);
                    return;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("WSJT-AUDIO: writer error: " + ex.Message);
                }
                Thread.Sleep(4);
            }
        }

        private static void TxReaderLoop()
        {
            int gen = _gen;
            while (_txRunning && _gen == gen)
            {
                var tx = _txStream;
                if (tx == null || !tx.IsConnected)
                {
                    Thread.Sleep(20);
                    continue;
                }
                try
                {
                    int got = tx.Read(_txReadBytes, 0, _txReadBytes.Length);
                    if (got <= 0) { _txRunning = false; return; }   // EOF (server gone)
                    int n = got / 4;
                    Buffer.BlockCopy(_txReadBytes, 0, _txBuf, 0, got);
                    if (n > 0)
                        cmaster.WsjtPushTx(_txBuf, n);
                }
                catch (ObjectDisposedException) { return; }
                catch (IOException ex)
                {
                    Trace.WriteLine("WSJT-TXAUDIO: read exception: " + ex.Message);
                    _txRunning = false;
                    return;
                }
                catch (Exception ex)
                {
                    Trace.WriteLine("WSJT-TXAUDIO: reader error: " + ex.Message);
                }
                Thread.Sleep(4);
            }
        }

        public static void Stop()
        {
            _running = false;
            _gen++;
            var s = _stream;
            _stream = null;
            if (s != null)
            {
                try { s.Close(); } catch { }
                try { s.Dispose(); } catch { }
            }
            var t = _writerThread;
            _writerThread = null;
            if (t != null)
            {
                try { t.Join(200); } catch { }
            }

            _txRunning = false;
            var tx = _txStream;
            _txStream = null;
            if (tx != null)
            {
                try { tx.Close(); } catch { }
                try { tx.Dispose(); } catch { }
            }
            var tt = _txReaderThread;
            _txReaderThread = null;
            if (tt != null)
            {
                try { tt.Join(200); } catch { }
            }
            Trace.WriteLine("WSJT-AUDIO: stopped");
        }
    }
}