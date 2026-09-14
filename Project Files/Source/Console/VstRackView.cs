//=================================================================
// VstRackView.cs
//=================================================================
// Owner-drawn "rack of outboard gear" presentation for a VST plugin chain,
// modelled on the Cubase VST Instruments rack: a numbered gutter, dark
// gradient faceplates, a compact icon cluster per unit, and the plugin's
// vendor snapshot artwork filling the body.
//
// This is presentation only. Every mutation is raised as an event carrying a
// slot index; VstChainManagerForm performs the actual chain operation through
// VstHost exactly as the list view does.
//=================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Thetis
{
    internal enum VstRackHitRegion
    {
        None,
        Header,
        Body,
        Enable,
        Editor,
        Bypass,
        Remove,
        Collapse,
        AddBay
    }

    internal sealed class VstRackSlotEventArgs : EventArgs
    {
        public int Index { get; private set; }

        public VstRackSlotEventArgs(int index)
        {
            Index = index;
        }
    }

    internal sealed class VstRackMoveEventArgs : EventArgs
    {
        public int Index { get; private set; }
        public int Delta { get; private set; }

        public VstRackMoveEventArgs(int index, int delta)
        {
            Index = index;
            Delta = delta;
        }
    }

    /// <summary>
    /// Shared palette, sampled from the Cubase rack reference.
    /// </summary>
    internal static class VstRackTheme
    {
        public static readonly Color Void = Color.FromArgb(0x1E, 0x1E, 0x1E);
        public static readonly Color FaceTop = Color.FromArgb(0x4A, 0x4A, 0x4A);
        public static readonly Color FaceBottom = Color.FromArgb(0x3A, 0x3A, 0x3A);
        public static readonly Color Inset = Color.FromArgb(0x33, 0x33, 0x33);
        public static readonly Color Border = Color.FromArgb(0x1A, 0x1A, 0x1A);
        public static readonly Color Bevel = Color.FromArgb(0x5E, 0x5E, 0x5E);
        public static readonly Color TextPrimary = Color.FromArgb(0xD8, 0xD8, 0xD8);
        public static readonly Color TextSecondary = Color.FromArgb(0x9A, 0x9A, 0x9A);
        public static readonly Color SelectionBorder = Color.FromArgb(0x8A, 0x8A, 0x8A);
        public static readonly Color Gutter = Color.FromArgb(0x26, 0x26, 0x26);
        public static readonly Color IconHover = Color.FromArgb(0x60, 0x60, 0x60);
        public static readonly Color LedActive = Color.FromArgb(0x6E, 0xC8, 0x6E);
        public static readonly Color LedDescriptor = Color.FromArgb(0xD6, 0xA8, 0x4A);
        public static readonly Color LedFailed = Color.FromArgb(0xD0, 0x5A, 0x5A);
        public static readonly Color LedOff = Color.FromArgb(0x55, 0x55, 0x55);
        public static readonly Color AccentOn = Color.FromArgb(0x7E, 0xC8, 0xF0);
    }

    /// <summary>
    /// Scrolling container that hosts one <see cref="VstRackUnit"/> per plugin
    /// plus a trailing "add" bay. Units are reused across refreshes so the
    /// periodic status refresh does not cause flicker.
    /// </summary>
    internal sealed class VstRackView : Panel
    {
        private readonly List<VstRackUnit> _units = new List<VstRackUnit>();
        private readonly VstChainKind _kind;
        private readonly ToolTip _toolTip;
        private int _selectedIndex = -1;
        private int _pluginCount;
        private int _maxSlots;
        private bool _layingOut;
        private bool _compact;
        private bool _chainBypassed;

        /// <summary>
        /// When the whole chain is bypassed every unit is displayed as
        /// bypassed regardless of each plugin's own bypass flag.
        /// </summary>
        public bool ChainBypassed
        {
            get { return _chainBypassed; }
            set
            {
                if (_chainBypassed == value)
                    return;

                _chainBypassed = value;

                for (int i = 0; i < _units.Count; i++)
                    _units[i].ChainBypassed = value;

                Invalidate(true);
            }
        }

        /// <summary>
        /// Raised for mouse movement anywhere over the rack, including over its
        /// units. A host that needs to know the pointer is over its content —
        /// such as a ucMeter container, whose move/resize chrome is driven by
        /// mouse movement over its own panel — can subscribe to this.
        /// </summary>
        public event EventHandler ContentMouseMove;

        /// <summary>Raised when the pointer leaves the rack entirely.</summary>
        public event EventHandler ContentMouseLeave;

        public event EventHandler SelectionChanged;
        public event EventHandler<VstRackSlotEventArgs> RemoveRequested;
        public event EventHandler<VstRackMoveEventArgs> MoveRequested;
        public event EventHandler<VstRackSlotEventArgs> EnabledToggleRequested;
        public event EventHandler<VstRackSlotEventArgs> BypassToggleRequested;
        public event EventHandler<VstRackSlotEventArgs> EditorRequested;
        public event EventHandler AddRequested;

        public VstRackView(VstChainKind kind)
        {
            _kind = kind;
            _toolTip = new ToolTip();
            _toolTip.AutoPopDelay = 8000;
            _toolTip.InitialDelay = 500;

            AutoScroll = true;
            BackColor = VstRackTheme.Void;
            BorderStyle = BorderStyle.FixedSingle;
            DoubleBuffered = true;
            Padding = new Padding(0, 0, 0, ScaleBy(6));
        }

        public VstChainKind Kind
        {
            get { return _kind; }
        }

        public int PluginCount
        {
            get { return _pluginCount; }
        }

        /// <summary>
        /// Renders every unit as a single short row with no artwork. Intended
        /// for the on-screen rack containers, which are too small for full
        /// units.
        /// </summary>
        public bool Compact
        {
            get { return _compact; }
            set
            {
                if (_compact == value)
                    return;

                _compact = value;

                for (int i = 0; i < _units.Count; i++)
                    _units[i].Compact = value;

                LayoutUnits();
            }
        }

        /// <summary>
        /// Index of the selected plugin, or -1 when this rack holds no
        /// selection. Setting the value does not raise
        /// <see cref="SelectionChanged"/>.
        /// </summary>
        public int SelectedIndex
        {
            get { return _selectedIndex; }
            set
            {
                int clamped = value >= 0 && value < _pluginCount ? value : -1;

                if (_selectedIndex == clamped)
                    return;

                _selectedIndex = clamped;
                ApplySelectionToUnits();
            }
        }

        private int ScaleBy(int value)
        {
            return (int)Math.Round(value * (DeviceDpi / 96.0));
        }

        /// <summary>
        /// Rebuilds the rack from the chain's plugin list, reusing existing
        /// unit controls where possible.
        /// </summary>
        public void SetPlugins(IList<VstPluginState> plugins, int maxSlots)
        {
            int count = plugins != null ? plugins.Count : 0;

            _pluginCount = count;
            _maxSlots = maxSlots;

            // One unit per plugin, plus an add bay while the chain has room.
            int desiredUnits = count < maxSlots ? count + 1 : count;

            SuspendLayout();

            try
            {
                while (_units.Count < desiredUnits)
                {
                    VstRackUnit unit = new VstRackUnit(_toolTip);
                    unit.Compact = _compact;
                    unit.Clicked += OnUnitClicked;
                    // Units cover the rack, so their mouse activity has to be
                    // relayed for a host to see it as movement over the content.
                    unit.MouseMove += OnUnitMouseMove;
                    unit.MouseLeave += OnUnitMouseLeave;
                    _units.Add(unit);
                    Controls.Add(unit);
                }

                while (_units.Count > desiredUnits)
                {
                    VstRackUnit unit = _units[_units.Count - 1];
                    _units.RemoveAt(_units.Count - 1);
                    Controls.Remove(unit);
                    unit.Clicked -= OnUnitClicked;
                    unit.MouseMove -= OnUnitMouseMove;
                    unit.MouseLeave -= OnUnitMouseLeave;
                    unit.Dispose();
                }

                for (int i = 0; i < _units.Count; i++)
                {
                    if (i < count)
                        _units[i].SetPlugin(i, plugins[i], i == _selectedIndex);
                    else
                        _units[i].SetAddBay(i);

                    _units[i].ChainBypassed = _chainBypassed;
                }

                if (_selectedIndex >= count)
                    _selectedIndex = -1;

                LayoutUnits();
            }
            finally
            {
                ResumeLayout(true);
            }
        }

        private void LayoutUnits()
        {
            // Setting AutoScrollMinSize below can change the client size, which
            // re-enters here; one pass is enough.
            if (_layingOut)
                return;

            _layingOut = true;

            try
            {
                int margin = ScaleBy(6);
                int gap = ScaleBy(6);
                int width = ClientSize.Width - (margin * 2) - SystemInformation.VerticalScrollBarWidth;

                // Child positions in a scrolling panel are client coordinates,
                // already shifted by the scroll offset. Laying out with raw
                // values while scrolled pushes units downward and grows the
                // scroll range upward, leaving empty space above the first unit.
                int scrollOffset = AutoScrollPosition.Y;
                int y = margin;

                if (width < ScaleBy(120))
                    width = ScaleBy(120);

                for (int i = 0; i < _units.Count; i++)
                {
                    VstRackUnit unit = _units[i];
                    int height = unit.GetPreferredHeight();

                    unit.SetBounds(margin, y + scrollOffset, width, height);
                    y += height + gap;
                }

                // State the content extent explicitly rather than letting it be
                // inferred from scrolled child bounds, so the scroll range
                // always matches the units exactly.
                Size contentSize = new Size(0, y);

                if (AutoScrollMinSize != contentSize)
                    AutoScrollMinSize = contentSize;
            }
            finally
            {
                _layingOut = false;
            }
        }

        protected override void OnClientSizeChanged(EventArgs e)
        {
            base.OnClientSizeChanged(e);
            LayoutUnits();
        }

        private const int WM_NCMOUSEMOVE = 0x00A0;
        private const int WM_NCMOUSELEAVE = 0x02A2;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            // The scrollbar is non-client area and so never raises MouseMove.
            // Without relaying this a host cannot tell the pointer is over the
            // rack's right or bottom edge — which is exactly where a container
            // keeps its resize grabber.
            if (m.Msg == WM_NCMOUSEMOVE)
                RaiseContentMouseMove();
            else if (m.Msg == WM_NCMOUSELEAVE)
                RaiseContentMouseLeave();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            RaiseContentMouseMove();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            RaiseContentMouseLeave();
        }

        private void OnUnitMouseMove(object sender, MouseEventArgs e)
        {
            RaiseContentMouseMove();
        }

        private void OnUnitMouseLeave(object sender, EventArgs e)
        {
            RaiseContentMouseLeave();
        }

        private void RaiseContentMouseMove()
        {
            if (ContentMouseMove != null)
                ContentMouseMove(this, EventArgs.Empty);
        }

        private void RaiseContentMouseLeave()
        {
            if (ContentMouseLeave != null)
                ContentMouseLeave(this, EventArgs.Empty);
        }

        private void ApplySelectionToUnits()
        {
            for (int i = 0; i < _units.Count; i++)
                _units[i].SetSelected(i == _selectedIndex);
        }

        private void OnUnitClicked(object sender, VstRackUnitClickEventArgs e)
        {
            if (e.Region == VstRackHitRegion.AddBay)
            {
                if (AddRequested != null)
                    AddRequested(this, EventArgs.Empty);
                return;
            }

            if (e.Index < 0 || e.Index >= _pluginCount)
                return;

            // Everything except the collapse chevron implies selecting the unit.
            if (e.Region != VstRackHitRegion.Collapse && _selectedIndex != e.Index)
            {
                _selectedIndex = e.Index;
                ApplySelectionToUnits();

                if (SelectionChanged != null)
                    SelectionChanged(this, EventArgs.Empty);
            }

            switch (e.Region)
            {
                case VstRackHitRegion.Body:
                    if (EditorRequested != null)
                        EditorRequested(this, new VstRackSlotEventArgs(e.Index));
                    break;

                case VstRackHitRegion.Editor:
                    if (EditorRequested != null)
                        EditorRequested(this, new VstRackSlotEventArgs(e.Index));
                    break;

                case VstRackHitRegion.Enable:
                    if (EnabledToggleRequested != null)
                        EnabledToggleRequested(this, new VstRackSlotEventArgs(e.Index));
                    break;

                case VstRackHitRegion.Bypass:
                    if (BypassToggleRequested != null)
                        BypassToggleRequested(this, new VstRackSlotEventArgs(e.Index));
                    break;

                case VstRackHitRegion.Remove:
                    if (RemoveRequested != null)
                        RemoveRequested(this, new VstRackSlotEventArgs(e.Index));
                    break;

                case VstRackHitRegion.Collapse:
                    LayoutUnits();
                    break;
            }
        }

        /// <summary>Moves the selected plugin, if any, by <paramref name="delta"/>.</summary>
        public void RequestMoveSelected(int delta)
        {
            if (_selectedIndex < 0)
                return;

            if (MoveRequested != null)
                MoveRequested(this, new VstRackMoveEventArgs(_selectedIndex, delta));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                for (int i = 0; i < _units.Count; i++)
                    _units[i].Clicked -= OnUnitClicked;

                _units.Clear();

                if (_toolTip != null)
                    _toolTip.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    internal sealed class VstRackUnitClickEventArgs : EventArgs
    {
        public int Index { get; private set; }
        public VstRackHitRegion Region { get; private set; }

        public VstRackUnitClickEventArgs(int index, VstRackHitRegion region)
        {
            Index = index;
            Region = region;
        }
    }

    /// <summary>
    /// A single rack unit: header strip with icon cluster, plugin name, format
    /// badge and load-state LED, above a body showing the plugin's snapshot
    /// artwork (or a drawn placeholder faceplate).
    /// </summary>
    internal sealed class VstRackUnit : Control
    {
        private const int HeaderHeightDip = 46;
        private const int BodyHeightDip = 104;
        private const int AddBayHeightDip = 40;
        private const int GutterWidthDip = 26;
        private const int IconSizeDip = 16;
        private const int IconGapDip = 5;

        // Compact rows exist for the on-screen containers, which are far too
        // small to show full units with artwork.
        private const int CompactHeightDip = 26;
        private const int CompactAddBayHeightDip = 22;

        private readonly ToolTip _toolTip;

        private int _index = -1;
        private VstPluginState _plugin;
        private bool _selected;
        private bool _collapsed;
        private bool _isAddBay;
        private bool _compact;
        private VstRackHitRegion _hoverRegion = VstRackHitRegion.None;

        private Rectangle _enableRect;
        private Rectangle _editorRect;
        private Rectangle _bypassRect;
        private Rectangle _removeRect;
        private Rectangle _collapseRect;
        private Rectangle _headerRect;
        private Rectangle _bodyRect;

        public event EventHandler<VstRackUnitClickEventArgs> Clicked;

        public bool ChainBypassed { get; set; }

        private bool IsBypassed
        {
            get { return _plugin != null && (_plugin.Bypass || ChainBypassed); }
        }

        public VstRackUnit(ToolTip toolTip)
        {
            _toolTip = toolTip;

            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);

            BackColor = VstRackTheme.Void;
        }

        private float DpiScale
        {
            get { return DeviceDpi / 96f; }
        }

        private int ScaleBy(int value)
        {
            return (int)Math.Round(value * DpiScale);
        }

        /// <summary>
        /// Renders as a single short row: no artwork, no collapse chevron, and
        /// no body. Used by the on-screen rack containers.
        /// </summary>
        public bool Compact
        {
            get { return _compact; }
            set
            {
                if (_compact == value)
                    return;

                _compact = value;
                Invalidate();
            }
        }

        public int GetPreferredHeight()
        {
            if (_compact)
                return ScaleBy(_isAddBay ? CompactAddBayHeightDip : CompactHeightDip);

            if (_isAddBay)
                return ScaleBy(AddBayHeightDip);

            return _collapsed
                ? ScaleBy(HeaderHeightDip)
                : ScaleBy(HeaderHeightDip) + ScaleBy(BodyHeightDip);
        }

        public void SetPlugin(int index, VstPluginState plugin, bool selected)
        {
            _index = index;
            _plugin = plugin;
            _selected = selected;
            _isAddBay = false;
            _collapsed = plugin != null && plugin.Path != null &&
                VstHost.UiState.CollapsedPlugins.Contains(plugin.Path);

            Invalidate();
        }

        public void SetAddBay(int index)
        {
            _index = index;
            _plugin = null;
            _selected = false;
            _isAddBay = true;
            _collapsed = false;

            Invalidate();
        }

        public void SetSelected(bool selected)
        {
            if (_selected == selected)
                return;

            _selected = selected;
            Invalidate();
        }

        private void ToggleCollapsed()
        {
            if (_plugin == null || string.IsNullOrEmpty(_plugin.Path))
                return;

            _collapsed = !_collapsed;

            List<string> collapsed = VstHost.UiState.CollapsedPlugins;

            if (_collapsed)
            {
                if (!collapsed.Contains(_plugin.Path))
                    collapsed.Add(_plugin.Path);
            }
            else
            {
                collapsed.Remove(_plugin.Path);
            }

            VstHost.ScheduleUiStateSave();
            Invalidate();
        }

        #region Layout

        private void RecomputeLayout()
        {
            if (_compact)
            {
                RecomputeCompactLayout();
                return;
            }

            int gutter = ScaleBy(GutterWidthDip);
            int headerHeight = ScaleBy(HeaderHeightDip);
            int icon = ScaleBy(IconSizeDip);
            int gap = ScaleBy(IconGapDip);
            int pad = ScaleBy(6);

            _headerRect = new Rectangle(gutter, 0, Math.Max(0, Width - gutter), headerHeight);

            int chevronWidth = ScaleBy(18);
            _collapseRect = new Rectangle(Width - chevronWidth - pad, pad, chevronWidth, chevronWidth);

            int x = gutter + pad;
            int iconY = pad;

            _enableRect = new Rectangle(x, iconY, icon, icon);
            x += icon + gap;
            _editorRect = new Rectangle(x, iconY, icon, icon);
            x += icon + gap;
            _bypassRect = new Rectangle(x, iconY, icon, icon);
            x += icon + gap;
            _removeRect = new Rectangle(x, iconY, icon, icon);

            _bodyRect = _collapsed
                ? Rectangle.Empty
                : new Rectangle(gutter, headerHeight, Math.Max(0, Width - gutter), Math.Max(0, Height - headerHeight));
        }

        private void RecomputeCompactLayout()
        {
            int gutter = ScaleBy(20);
            int icon = ScaleBy(13);
            int gap = ScaleBy(3);
            int pad = ScaleBy(3);
            int iconY = Math.Max(0, (Height - icon) / 2);

            _headerRect = new Rectangle(gutter, 0, Math.Max(0, Width - gutter), Height);
            _bodyRect = Rectangle.Empty;
            _collapseRect = Rectangle.Empty;

            int x = gutter + pad;

            _enableRect = new Rectangle(x, iconY, icon, icon);
            x += icon + gap;
            _editorRect = new Rectangle(x, iconY, icon, icon);
            x += icon + gap;
            _bypassRect = new Rectangle(x, iconY, icon, icon);
            x += icon + gap;
            _removeRect = new Rectangle(x, iconY, icon, icon);
        }

        /// <summary>
        /// Splits the body into the artwork area and a reserved strip beneath
        /// it. The strip is currently unused; it is where a future quick
        /// control knob row will live, so adding one stays a layout change.
        /// </summary>
        private void GetBodyRegions(out Rectangle artworkRect, out Rectangle knobStripRect)
        {
            artworkRect = _bodyRect;
            knobStripRect = Rectangle.Empty;
        }

        private VstRackHitRegion HitTest(Point location)
        {
            if (_isAddBay)
                return VstRackHitRegion.AddBay;

            if (_enableRect.Contains(location)) return VstRackHitRegion.Enable;
            if (_editorRect.Contains(location)) return VstRackHitRegion.Editor;
            if (_bypassRect.Contains(location)) return VstRackHitRegion.Bypass;
            if (_removeRect.Contains(location)) return VstRackHitRegion.Remove;
            if (_collapseRect.Contains(location)) return VstRackHitRegion.Collapse;
            if (!_collapsed && _bodyRect.Contains(location)) return VstRackHitRegion.Body;
            if (_headerRect.Contains(location)) return VstRackHitRegion.Header;

            return VstRackHitRegion.Header;
        }

        #endregion

        #region Mouse

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            VstRackHitRegion region = HitTest(e.Location);

            if (region == _hoverRegion)
                return;

            _hoverRegion = region;
            Cursor = IsClickableRegion(region) ? Cursors.Hand : Cursors.Default;

            if (_toolTip != null)
                _toolTip.SetToolTip(this, GetRegionTooltip(region));

            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);

            _hoverRegion = VstRackHitRegion.None;
            Cursor = Cursors.Default;
            Invalidate();
        }

        private bool IsClickableRegion(VstRackHitRegion region)
        {
            switch (region)
            {
                case VstRackHitRegion.Enable:
                case VstRackHitRegion.Editor:
                case VstRackHitRegion.Bypass:
                case VstRackHitRegion.Remove:
                case VstRackHitRegion.Collapse:
                case VstRackHitRegion.Body:
                case VstRackHitRegion.AddBay:
                    return true;
                default:
                    return false;
            }
        }

        private string GetRegionTooltip(VstRackHitRegion region)
        {
            switch (region)
            {
                case VstRackHitRegion.Enable:
                    return _plugin != null && _plugin.Enabled ? "Disable plugin" : "Enable plugin";
                case VstRackHitRegion.Bypass:
                    return _plugin != null && _plugin.Bypass ? "Unbypass plugin" : "Bypass plugin";
                case VstRackHitRegion.Editor:
                    return "Open plugin editor";
                case VstRackHitRegion.Remove:
                    return "Remove plugin";
                case VstRackHitRegion.Collapse:
                    return _collapsed ? "Expand" : "Collapse";
                case VstRackHitRegion.AddBay:
                    return "Add a plugin to this chain";
                case VstRackHitRegion.Body:
                    return !_compact && _plugin != null ? (_plugin.Path ?? string.Empty) : string.Empty;
                default:
                    return string.Empty;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (e.Button != MouseButtons.Left)
                return;

            VstRackHitRegion region = HitTest(e.Location);

            if (region == VstRackHitRegion.Collapse)
                ToggleCollapsed();

            if (Clicked != null)
                Clicked(this, new VstRackUnitClickEventArgs(_index, region));
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);

            if (e.Button != MouseButtons.Left || _isAddBay)
                return;

            VstRackHitRegion region = HitTest(e.Location);

            // Double-clicking the header opens the editor too; the body already
            // opened it on the first click.
            if (region == VstRackHitRegion.Header && Clicked != null)
                Clicked(this, new VstRackUnitClickEventArgs(_index, VstRackHitRegion.Editor));
        }

        #endregion

        #region Painting

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;

            RecomputeLayout();

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(VstRackTheme.Void);

            if (_isAddBay)
            {
                PaintAddBay(g);
                return;
            }

            if (_compact)
            {
                PaintCompact(g);
                return;
            }

            PaintGutter(g);
            PaintFaceplate(g);
            PaintHeaderContent(g);

            if (!_collapsed)
                PaintBody(g);

            PaintBorder(g);
        }

        /// <summary>
        /// Single-row rendering for the on-screen containers: slot number, icon
        /// cluster, name, format badge and load LED. No artwork or body.
        /// </summary>
        private void PaintCompact(Graphics g)
        {
            int gutter = ScaleBy(20);
            Rectangle face = new Rectangle(gutter, 0,
                Math.Max(1, Width - gutter - 1), Math.Max(1, Height - 1));

            using (SolidBrush fill = new SolidBrush(VstRackTheme.Gutter))
                g.FillRectangle(fill, new Rectangle(0, 0, gutter, Height));

            Color top = VstRackTheme.FaceTop;
            Color bottom = VstRackTheme.FaceBottom;

            if (IsDimmed())
            {
                top = Dim(top);
                bottom = Dim(bottom);
            }

            if (_selected)
            {
                top = Lighten(top, 0.10f);
                bottom = Lighten(bottom, 0.10f);
            }

            using (GraphicsPath path = CreateRoundedPath(face, ScaleBy(3)))
            using (LinearGradientBrush brush = new LinearGradientBrush(
                new Rectangle(face.X, face.Y, face.Width, Math.Max(1, face.Height)),
                top, bottom, LinearGradientMode.Vertical))
            {
                g.FillPath(brush, path);
            }

            using (SolidBrush text = new SolidBrush(VstRackTheme.TextSecondary))
            using (Font font = new Font(Font.FontFamily, 7f, FontStyle.Bold))
            using (StringFormat sf = CreateCenteredFormat())
            {
                g.DrawString((_index + 1).ToString(), font, text,
                    new Rectangle(0, 0, gutter, Height), sf);
            }

            PaintIcon(g, _enableRect, VstRackHitRegion.Enable);
            PaintIcon(g, _editorRect, VstRackHitRegion.Editor);
            PaintIcon(g, _bypassRect, VstRackHitRegion.Bypass);
            PaintIcon(g, _removeRect, VstRackHitRegion.Remove);

            // LED and format badge sit hard right; the name takes what is left.
            int ledSize = ScaleBy(6);
            Rectangle ledRect = new Rectangle(Width - ScaleBy(10), (Height - ledSize) / 2, ledSize, ledSize);

            using (SolidBrush brush = new SolidBrush(GetLoadStateColor()))
                g.FillEllipse(brush, ledRect);

            int badgeRight = ledRect.Left - ScaleBy(4);
            int nameRight = badgeRight;

            if (_plugin != null)
            {
                string format = VstHost.GetPluginFormatDisplayName(_plugin.Format);

                using (Font font = new Font(Font.FontFamily, 6.5f, FontStyle.Bold))
                using (SolidBrush text = new SolidBrush(VstRackTheme.TextSecondary))
                using (StringFormat sf = CreateCenteredFormat())
                {
                    SizeF size = g.MeasureString(format, font);
                    Rectangle badge = new Rectangle(
                        badgeRight - (int)size.Width - ScaleBy(2), (Height - ScaleBy(11)) / 2,
                        (int)size.Width + ScaleBy(2), ScaleBy(11));

                    g.DrawString(format, font, text, badge, sf);
                    nameRight = badge.Left - ScaleBy(4);
                }
            }

            int nameLeft = _removeRect.Right + ScaleBy(6);

            if (nameRight > nameLeft)
            {
                using (SolidBrush brush = new SolidBrush(IsDimmed() ? VstRackTheme.TextSecondary : VstRackTheme.TextPrimary))
                using (Font font = new Font(Font.FontFamily, 8f, FontStyle.Regular))
                using (StringFormat sf = CreateTrimmedFormat())
                {
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(
                        _plugin != null ? VstHost.GetPluginDisplayName(_plugin) : string.Empty,
                        font, brush, new Rectangle(nameLeft, 0, nameRight - nameLeft, Height), sf);
                }
            }

            PaintBorder(g);
        }

        private void PaintAddBay(Graphics g)
        {
            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            bool hover = _hoverRegion == VstRackHitRegion.AddBay;

            using (GraphicsPath path = CreateRoundedPath(bounds, ScaleBy(4)))
            using (SolidBrush fill = new SolidBrush(hover ? VstRackTheme.Inset : VstRackTheme.Gutter))
            using (Pen pen = new Pen(hover ? VstRackTheme.SelectionBorder : VstRackTheme.Border))
            {
                pen.DashStyle = DashStyle.Dash;
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }

            using (SolidBrush text = new SolidBrush(hover ? VstRackTheme.TextPrimary : VstRackTheme.TextSecondary))
            using (StringFormat format = CreateCenteredFormat())
            using (Font font = new Font(Font.FontFamily, 8.5f, FontStyle.Regular))
            {
                g.DrawString("+  Add plugin", font, text, bounds, format);
            }
        }

        private void PaintGutter(Graphics g)
        {
            Rectangle gutterRect = new Rectangle(0, 0, ScaleBy(GutterWidthDip), Height);

            using (SolidBrush fill = new SolidBrush(VstRackTheme.Gutter))
                g.FillRectangle(fill, gutterRect);

            using (SolidBrush text = new SolidBrush(VstRackTheme.TextSecondary))
            using (StringFormat format = new StringFormat())
            using (Font font = new Font(Font.FontFamily, 8f, FontStyle.Bold))
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Near;

                Rectangle numberRect = new Rectangle(0, ScaleBy(8), gutterRect.Width, ScaleBy(16));
                g.DrawString((_index + 1).ToString(), font, text, numberRect, format);
            }
        }

        private void PaintFaceplate(Graphics g)
        {
            Rectangle face = new Rectangle(ScaleBy(GutterWidthDip), 0,
                Math.Max(1, Width - ScaleBy(GutterWidthDip) - 1), Math.Max(1, Height - 1));

            if (face.Height <= 0 || face.Width <= 0)
                return;

            Color top = VstRackTheme.FaceTop;
            Color bottom = VstRackTheme.FaceBottom;

            if (IsDimmed())
            {
                top = Dim(top);
                bottom = Dim(bottom);
            }

            if (_selected)
            {
                top = Lighten(top, 0.10f);
                bottom = Lighten(bottom, 0.10f);
            }

            using (GraphicsPath path = CreateRoundedPath(face, ScaleBy(4)))
            using (LinearGradientBrush brush = new LinearGradientBrush(
                new Rectangle(face.X, face.Y, face.Width, Math.Max(1, face.Height)),
                top, bottom, LinearGradientMode.Vertical))
            {
                g.FillPath(brush, path);
            }

            // Light top bevel, as on a real faceplate.
            using (Pen bevel = new Pen(Color.FromArgb(90, VstRackTheme.Bevel)))
                g.DrawLine(bevel, face.Left + ScaleBy(3), face.Top + 1, face.Right - ScaleBy(3), face.Top + 1);
        }

        private void PaintHeaderContent(Graphics g)
        {
            PaintIcon(g, _enableRect, VstRackHitRegion.Enable);
            PaintIcon(g, _editorRect, VstRackHitRegion.Editor);
            PaintIcon(g, _bypassRect, VstRackHitRegion.Bypass);
            PaintIcon(g, _removeRect, VstRackHitRegion.Remove);
            PaintCollapseChevron(g);

            string name = _plugin != null ? VstHost.GetPluginDisplayName(_plugin) : string.Empty;
            int nameLeft = _removeRect.Right + ScaleBy(10);
            int nameRight = _collapseRect.Left - ScaleBy(6);
            Rectangle nameRect = new Rectangle(nameLeft, ScaleBy(4),
                Math.Max(0, nameRight - nameLeft), ScaleBy(20));

            if (nameRect.Width > 0)
            {
                using (SolidBrush brush = new SolidBrush(IsDimmed() ? VstRackTheme.TextSecondary : VstRackTheme.TextPrimary))
                using (Font font = new Font(Font.FontFamily, 10.5f, FontStyle.Regular))
                using (StringFormat format = CreateTrimmedFormat())
                {
                    format.Alignment = StringAlignment.Far;
                    format.LineAlignment = StringAlignment.Center;
                    g.DrawString(name, font, brush, nameRect, format);
                }
            }

            PaintStatusRow(g);
        }

        private void PaintStatusRow(Graphics g)
        {
            if (_plugin == null)
                return;

            int y = ScaleBy(26);
            int left = ScaleBy(GutterWidthDip) + ScaleBy(6);

            // Format badge.
            string format = VstHost.GetPluginFormatDisplayName(_plugin.Format);
            using (Font font = new Font(Font.FontFamily, 7f, FontStyle.Bold))
            {
                SizeF size = g.MeasureString(format, font);
                Rectangle badge = new Rectangle(left, y, (int)size.Width + ScaleBy(8), ScaleBy(14));

                using (GraphicsPath path = CreateRoundedPath(badge, ScaleBy(2)))
                using (SolidBrush fill = new SolidBrush(VstRackTheme.Inset))
                    g.FillPath(fill, path);

                using (SolidBrush text = new SolidBrush(VstRackTheme.TextSecondary))
                using (StringFormat sf = CreateCenteredFormat())
                    g.DrawString(format, font, text, badge, sf);

                left = badge.Right + ScaleBy(8);
            }

            // Load-state LED plus label.
            Color led = GetLoadStateColor();
            int ledSize = ScaleBy(7);
            Rectangle ledRect = new Rectangle(left, y + ScaleBy(4), ledSize, ledSize);

            using (SolidBrush brush = new SolidBrush(led))
                g.FillEllipse(brush, ledRect);

            using (Font font = new Font(Font.FontFamily, 7.5f, FontStyle.Regular))
            using (SolidBrush text = new SolidBrush(VstRackTheme.TextSecondary))
            {
                string label = VstHost.GetLoadStateDisplayName(_plugin.LoadState);

                if (!_plugin.Enabled)
                    label += " · disabled";
                if (IsBypassed)
                    label += " · bypassed";

                Rectangle labelRect = new Rectangle(ledRect.Right + ScaleBy(4), y,
                    Math.Max(0, _collapseRect.Left - ledRect.Right - ScaleBy(10)), ScaleBy(14));

                using (StringFormat sf = CreateTrimmedFormat())
                {
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(label, font, text, labelRect, sf);
                }
            }
        }

        private Color GetLoadStateColor()
        {
            if (_plugin == null)
                return VstRackTheme.LedOff;

            switch (_plugin.LoadState)
            {
                case VstPluginLoadState.Active:
                    return _plugin.Enabled && !IsBypassed ? VstRackTheme.LedActive : VstRackTheme.LedOff;
                case VstPluginLoadState.DescriptorOnly:
                    return VstRackTheme.LedDescriptor;
                case VstPluginLoadState.Failed:
                    return VstRackTheme.LedFailed;
                default:
                    return VstRackTheme.LedOff;
            }
        }

        private void PaintIcon(Graphics g, Rectangle rect, VstRackHitRegion region)
        {
            bool hover = _hoverRegion == region;

            if (hover)
            {
                using (GraphicsPath path = CreateRoundedPath(rect, ScaleBy(3)))
                using (SolidBrush fill = new SolidBrush(VstRackTheme.IconHover))
                    g.FillPath(fill, path);
            }

            Color stroke = VstRackTheme.TextSecondary;

            if (region == VstRackHitRegion.Enable && _plugin != null && _plugin.Enabled)
                stroke = VstRackTheme.AccentOn;
            if (region == VstRackHitRegion.Bypass && IsBypassed)
                stroke = VstRackTheme.LedDescriptor;
            if (hover)
                stroke = VstRackTheme.TextPrimary;

            using (Pen pen = new Pen(stroke, Math.Max(1f, DpiScale)))
            {
                Rectangle inner = Rectangle.Inflate(rect, -ScaleBy(4), -ScaleBy(4));

                switch (region)
                {
                    case VstRackHitRegion.Enable:
                        // Power symbol: broken ring with a vertical stem.
                        g.DrawArc(pen, inner, -60, 300);
                        g.DrawLine(pen, inner.Left + inner.Width / 2, inner.Top - ScaleBy(1),
                            inner.Left + inner.Width / 2, inner.Top + inner.Height / 2);
                        break;

                    case VstRackHitRegion.Editor:
                        using (Font font = new Font(Font.FontFamily, 7.5f, FontStyle.Bold))
                        using (SolidBrush brush = new SolidBrush(stroke))
                        using (StringFormat sf = CreateCenteredFormat())
                            g.DrawString("e", font, brush, rect, sf);
                        break;

                    case VstRackHitRegion.Bypass:
                        // Circle with a slash.
                        g.DrawEllipse(pen, inner);
                        g.DrawLine(pen, inner.Left + ScaleBy(1), inner.Bottom - ScaleBy(1),
                            inner.Right - ScaleBy(1), inner.Top + ScaleBy(1));
                        break;

                    case VstRackHitRegion.Remove:
                        g.DrawLine(pen, inner.Left, inner.Top, inner.Right, inner.Bottom);
                        g.DrawLine(pen, inner.Right, inner.Top, inner.Left, inner.Bottom);
                        break;
                }
            }
        }

        private void PaintCollapseChevron(Graphics g)
        {
            bool hover = _hoverRegion == VstRackHitRegion.Collapse;

            using (Pen pen = new Pen(hover ? VstRackTheme.TextPrimary : VstRackTheme.TextSecondary, Math.Max(1f, DpiScale)))
            {
                int cx = _collapseRect.Left + _collapseRect.Width / 2;
                int cy = _collapseRect.Top + _collapseRect.Height / 2;
                int w = ScaleBy(4);

                if (_collapsed)
                {
                    g.DrawLine(pen, cx - w, cy - w / 2, cx, cy + w / 2);
                    g.DrawLine(pen, cx, cy + w / 2, cx + w, cy - w / 2);
                }
                else
                {
                    g.DrawLine(pen, cx - w, cy + w / 2, cx, cy - w / 2);
                    g.DrawLine(pen, cx, cy - w / 2, cx + w, cy + w / 2);
                }
            }
        }

        private void PaintBody(Graphics g)
        {
            Rectangle artworkRect;
            Rectangle knobStripRect;

            GetBodyRegions(out artworkRect, out knobStripRect);

            Rectangle inset = Rectangle.Inflate(artworkRect, -ScaleBy(6), -ScaleBy(4));
            inset.Height -= ScaleBy(2);

            if (inset.Width <= 0 || inset.Height <= 0)
                return;

            using (GraphicsPath path = CreateRoundedPath(inset, ScaleBy(3)))
            using (SolidBrush fill = new SolidBrush(VstRackTheme.Inset))
            {
                g.FillPath(fill, path);
            }

            // With snapshots switched off nothing is requested at all, so the
            // toggle also suppresses vendor artwork and costs no disk access.
            Image art = _plugin != null && VstHost.UiState.ShowSnapshots
                ? VstPluginArt.GetOrRequest(_plugin.Path, _plugin.ClassId, OnArtworkLoaded)
                : null;

            if (art != null)
                PaintArtwork(g, inset, art);
            else
                PaintPlaceholderFace(g, inset);

            // Hovering the body signals "click to open the editor".
            if (_hoverRegion == VstRackHitRegion.Body)
            {
                using (Pen pen = new Pen(Color.FromArgb(120, VstRackTheme.AccentOn)))
                using (GraphicsPath path = CreateRoundedPath(inset, ScaleBy(3)))
                    g.DrawPath(pen, path);
            }
        }

        private void OnArtworkLoaded()
        {
            // Raised from a thread pool thread once a snapshot finishes decoding.
            if (IsDisposed || Disposing || !IsHandleCreated)
                return;

            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (!IsDisposed && !Disposing)
                        Invalidate();
                });
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void PaintArtwork(Graphics g, Rectangle bounds, Image art)
        {
            // Preserve aspect ratio, letterboxed inside the inset.
            float scale = Math.Min((float)bounds.Width / art.Width, (float)bounds.Height / art.Height);
            int width = Math.Max(1, (int)(art.Width * scale));
            int height = Math.Max(1, (int)(art.Height * scale));
            Rectangle target = new Rectangle(
                bounds.Left + (bounds.Width - width) / 2,
                bounds.Top + (bounds.Height - height) / 2,
                width, height);

            // Save/Restore rather than stashing g.Clip — the Region that
            // property returns is a fresh GDI object the caller must dispose,
            // and leaking one per paint adds up fast on a repainting rack.
            GraphicsState savedState = g.Save();

            try
            {
                using (GraphicsPath path = CreateRoundedPath(bounds, ScaleBy(3)))
                    g.SetClip(path);

                g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                if (IsDimmed())
                {
                    using (System.Drawing.Imaging.ImageAttributes attributes = CreateDimAttributes())
                    {
                        g.DrawImage(art, target, 0, 0, art.Width, art.Height,
                            GraphicsUnit.Pixel, attributes);
                    }
                }
                else
                {
                    g.DrawImage(art, target);
                }
            }
            finally
            {
                g.Restore(savedState);
            }
        }

        private static System.Drawing.Imaging.ImageAttributes CreateDimAttributes()
        {
            // Desaturate toward luminance and darken, so a disabled or bypassed
            // unit reads as inactive without hiding what plugin it is.
            System.Drawing.Imaging.ImageAttributes attributes = new System.Drawing.Imaging.ImageAttributes();
            float[][] matrix =
            {
                new float[] { 0.36f, 0.36f, 0.36f, 0f, 0f },
                new float[] { 0.35f, 0.35f, 0.35f, 0f, 0f },
                new float[] { 0.14f, 0.14f, 0.14f, 0f, 0f },
                new float[] { 0f, 0f, 0f, 1f, 0f },
                new float[] { 0f, 0f, 0f, 0f, 1f }
            };

            attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix(matrix));
            return attributes;
        }

        private void PaintPlaceholderFace(Graphics g, Rectangle bounds)
        {
            // Brushed-metal faceplate drawn rather than shipped as an asset.
            using (GraphicsPath path = CreateRoundedPath(bounds, ScaleBy(3)))
            using (LinearGradientBrush brush = new LinearGradientBrush(
                new Rectangle(bounds.X, bounds.Y, bounds.Width, Math.Max(1, bounds.Height)),
                Color.FromArgb(0x45, 0x45, 0x45), Color.FromArgb(0x2E, 0x2E, 0x2E),
                LinearGradientMode.Vertical))
            {
                g.FillPath(brush, path);
            }

            using (Pen grain = new Pen(Color.FromArgb(18, 255, 255, 255)))
            {
                for (int y = bounds.Top + ScaleBy(3); y < bounds.Bottom; y += ScaleBy(3))
                    g.DrawLine(grain, bounds.Left + ScaleBy(2), y, bounds.Right - ScaleBy(2), y);
            }

            // Ventilation slots either side of the name plate, so an art-less
            // unit still reads as a piece of hardware rather than a blank box.
            int ventWidth = ScaleBy(26);
            PaintVents(g, new Rectangle(bounds.Left + ScaleBy(16), bounds.Top + ScaleBy(20), ventWidth,
                Math.Max(1, bounds.Height - ScaleBy(40))));
            PaintVents(g, new Rectangle(bounds.Right - ScaleBy(16) - ventWidth, bounds.Top + ScaleBy(20), ventWidth,
                Math.Max(1, bounds.Height - ScaleBy(40))));

            PaintScrew(g, new Point(bounds.Left + ScaleBy(9), bounds.Top + ScaleBy(9)));
            PaintScrew(g, new Point(bounds.Right - ScaleBy(9), bounds.Top + ScaleBy(9)));
            PaintScrew(g, new Point(bounds.Left + ScaleBy(9), bounds.Bottom - ScaleBy(9)));
            PaintScrew(g, new Point(bounds.Right - ScaleBy(9), bounds.Bottom - ScaleBy(9)));

            string name = _plugin != null ? VstHost.GetPluginDisplayName(_plugin) : string.Empty;

            // Recessed name plate, like a screened badge on a faceplate.
            int plateWidth = Math.Min(Math.Max(ScaleBy(140), bounds.Width / 2), Math.Max(1, bounds.Width - ScaleBy(96)));
            int plateHeight = ScaleBy(34);
            Rectangle plate = new Rectangle(
                bounds.Left + (bounds.Width - plateWidth) / 2,
                bounds.Top + (bounds.Height - plateHeight) / 2,
                Math.Max(1, plateWidth), plateHeight);

            using (GraphicsPath path = CreateRoundedPath(plate, ScaleBy(3)))
            using (LinearGradientBrush brush = new LinearGradientBrush(
                new Rectangle(plate.X, plate.Y, plate.Width, Math.Max(1, plate.Height)),
                Color.FromArgb(0x23, 0x23, 0x23), Color.FromArgb(0x1A, 0x1A, 0x1A),
                LinearGradientMode.Vertical))
            using (Pen edge = new Pen(Color.FromArgb(40, 255, 255, 255)))
            {
                g.FillPath(brush, path);
                g.DrawPath(edge, path);
            }

            using (Font font = new Font(Font.FontFamily, 10f, FontStyle.Regular))
            using (SolidBrush text = new SolidBrush(Color.FromArgb(0xC8, 0xC8, 0xC8)))
            using (StringFormat sf = CreateCenteredFormat())
            {
                Rectangle textRect = Rectangle.Inflate(plate, -ScaleBy(6), 0);
                sf.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(name, font, text, textRect, sf);
            }

            // Only claim artwork is missing when it actually is — with
            // snapshots switched off the plugin may well have a picture.
            if (VstHost.UiState.ShowSnapshots)
            {
                using (Font font = new Font(Font.FontFamily, 6.5f, FontStyle.Regular))
                using (SolidBrush text = new SolidBrush(Color.FromArgb(0x70, 0x70, 0x70)))
                using (StringFormat sf = CreateCenteredFormat())
                {
                    Rectangle noteRect = new Rectangle(bounds.Left, bounds.Bottom - ScaleBy(15), bounds.Width, ScaleBy(12));
                    g.DrawString("no preview artwork", font, text, noteRect, sf);
                }
            }
        }

        private void PaintVents(Graphics g, Rectangle bounds)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;

            int step = ScaleBy(5);

            using (Pen dark = new Pen(Color.FromArgb(70, 0, 0, 0)))
            using (Pen light = new Pen(Color.FromArgb(16, 255, 255, 255)))
            {
                for (int y = bounds.Top; y < bounds.Bottom - step; y += step)
                {
                    g.DrawLine(dark, bounds.Left, y, bounds.Right, y);
                    g.DrawLine(light, bounds.Left, y + 1, bounds.Right, y + 1);
                }
            }
        }

        private void PaintScrew(Graphics g, Point center)
        {
            int r = ScaleBy(3);
            Rectangle rect = new Rectangle(center.X - r, center.Y - r, r * 2, r * 2);

            using (SolidBrush fill = new SolidBrush(Color.FromArgb(0x22, 0x22, 0x22)))
                g.FillEllipse(fill, rect);

            using (Pen pen = new Pen(Color.FromArgb(60, 255, 255, 255)))
                g.DrawEllipse(pen, rect);

            using (Pen slot = new Pen(Color.FromArgb(0x55, 0x55, 0x55)))
                g.DrawLine(slot, rect.Left + 1, center.Y, rect.Right - 1, center.Y);
        }

        private void PaintBorder(Graphics g)
        {
            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);

            using (GraphicsPath path = CreateRoundedPath(bounds, ScaleBy(4)))
            using (Pen pen = new Pen(_selected ? VstRackTheme.SelectionBorder : VstRackTheme.Border,
                _selected ? Math.Max(1.6f, DpiScale * 1.6f) : Math.Max(1f, DpiScale)))
            {
                g.DrawPath(pen, path);
            }
        }

        private bool IsDimmed()
        {
            if (_plugin == null)
                return false;

            return !_plugin.Enabled || IsBypassed || _plugin.LoadState == VstPluginLoadState.Failed;
        }

        #endregion

        #region Drawing helpers

        private static Color Dim(Color color)
        {
            return Color.FromArgb(color.A,
                (int)(color.R * 0.72f),
                (int)(color.G * 0.72f),
                (int)(color.B * 0.72f));
        }

        private static Color Lighten(Color color, float amount)
        {
            return Color.FromArgb(color.A,
                Math.Min(255, (int)(color.R + (255 - color.R) * amount)),
                Math.Min(255, (int)(color.G + (255 - color.G) * amount)),
                Math.Min(255, (int)(color.B + (255 - color.B) * amount)));
        }

        private static StringFormat CreateCenteredFormat()
        {
            StringFormat format = new StringFormat();
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            format.FormatFlags = StringFormatFlags.NoWrap;
            return format;
        }

        private static StringFormat CreateTrimmedFormat()
        {
            StringFormat format = new StringFormat();
            format.Trimming = StringTrimming.EllipsisCharacter;
            format.FormatFlags = StringFormatFlags.NoWrap;
            return format;
        }

        private static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();

            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                path.AddRectangle(bounds);
                return path;
            }

            int diameter = Math.Max(1, radius * 2);

            if (diameter >= bounds.Width || diameter >= bounds.Height)
            {
                path.AddRectangle(bounds);
                return path;
            }

            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();

            return path;
        }

        #endregion
    }
}
