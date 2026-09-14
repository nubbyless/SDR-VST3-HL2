/*  WsjtCtlBridge.cs
 *
 *  Async duplex named-pipe client for the WSJT-X CTL channel.
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
    /// Newline-delimited JSON CTL channel to the WSJT-X sidecar.
    ///
    /// WSJT-X is the master: it polls `{"msg":"get_status"}` every 500 ms
    /// and issues set_freq / set_mode / set_txfreq / set_ptt to the rig
    /// (Thetis).  This bridge answers each poll with a `status` line holding
    /// the current radio rx frequency (Hz), mode and PTT, and applies the
    /// set_* commands to the VFO/MOX the same way a CAT rig would.
    ///
    /// Same async-read + locked-write pattern as FldigiControlBridge so a
    /// blocking read is never outstanding while the pipe is written to.
    /// </summary>
    internal static class WsjtCtlBridge
    {
        private static NamedPipeClientStream _stream;
        private static volatile bool _running;
        private static int _gen;
        private static CancellationTokenSource _cts;
        private static readonly object _writeLock = new object();

        private static readonly object _stateLock = new object();
        // Last radio state actually applied to the SDR (the receive mirror of
        // what WSJT-X commanded).  Used to dedupe echo statuses.
        private static long _lastAppliedFreqHz;
        private static bool _lastAppliedPtt;

        private static Console.MoxChanged _moxHandler;
        private static Console.VFOAFrequencyChanged _vfoaHandler;
        private static bool _handlersWired;

        private const int READ_LEN = 4096;

        static WsjtCtlBridge()
        {
            // Thetis dial / MOX changed locally (band button, direct tune,
            // TX button): push the resulting status to WSJT-X promptly rather
            // than waiting for the next 500 ms poll.  Both fire on the UI
            // thread; Send() is a short buffered write so that is acceptable.
            _moxHandler = (rx, oldMox, newMox) =>
            {
                if (_running) PushRadioState();
            };
            _vfoaHandler = (oldBand, newBand, oldMode, newMode, oldFilter, newFilter,
                            oldFreq, newFreq, oldCentreF, newCentreF,
                            oldCTUN, newCTUN, oldZoom, newZoom, offset, rx) =>
            {
                if (_running) PushRadioState();
            };
        }

        public static void Start(int pid)
        {
            Stop();                              // tear down any prior session
            if (pid <= 0) return;
            _gen++;
            _running = true;
            _cts = new CancellationTokenSource();

            var stream = new NamedPipeClientStream(
                ".",
                WsjtManager.CtlPipeName(pid),
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            _stream = stream;

            // The sidecar creates the CTL pipe when its rig layer opens,
            // which trails process launch by a beat.  Retry like the fldigi
            // control bridge does.
            bool connected = false;
            for (int i = 0; i < 50 && _running; i++)
            {
                try
                {
                    stream.Connect(100);
                    connected = true;
                    break;
                }
                catch (TimeoutException) { }
                catch (IOException) { break; }
                catch { break; }
                Thread.Sleep(100);
            }
            if (!connected)
            {
                Trace.WriteLine("WSJT-CTL: pipe connect failed");
                try { stream.Dispose(); } catch { }
                _stream = null;
                return;
            }
            Trace.WriteLine(string.Format("WSJT-CTL: connected (pid {0})", pid));

            WireConsole();

            // Baseline: report the radio as it is right now so WSJT-X's
            // frequency display comes alive immediately.
            _lastAppliedFreqHz = 0;
            _lastAppliedPtt = false;
            PushRadioState();

            var _ = ReaderLoop(_cts.Token, _gen);
        }

        public static void Stop()
        {
            _running = false;
            _gen++;
            var cts = _cts;
            _cts = null;
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
            }
            UnwireConsole();
            var s = _stream;
            _stream = null;
            if (s != null)
            {
                try { s.Close(); } catch { }
                try { s.Dispose(); } catch { }
            }
            lock (_stateLock)
            {
                _lastAppliedFreqHz = 0;
                _lastAppliedPtt = false;
            }
            Trace.WriteLine("WSJT-CTL: stopped");
        }

        /// <summary>Current radio rx frequency in Hz, resolved on the UI thread.</summary>
        private static long CurrentFreqHz()
        {
            long hz = 0;
            var c = Console.getConsole();
            if (c == null || c.IsDisposed) return hz;
            try
            {
                if (c.InvokeRequired)
                    c.Invoke((Action)(() => { hz = (long)Math.Round(c.VFOAFreq * 1e6); }));
                else
                    hz = (long)Math.Round(c.VFOAFreq * 1e6);
            }
            catch { }
            return hz;
        }

        /// <summary>Current MOX state, resolved on the UI thread.</summary>
        private static bool CurrentPtt()
        {
            bool ptt = false;
            var c = Console.getConsole();
            if (c == null || c.IsDisposed) return ptt;
            try
            {
                if (c.InvokeRequired)
                    c.Invoke((Action)(() => { ptt = c.MOX; }));
                else
                    ptt = c.MOX;
            }
            catch { }
            return ptt;
        }

        /// <summary>Current RX DSP mode in rig-record form, UI thread.</summary>
        private static string CurrentModeName()
        {
            string m = "";
            var c = Console.getConsole();
            if (c == null || c.IsDisposed) return m;
            try
            {
                if (c.InvokeRequired)
                    c.Invoke((Action)(() => { m = MapDSPModeToRig(c.RX1DSPMode); }));
                else
                    m = MapDSPModeToRig(c.RX1DSPMode);
            }
            catch { }
            return m;
        }

        private static void PushRadioState()
        {
            long hz = CurrentFreqHz();
            bool ptt = CurrentPtt();
            string mode = CurrentModeName();

            bool changed;
            lock (_stateLock)
            {
                changed = (hz != _lastAppliedFreqHz || ptt != _lastAppliedPtt);
                _lastAppliedFreqHz = hz;
                _lastAppliedPtt = ptt;
            }
            if (!changed) return;

            Send(string.Format("{{\"msg\":\"status\",\"rf\":{0},\"mode\":\"{1}\",\"ptt\":{2}}}",
                               hz, EscapeJson(mode), ptt ? "true" : "false"));
        }

        private static void HandleIncoming(string line)
        {
            try
            {
                var o = Newtonsoft.Json.Linq.JObject.Parse(line);
                string msg = (string)o["msg"];
                if (string.IsNullOrEmpty(msg)) return;

                switch (msg)
                {
                    case "get_status":
                        PushRadioState();          // echo current radio state
                        break;
                    case "set_freq":
                        {
                            double rf = (double)(o["rf"] ?? 0);
                            if (rf > 0)
                                ApplyRadioFreq((long)rf);
                            break;
                        }
                    case "set_txfreq":
                        {
                            double rf = (double)(o["rf"] ?? 0);
                            if (rf > 0)
                                ApplyRadioTxFreq((long)rf);
                            break;
                        }
                    case "set_mode":
                        {
                            string mode = (string)o["mode"];
                            if (!string.IsNullOrEmpty(mode))
                                ApplyRadioMode(mode);
                            break;
                        }
                    case "set_ptt":
                        {
                            bool on = (bool)(o["on"] ?? false);
                            ApplyRadioMox(on);
                            break;
                        }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("WSJT-CTL: parse error: " + ex.Message);
            }
        }

        private static void ApplyRadioFreq(long hz)
        {
            lock (_stateLock) _lastAppliedFreqHz = hz;
            try
            {
                var c = Console.getConsole();
                if (c == null || c.IsDisposed) return;
                if (c.InvokeRequired)
                    c.BeginInvoke((Action)(() => SetRadioFreq(c, hz)));
                else
                    SetRadioFreq(c, hz);
            }
            catch { }
        }

        private static void SetRadioFreq(Console c, long hz)
        {
            if (c == null || c.IsDisposed) return;
            try { c.VFOAFreq = hz / 1e6; } catch { }
        }

        private static void ApplyRadioTxFreq(long hz)
        {
            try
            {
                var c = Console.getConsole();
                if (c == null || c.IsDisposed) return;
                if (c.InvokeRequired)
                    c.BeginInvoke((Action)(() => SetRadioTxFreq(c, hz)));
                else
                    SetRadioTxFreq(c, hz);
            }
            catch { }
        }

        private static void SetRadioTxFreq(Console c, long hz)
        {
            if (c == null || c.IsDisposed) return;
            // Split operating: WSJT-X reports the transmit frequency on VFO B.
            try { c.VFOBFreq = hz / 1e6; } catch { }
        }

        private static void ApplyRadioMode(string mode)
        {
            try
            {
                var c = Console.getConsole();
                if (c == null || c.IsDisposed) return;
                DSPMode m = MapRigModeToDSPMode(mode);
                if (m == DSPMode.FIRST) return;
                if (c.InvokeRequired)
                    c.BeginInvoke((Action)(() => SetRadioMode(c, m)));
                else
                    SetRadioMode(c, m);
            }
            catch { }
        }

        private static void SetRadioMode(Console c, DSPMode m)
        {
            if (c == null || c.IsDisposed) return;
            try { if (c.RX1DSPMode != m) c.RX1DSPMode = m; } catch { }
        }

        private static void ApplyRadioMox(bool on)
        {
            lock (_stateLock) _lastAppliedPtt = on;
            try
            {
                var c = Console.getConsole();
                if (c == null || c.IsDisposed) return;
                if (c.InvokeRequired)
                    c.BeginInvoke((Action)(() => SetRadioMox(c, on)));
                else
                    SetRadioMox(c, on);
            }
            catch { }
        }

        private static void SetRadioMox(Console c, bool on)
        {
            if (c == null || c.IsDisposed) return;
            try { cmaster.SetWsjtMoxState(on ? 1 : 0); } catch { }
            try { if (c.MOX != on) c.MOX = on; } catch { }
        }

        private static void WireConsole()
        {
            if (_handlersWired) return;
            var c = Console.getConsole();
            if (c == null) return;
            try
            {
                c.MoxChangeHandlers += _moxHandler;
                c.VFOAFrequencyChangeHandlers += _vfoaHandler;
                _handlersWired = true;
            }
            catch { }
        }

        private static void UnwireConsole()
        {
            if (!_handlersWired) return;
            var c = Console.getConsole();
            if (c != null)
            {
                try
                {
                    c.MoxChangeHandlers -= _moxHandler;
                    c.VFOAFrequencyChangeHandlers -= _vfoaHandler;
                }
                catch { }
            }
            _handlersWired = false;
        }

        private static string MapDSPModeToRig(DSPMode m)
        {
            switch (m)
            {
                case DSPMode.LSB: return "LSB";
                case DSPMode.USB: return "USB";
                case DSPMode.DSB: return "DSB";
                case DSPMode.CWL: return "CWR";
                case DSPMode.CWU: return "CW";
                case DSPMode.FM: return "FM";
                case DSPMode.AM:
                case DSPMode.SAM: return "AM";
                case DSPMode.DIGL: return "DIGL";
                case DSPMode.DIGU:
                case DSPMode.DRM:
                default: return "DIGU";
            }
        }

        private static DSPMode MapRigModeToDSPMode(string s)
        {
            if (string.IsNullOrEmpty(s)) return DSPMode.FIRST;
            string u = s.Trim().ToUpperInvariant();
            switch (u)
            {
                case "USB": return DSPMode.USB;
                case "LSB": return DSPMode.LSB;
                case "AM": return DSPMode.AM;
                case "FM": return DSPMode.FM;
                case "CW": return DSPMode.CWU;
                case "CWR": return DSPMode.CWL;
                case "DIGU": return DSPMode.DIGU;
                case "DIGL": return DSPMode.DIGL;
                default: return DSPMode.FIRST;
            }
        }

        private static string EscapeJson(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static void Send(string json)
        {
            if (!_running) return;
            var s = _stream;
            if (s == null || !s.IsConnected) return;
            byte[] data = System.Text.Encoding.UTF8.GetBytes(json + "\n");
            try
            {
                lock (_writeLock)
                {
                    if (_stream == null || !_stream.IsConnected) return;
                    _stream.Write(data, 0, data.Length);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("WSJT-CTL: write error: " + ex.Message);
            }
        }

        private static async Task ReaderLoop(CancellationToken ct, int gen)
        {
            byte[] buf = new byte[READ_LEN];
            string pending = "";
            try
            {
                while (_running && !ct.IsCancellationRequested && _gen == gen)
                {
                    var s = _stream;
                    if (s == null || !s.IsConnected)
                    {
                        await Task.Delay(25, ct).ConfigureAwait(false);
                        continue;
                    }
                    int n = 0;
                    try
                    {
                        n = await s.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException) { return; }
                    catch (OperationCanceledException) { return; }
                    catch (IOException) { return; }
                    catch { return; }
                    if (n <= 0)
                        return;      // server closed the pipe
                    pending += System.Text.Encoding.UTF8.GetString(buf, 0, n);
                    int nl;
                    while ((nl = pending.IndexOf('\n')) >= 0)
                    {
                        string line = pending.Substring(0, nl).Trim();
                        pending = pending.Substring(nl + 1);
                        if (line.Length > 0)
                            HandleIncoming(line);
                    }
                }
            }
            catch (TaskCanceledException) { }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }
    }
}