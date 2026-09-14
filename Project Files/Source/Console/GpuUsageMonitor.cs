// Per-process GPU utilisation monitor for the console GPU% indicator.
// Samples the Windows "GPU Engine" performance counter category (the same
// source Task Manager's per-process GPU column uses), summing every engine
// instance that belongs to our PID, and reports 0..100 on the UI thread.
//
// Engines (3D/Copy/VideoDecode/...) come and go dynamically, so instance
// enumeration is refreshed periodically on a worker thread; sampling is a
// plain NextValue() sum. If the category is missing (pre-WDDM2.0 driver,
// stripped-down system) the monitor reports unavailable exactly once and
// stays dormant - the UI hides the indicator in that case.
//
// Note: with multiple physical adapters the theoretical sum can exceed 100;
// values are clamped to 100 which is correct for single-GPU systems.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Thetis
{
    internal sealed class GpuUsageMonitor : IDisposable
    {
        private const string c_categoryName = "GPU Engine";
        private const string c_counterName = "Utilization Percentage";
        private const int c_enumIntervalMs = 5000;
        private const int c_sampleIntervalMs = 1000;

        private readonly string _instancePrefix;
        private readonly SynchronizationContext _uiContext;
        private readonly Action<float> _onSample; // float % on UI thread, -1 => unavailable

        private List<PerformanceCounter> _counters = new List<PerformanceCounter>();
        private Timer _sampleTimer;
        private Timer _enumTimer;
        private bool _started;
        private bool _unavailableReported;
        private int _disposed;

        public GpuUsageMonitor(Action<float> onSample)
        {
            _onSample = onSample ?? delegate { };
            _uiContext = SynchronizationContext.Current;
            _instancePrefix = "pid_" + Process.GetCurrentProcess().Id + "_";
        }

        public void Start()
        {
            if (_started)
                return;
            _started = true;

            // PerformanceCounterCategory.Exists can take seconds - keep off the UI thread.
            // The threadpool is used for all counter access from here on.
            Task.Run(() =>
            {
                try
                {
                    if (!PerformanceCounterCategory.Exists(c_categoryName))
                    {
                        reportUnavailable();
                        return;
                    }

                    if (!enumerateInstances())
                    {
                        reportUnavailable();
                        return;
                    }
                }
                catch
                {
                    reportUnavailable();
                    return;
                }

                _enumTimer = new Timer(delegate { enumerateInstances(); }, null, c_enumIntervalMs, c_enumIntervalMs);
                _sampleTimer = new Timer(delegate { sample(); }, null, c_sampleIntervalMs, c_sampleIntervalMs);
            });
        }

        // returns true when at least one matching engine instance was found
        private bool enumerateInstances()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return false;

            List<PerformanceCounter> old;
            List<PerformanceCounter> fresh = new List<PerformanceCounter>();
            try
            {
                PerformanceCounterCategory cat = new PerformanceCounterCategory(c_categoryName);
                foreach (string inst in cat.GetInstanceNames())
                {
                    if (inst.StartsWith(_instancePrefix, StringComparison.OrdinalIgnoreCase))
                        fresh.Add(new PerformanceCounter(c_categoryName, c_counterName, inst, true));
                }
            }
            catch
            {
                // category vanished / transient PDH failure - keep whatever we had
                fresh.ForEach(x => x.Dispose());
                return _counters.Count > 0;
            }

            if (fresh.Count == 0 && _counters.Count > 0)
            {
                // nothing matched this refresh (engines idle & deregistered);
                // keep previous counters so the next sample still reads ~0
                fresh.ForEach(x => x.Dispose());
                return true;
            }

            old = Interlocked.Exchange(ref _counters, fresh);
            disposeList(old);
            return fresh.Count > 0;
        }

        private void sample()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            float sum = 0f;
            List<PerformanceCounter> snapshot = Volatile.Read(ref _counters);
            foreach (PerformanceCounter pc in snapshot)
            {
                try
                {
                    float v = pc.NextValue();
                    if (v > 0f)
                        sum += v;
                }
                catch
                {
                    // instance died between enum and sample - ignore until next enum
                }
            }

            post(Math.Min(sum, 100f));
        }

        private void reportUnavailable()
        {
            if (_unavailableReported)
                return;
            _unavailableReported = true;
            post(-1f);
        }

        private void post(float value)
        {
            SynchronizationContext ctx = _uiContext;
            Action<float> cb = _onSample;
            if (ctx != null)
                ctx.Post(delegate { cb(value); }, null);
            else
                cb(value);
        }

        private static void disposeList(List<PerformanceCounter> list)
        {
            if (list == null)
                return;
            foreach (PerformanceCounter pc in list)
            {
                try { pc.Dispose(); } catch { }
            }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            disposeTimers();
            disposeList(Interlocked.Exchange(ref _counters, new List<PerformanceCounter>()));
        }

        private void disposeTimers()
        {
            // plain dispose; a callback that already passed its _disposed check
            // is still safe - counter access is try/caught and post() is guarded
            // by the caller's IsDisposed check on the UI side
            Timer t1 = Interlocked.Exchange(ref _sampleTimer, null);
            Timer t2 = Interlocked.Exchange(ref _enumTimer, null);
            t1?.Dispose();
            t2?.Dispose();
        }
    }
}
