/*  WsjtManager.cs
 *
 *  Manages the WSJT-X FT8/FT4 sidecar process for HF weak-signal modes.
 *
 *  Copyright (C) 2026  SDR-VST3 Thetis integration (WSJT-X sidecar)
 *
 *  This program is free software; you can redistribute it and/or
 *  modify it under the terms of the GNU General Public License
 *  as published by the Free Software Foundation; either version 2
 *  of the License, or (at your option) any later version.
 *
 *  WSJT-X (FT8/FT4/WSPR, bundled as wsjtx.exe in the WsjtX install
 *  folder) is Copyright (C) the WSJT-X Development Group and
 *  contributors, hosted at https://github.com/WSJTX/wsjtx (GPL v3).
 *  The sidecar binary shipped with SDR-VST3 is built from a fork of that
 *  project, modified to bridge audio and CAT to SDR-VST3 over named
 *  pipes.  The full GPL v3 license text is installed alongside the
 *  application in the Licenses folder (LICENSE-WSJT-X.txt).
 */

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Thetis.WSJTX
{
    /// <summary>
    /// Owns the wsjtx.exe sidecar: spawn / kill / teardown, plus the
    /// audio named-pipe bridge that feeds it RX audio.  Enable/disable is
    /// driven from the main console "WSJT-X" menu entry; this class reacts
    /// to SetEnabled and does NOT auto-start on app launch by design --
    /// the operator starts/stops the sidecar explicitly each session.  A
    /// sidecar exit (graceful or crash) turns the session off; it never
    /// restarts on its own.
    ///
    /// P1 is decode-only: the sidecar is launched with its own rig-less
    /// configuration (`-r SDR-VST3 -c SDR-VST3`) and audio arrives over the
    /// AUDIO pipe (8 kHz mono float32, little-endian).  No control pipe yet.
    /// </summary>
    public static class WsjtManager
    {
        public const string PIPE_BASE = "SDRVST3.WSJTX";
        public const int AUDIO_RATE = 8000;

        private static readonly object _sync = new object();
        private static Process _process;
        private static int _pid;
        private static bool _enabled;
        private static bool _starting;

        /// <summary>True while the WSJT-X session is desired on (managed + bridge live).</summary>
        public static bool Enabled { get { lock (_sync) return _enabled; } }

        /// <summary>PID of the running wsjtx.exe, 0 if none.</summary>
        public static int Pid { get { lock (_sync) return _pid; } }

        public static bool Running
        {
            get
            {
                lock (_sync)
                {
                    try { return _process != null && _process.HasExited == false; }
                    catch { return false; }
                }
            }
        }

        static WsjtManager()
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SDR-VST3");
                Directory.CreateDirectory(dir);
                var l = new TextWriterTraceListener(Path.Combine(dir, "wsjtx-integr.log"), "wsjtxint");
                Trace.Listeners.Add(l);
                Trace.AutoFlush = true;
            }
            catch { }
        }

        /// <summary>Raised after the sidecar's desired state changes (started,
        /// exited) so the console can refresh the menu.</summary>
        public static event EventHandler StateChanged;

        private static void NotifyStateChanged()
        {
            try { StateChanged?.Invoke(null, EventArgs.Empty); }
            catch { }
        }

        /// <summary>Executable path preference.  If unset the manager probes
        /// <c>Application.StartupPath\WsjtX\wsjtx.exe</c>.</summary>
        public static string ExecutablePath = "";

        public static string AudioPipeName(int pid)
        {
            return string.Format("{0}.{1}.AUDIO", PIPE_BASE, pid);
        }

        public static string TxAudioPipeName(int pid)
        {
            return string.Format("{0}.{1}.TXAUDIO", PIPE_BASE, pid);
        }

        public static string CtlPipeName(int pid)
        {
            return string.Format("{0}.{1}.CTL", PIPE_BASE, pid);
        }

        /// <summary>Paths probed, in order, for wsjtx.exe.</summary>
        private static string ResolveExecutable()
        {
            if (!string.IsNullOrWhiteSpace(ExecutablePath) && File.Exists(ExecutablePath))
                return ExecutablePath;

            string[] candidates =
            {
                Path.Combine(Application.StartupPath, "WsjtX", "wsjtx.exe"),
                Path.Combine(Path.GetDirectoryName(Application.ExecutablePath) ?? "", "WsjtX", "wsjtx.exe"),
                // Dev-tree fallback: the freshly built sidecar next to this
                // source checkout.  Lets "enable" launch the current build
                // before a real installer layout exists.
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "wsjtx", "build", "wsjtx.exe"),
            };
            foreach (var c in candidates)
            {
                string full;
                try { full = Path.GetFullPath(c); } catch { continue; }
                if (!string.IsNullOrWhiteSpace(full) && File.Exists(full))
                    return full;
            }
            return "";
        }

        /// <summary>
        /// Enable or disable the whole WSJT-X session.  Called by the menu
        /// entry (which also owns persistence-free state).  When enabling, the
        /// native RX1 tap is armed and the sidecar is launched; when disabling
        /// everything is torn down and the tap disarmed.
        /// </summary>
        public static void SetEnabled(bool on)
        {
            lock (_sync)
            {
                if (on == _enabled)
                {
                    // Still apply if a (re)start was requested while already
                    // enabled but the process is gone.
                    if (!on || Running)
                        return;
                }
                _enabled = on;
            }

            if (on)
                Start();
            else
                Stop();
        }

        public static void Start()
        {
            bool should;
            lock (_sync) should = _enabled && !_starting && !Running;
            if (!should) return;

            lock (_sync) _starting = true;
            try
            {
                string exe = ResolveExecutable();
                if (exe == "")
                {
                    Trace.WriteLine("WSJT-X: no wsjtx.exe found (checked WsjtX\\wsjtx.exe next to host)");
                    return;
                }

                // Sidecar runs decode-only with its own app/ini identity and
                // a configuration group (seeded by main.cpp's bootstrap when
                // both -c and --sdrvst3-pipe are present).
                string args = string.Format("--sdrvst3-pipe={0} -r SDR-VST3 -c SDR-VST3", PIPE_BASE);

                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                };
                // Dev convenience: let the MinGW Qt DLLs resolve exactly like
                // the probe harness does.  The install layout ships them next
                // to wsjtx.exe so this is only meaningful for dev trees.
                string mingw = @"C:\msys64\mingw64\bin";
                if (Directory.Exists(mingw))
                    psi.Environment["PATH"] = mingw + ";" + (Environment.GetEnvironmentVariable("PATH") ?? "");

                var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                if (!proc.Start())
                {
                    Trace.WriteLine("WSJT-X: process failed to start");
                    return;
                }
                proc.Exited += OnProcessExited;
                _process = proc;
                _pid = proc.Id;

                // Arm the taps + start the bridges.  RX flows whenever the
                // session is up so the decoder always has audio; TX is also
                // enabled so the sidecar can key and push modem audio the
                // moment the operator clicks Enable Tx in WSJT-X.
                try { cmaster.SetWsjtRxEnable(0, 1); } catch { }
                try { cmaster.SetWsjtTxEnable(1); } catch { }
                try { cmaster.WsjtFlush(); } catch { }

                WsjtAudioBridge.Start(_pid);
                WsjtCtlBridge.Start(_pid);
                Trace.WriteLine(string.Format("WSJT-X: started pid {0} ({1})", _pid, exe));
            }
            catch (Exception ex)
            {
                Trace.WriteLine("WSJT-X: start exception: " + ex.Message);
            }
            finally
            {
                lock (_sync) _starting = false;
            }
        }

        private static void OnProcessExited(object sender, EventArgs e)
        {
            Process p = sender as Process;
            if (p == null) return;

            // Any exit -- graceful (operator closed the WSJT-X window) or a
            // crash -- turns the session off and does NOT auto-restart.  The
            // operator starts the sidecar explicitly from the menu, so a
            // force-killed stub can never come back on its own.
            lock (_sync)
            {
                if (_process != p)
                    return;   // stale exit from an old instance
                _process = null;
                _pid = 0;
                _enabled = false;
            }
            try { cmaster.SetWsjtRxEnable(0, 0); } catch { }
            WsjtAudioBridge.Stop();
            WsjtCtlBridge.Stop();
            try { p.Dispose(); } catch { }
            NotifyStateChanged();
        }

        public static void Stop()
        {
            Process p;
            lock (_sync)
            {
                p = _process;
                _process = null;
                _pid = 0;
            }

            try { WsjtAudioBridge.Stop(); } catch { }
            try { WsjtCtlBridge.Stop(); } catch { }

            if (p != null)
            {
                try
                {
                    if (!p.HasExited)
                        p.Kill();
                }
                catch { }
                try { p.WaitForExit(2000); } catch { }
                try { p.Dispose(); } catch { }
            }

            try { cmaster.SetWsjtRxEnable(0, 0); } catch { }
            try { cmaster.SetWsjtTxEnable(0); } catch { }
            try { cmaster.WsjtFlush(); } catch { }
            Trace.WriteLine("WSJT-X: stopped");
        }

        /// <summary>App-exit hook: always tear down cleanly.</summary>
        public static void Shutdown()
        {
            lock (_sync) _enabled = false;
            Stop();
        }
    }
}