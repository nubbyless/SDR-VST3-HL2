//=================================================================
// VstRackMeterHost
//
// Hosts the compact VstRackView inside meter-system containers whose
// meter type is TX/RX_VST_PLUGINS, replacing the DirectX plugin list
// (clsVstPlugins) with the richer rack UI. Each hosted rack owns its
// own refresh timer; teardown happens via Unhost / Shutdown.
//
// The meter item itself (the clsItemGroup added by AddTxVstPlugins /
// AddRxVstPlugins) is left intact so that persistence, ordering and
// the setup UI keep working unchanged - only the drawing is swapped.
//=================================================================

using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace Thetis
{
    internal static class VstRackMeterHost
    {
        private const int RefreshIntervalMs = 750;
        private const int MaxPluginsPerChain = 16;

        private sealed class HostedRack
        {
            public VstChainKind Kind;
            public ucMeter Container;
            public VstRackView RackView;
            public VstChainInfo LastInfo;
            public Timer RefreshTimer;
            public EventHandler RefreshTick;
        }

        private static readonly Dictionary<string, HostedRack> _hosts = new Dictionary<string, HostedRack>();
        private static Console _console;

        public static void Initialize(Console console)
        {
            _console = console;
        }

        public static void Host(string id, VstChainKind kind, ucMeter container)
        {
            if (!Console.VstEnabled)
                return;

            if (container == null || container.IsDisposed)
                return;

            Unhost(id);

            HostedRack host = new HostedRack();
            host.Kind = kind;
            host.Container = container;

            VstRackView rack = new VstRackView(kind);
            rack.Dock = DockStyle.Fill;
            rack.Compact = true;

            WireRackEvents(host, rack);

            host.RackView = rack;

            container.DisplayContainer.Controls.Add(rack);
            rack.BringToFront();

            host.RefreshTick = delegate { Refresh(host); };
            host.RefreshTimer = new Timer();
            host.RefreshTimer.Interval = RefreshIntervalMs;
            host.RefreshTimer.Tick += host.RefreshTick;
            host.RefreshTimer.Start();

            lock (_hosts)
                _hosts[id] = host;

            Refresh(host);
        }

        public static void Unhost(string id)
        {
            HostedRack host;

            lock (_hosts)
            {
                if (!_hosts.TryGetValue(id, out host))
                    return;

                _hosts.Remove(id);
            }

            if (host.RefreshTimer != null)
            {
                host.RefreshTimer.Stop();
                host.RefreshTimer.Tick -= host.RefreshTick;
                host.RefreshTimer.Dispose();
                host.RefreshTimer = null;
            }

            if (host.RackView != null && !host.RackView.IsDisposed)
            {
                if (host.RackView.Parent != null)
                    host.RackView.Parent.Controls.Remove(host.RackView);

                host.RackView.Dispose();
            }
        }

        public static void Shutdown()
        {
            List<string> ids;

            lock (_hosts)
                ids = new List<string>(_hosts.Keys);

            foreach (string id in ids)
                Unhost(id);
        }

        #region Wiring

        private static void WireRackEvents(HostedRack host, VstRackView rack)
        {
            rack.EditorRequested += delegate(object s, VstRackSlotEventArgs e)
            {
                VstPluginState plugin = PluginAt(host, e.Index);

                if (plugin == null || plugin.LoadState != VstPluginLoadState.Active)
                    return;

                // The bridge call can block briefly; keep it off the UI thread.
                VstChainKind kind = host.Kind;
                int index = e.Index;

                System.Threading.Tasks.Task.Run(() => VstHost.OpenPluginEditorWindow(kind, index));
            };

            rack.EnabledToggleRequested += delegate(object s, VstRackSlotEventArgs e)
            {
                VstPluginState plugin = PluginAt(host, e.Index);

                if (plugin != null && VstHost.SetPluginEnabled(host.Kind, e.Index, !plugin.Enabled))
                    Refresh(host);
            };

            rack.BypassToggleRequested += delegate(object s, VstRackSlotEventArgs e)
            {
                VstPluginState plugin = PluginAt(host, e.Index);

                if (plugin != null && VstHost.SetPluginBypass(host.Kind, e.Index, !plugin.Bypass))
                    Refresh(host);
            };

            rack.RemoveRequested += delegate(object s, VstRackSlotEventArgs e)
            {
                if (VstHost.RemovePlugin(host.Kind, e.Index))
                    Refresh(host);
            };

            rack.MoveRequested += delegate(object s, VstRackMoveEventArgs e)
            {
                if (VstHost.MovePlugin(host.Kind, e.Index, e.Index + e.Delta))
                    Refresh(host);
            };

            // Adding a plugin needs the picker, which belongs to the chain
            // manager window; send the user there rather than duplicating it.
            rack.AddRequested += delegate
            {
                if (_console != null)
                {
                    _console.VstChainManagerForm.RefreshChains();
                    _console.VstChainManagerForm.Show(_console);
                    _console.VstChainManagerForm.Focus();
                }
            };

            // Relay content mouse activity so the container still shows its
            // move bar and resize grabber (see ucMeter.DisplayContainer).
            rack.ContentMouseMove += delegate { SafeNotifyMouseMove(host); };
            rack.ContentMouseLeave += delegate { SafeNotifyMouseLeave(host); };
        }

        private static void SafeNotifyMouseMove(HostedRack host)
        {
            if (host.Container != null && !host.Container.IsDisposed)
                host.Container.NotifyContentMouseMove();
        }

        private static void SafeNotifyMouseLeave(HostedRack host)
        {
            if (host.Container != null && !host.Container.IsDisposed)
                host.Container.NotifyContentMouseLeave();
        }

        #endregion

        #region Refresh

        private static void Refresh(HostedRack host)
        {
            if (host == null || host.RackView == null || host.RackView.IsDisposed)
                return;

            if (host.Container != null && !host.Container.Visible)
                return;

            VstChainInfo info = VstHost.GetChainInfo(host.Kind);

            if (info == null)
                return;

            host.LastInfo = info;
            host.RackView.ChainBypassed = info.Bypass;
            host.RackView.SetPlugins(info.Plugins, MaxPluginsPerChain);
        }

        private static VstPluginState PluginAt(HostedRack host, int index)
        {
            if (host.LastInfo == null || host.LastInfo.Plugins == null)
                return null;
            if (index < 0 || index >= host.LastInfo.Plugins.Count)
                return null;

            return host.LastInfo.Plugins[index];
        }

        #endregion
    }
}
