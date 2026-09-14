/*  frm3DPanadapter.cs

This code/file can be found on GitHub : https://github.com/ramdor/Thetis

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program; if not, write to the Free Software
Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
*/

using System;
using System.Drawing;
using System.Windows.Forms;

namespace Thetis
{
    public partial class frm3DPanadapter : Form
    {
        private bool _initializing;
        private int _lineCountBeforeCap = -1;
        private ToolTip toolTip1;

        private CheckBoxTS chk3DWaterfallSync;
        private CheckBoxTS chk3DSideWalls;
        private LabelTS lbl3DXOffset;
        private NumericUpDownTS ud3DXOffset;
        private LabelTS lbl3DYOffset;
        private NumericUpDownTS ud3DYOffset;
        private LabelTS lbl3DRidgeHeight;
        private NumericUpDownTS ud3DRidgeHeight;
        private LabelTS lbl3DHaze;
        private NumericUpDownTS ud3DHaze;
        private LabelTS lbl3DLineCount;
        private NumericUpDownTS ud3DLineCount;
        private LabelTS lbl3DSpeed;
        private NumericUpDownTS ud3DSpeed;
        private LabelTS lbl3DLineColor;
        private ColorButton clrbtn3DLineColor;
        private CheckBoxTS chk3DFillColorEnable;
        private ColorButton clrbtn3DFillColor;
        private TrackBarTS tb3DFillOpacity;
        private LabelTS lbl3DColorMap;
        private ComboBoxTS combo3DColorMap;
        private LabelTS lbl3DZCurve;
        private NumericUpDownTS ud3DZCurve;
        private ButtonTS btn3DResetDefaults;

        public frm3DPanadapter()
        {
            _initializing = true;

            InitializeComponent();

            Common.RestoreForm(this, "3DPanadapter", false);
            Common.ForceFormOnScreen(this);

            _initializing = false;

            PushAllSettings();

            ApplyRenderPathLimits();
        }

        public void ApplyRenderPathLimits()
        {
            bool bCap = Display.RenderPath == Display.DXRenderPath.WarpSoftware;

            _initializing = true;

            if (bCap)
            {
                if (ud3DLineCount.Maximum > Display.Max3DLinesSoftwareRender)
                {
                    if (ud3DLineCount.Value > Display.Max3DLinesSoftwareRender)
                        _lineCountBeforeCap = (int)ud3DLineCount.Value;
                    ud3DLineCount.Maximum = Display.Max3DLinesSoftwareRender;
                }
                toolTip1.SetToolTip(ud3DLineCount, "Number of historical spectrum traces receding into the distance.\nLimited to " + Display.Max3DLinesSoftwareRender + " while software rendering is active (WARP rasterises every stroke on the CPU).");
            }
            else
            {
                if (ud3DLineCount.Maximum == Display.Max3DLinesSoftwareRender)
                {
                    ud3DLineCount.Maximum = Display.Max3DHistoryLines;
                    if (_lineCountBeforeCap > 0)
                    {
                        ud3DLineCount.Value = _lineCountBeforeCap;
                        _lineCountBeforeCap = -1;
                    }
                }
                toolTip1.SetToolTip(ud3DLineCount, "Number of historical spectrum traces receding into the distance.");
            }

            _initializing = false;
        }

        private void InitializeComponent()
        {
            this.toolTip1 = new System.Windows.Forms.ToolTip();
            this.chk3DWaterfallSync = new System.Windows.Forms.CheckBoxTS();
            this.chk3DSideWalls = new System.Windows.Forms.CheckBoxTS();
            this.lbl3DXOffset = new System.Windows.Forms.LabelTS();
            this.ud3DXOffset = new System.Windows.Forms.NumericUpDownTS();
            this.lbl3DYOffset = new System.Windows.Forms.LabelTS();
            this.ud3DYOffset = new System.Windows.Forms.NumericUpDownTS();
            this.lbl3DRidgeHeight = new System.Windows.Forms.LabelTS();
            this.ud3DRidgeHeight = new System.Windows.Forms.NumericUpDownTS();
            this.lbl3DHaze = new System.Windows.Forms.LabelTS();
            this.ud3DHaze = new System.Windows.Forms.NumericUpDownTS();
            this.lbl3DLineCount = new System.Windows.Forms.LabelTS();
            this.ud3DLineCount = new System.Windows.Forms.NumericUpDownTS();
            this.lbl3DSpeed = new System.Windows.Forms.LabelTS();
            this.ud3DSpeed = new System.Windows.Forms.NumericUpDownTS();
            this.lbl3DLineColor = new System.Windows.Forms.LabelTS();
            this.clrbtn3DLineColor = new Thetis.ColorButton();
            this.chk3DFillColorEnable = new System.Windows.Forms.CheckBoxTS();
            this.clrbtn3DFillColor = new Thetis.ColorButton();
            this.tb3DFillOpacity = new System.Windows.Forms.TrackBarTS();
            this.lbl3DColorMap = new System.Windows.Forms.LabelTS();
            this.combo3DColorMap = new System.Windows.Forms.ComboBoxTS();
            this.lbl3DZCurve = new System.Windows.Forms.LabelTS();
            this.ud3DZCurve = new System.Windows.Forms.NumericUpDownTS();
            this.btn3DResetDefaults = new System.Windows.Forms.ButtonTS();
            this.SuspendLayout();
            //
            // chk3DWaterfallSync
            //
            this.chk3DWaterfallSync.AutoSize = true;
            this.chk3DWaterfallSync.Checked = true;
            this.chk3DWaterfallSync.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chk3DWaterfallSync.Image = null;
            this.chk3DWaterfallSync.Location = new System.Drawing.Point(12, 12);
            this.chk3DWaterfallSync.Name = "chk3DWaterfallSync";
            this.chk3DWaterfallSync.Size = new System.Drawing.Size(105, 17);
            this.chk3DWaterfallSync.TabIndex = 0;
            this.chk3DWaterfallSync.Text = "Waterfall Sync";
            this.toolTip1.SetToolTip(this.chk3DWaterfallSync, "Use waterfall palette and levels for 3D colors (overrides Color and Colormap). A enabled Fill Color overrides this for the front fill.");
            this.chk3DWaterfallSync.CheckedChanged += new System.EventHandler(this.chk3DWaterfallSync_CheckedChanged);
            //
            // chk3DSideWalls
            //
            this.chk3DSideWalls.AutoSize = true;
            this.chk3DSideWalls.Checked = true;
            this.chk3DSideWalls.CheckState = System.Windows.Forms.CheckState.Checked;
            this.chk3DSideWalls.Image = null;
            this.chk3DSideWalls.Location = new System.Drawing.Point(12, 34);
            this.chk3DSideWalls.Name = "chk3DSideWalls";
            this.chk3DSideWalls.Size = new System.Drawing.Size(89, 17);
            this.chk3DSideWalls.TabIndex = 1;
            this.chk3DSideWalls.Text = "Side Walls";
            this.toolTip1.SetToolTip(this.chk3DSideWalls, "Draw darkened left/right side walls for a solid object look.");
            this.chk3DSideWalls.CheckedChanged += new System.EventHandler(this.chk3DSideWalls_CheckedChanged);
            //
            // lbl3DXOffset (Perspective)
            //
            this.lbl3DXOffset.Image = null;
            this.lbl3DXOffset.Location = new System.Drawing.Point(12, 62);
            this.lbl3DXOffset.Name = "lbl3DXOffset";
            this.lbl3DXOffset.Size = new System.Drawing.Size(90, 16);
            this.lbl3DXOffset.TabIndex = 2;
            this.lbl3DXOffset.Text = "Perspective:";
            //
            // ud3DXOffset (Perspective: 0.1-1.0, default 0.60)
            //
            this.ud3DXOffset.DecimalPlaces = 2;
            this.ud3DXOffset.Increment = 0.05m;
            this.ud3DXOffset.Location = new System.Drawing.Point(120, 59);
            this.ud3DXOffset.Maximum = 1.00m;
            this.ud3DXOffset.Minimum = 0.10m;
            this.ud3DXOffset.Name = "ud3DXOffset";
            this.ud3DXOffset.Size = new System.Drawing.Size(56, 20);
            this.ud3DXOffset.TabIndex = 3;
            this.toolTip1.SetToolTip(this.ud3DXOffset, "How much back rows narrow (0.1=wide, 1.0=no narrowing).");
            this.ud3DXOffset.Value = 0.60m;
            this.ud3DXOffset.ValueChanged += new System.EventHandler(this.ud3DXOffset_ValueChanged);
            //
            // lbl3DYOffset (Depth)
            //
            this.lbl3DYOffset.Image = null;
            this.lbl3DYOffset.Location = new System.Drawing.Point(12, 84);
            this.lbl3DYOffset.Name = "lbl3DYOffset";
            this.lbl3DYOffset.Size = new System.Drawing.Size(90, 16);
            this.lbl3DYOffset.TabIndex = 4;
            this.lbl3DYOffset.Text = "Depth:";
            //
            // ud3DYOffset (Depth: 0.0-1.0, default 0.58)
            //
            this.ud3DYOffset.DecimalPlaces = 2;
            this.ud3DYOffset.Increment = 0.05m;
            this.ud3DYOffset.Location = new System.Drawing.Point(120, 81);
            this.ud3DYOffset.Maximum = 1.00m;
            this.ud3DYOffset.Minimum = 0.00m;
            this.ud3DYOffset.Name = "ud3DYOffset";
            this.ud3DYOffset.Size = new System.Drawing.Size(56, 20);
            this.ud3DYOffset.TabIndex = 5;
            this.toolTip1.SetToolTip(this.ud3DYOffset, "How far back rows rise (0=flat, 1.0=maximum depth).");
            this.ud3DYOffset.Value = 0.58m;
            this.ud3DYOffset.ValueChanged += new System.EventHandler(this.ud3DYOffset_ValueChanged);
            //
            // lbl3DRidgeHeight
            //
            this.lbl3DRidgeHeight.Image = null;
            this.lbl3DRidgeHeight.Location = new System.Drawing.Point(12, 106);
            this.lbl3DRidgeHeight.Name = "lbl3DRidgeHeight";
            this.lbl3DRidgeHeight.Size = new System.Drawing.Size(90, 16);
            this.lbl3DRidgeHeight.TabIndex = 6;
            this.lbl3DRidgeHeight.Text = "Ridge Ht:";
            //
            // ud3DRidgeHeight
            //
            this.ud3DRidgeHeight.DecimalPlaces = 2;
            this.ud3DRidgeHeight.Increment = 0.02m;
            this.ud3DRidgeHeight.Location = new System.Drawing.Point(120, 103);
            this.ud3DRidgeHeight.Maximum = 1.00m;
            this.ud3DRidgeHeight.Minimum = 0.10m;
            this.ud3DRidgeHeight.Name = "ud3DRidgeHeight";
            this.ud3DRidgeHeight.Size = new System.Drawing.Size(56, 20);
            this.ud3DRidgeHeight.TabIndex = 7;
            this.toolTip1.SetToolTip(this.ud3DRidgeHeight, "Height of the front ridge as a fraction of plot height (0.1-1.0).");
            this.ud3DRidgeHeight.Value = 0.46m;
            this.ud3DRidgeHeight.ValueChanged += new System.EventHandler(this.ud3DRidgeHeight_ValueChanged);
            //
            // lbl3DHaze
            //
            this.lbl3DHaze.Image = null;
            this.lbl3DHaze.Location = new System.Drawing.Point(12, 128);
            this.lbl3DHaze.Name = "lbl3DHaze";
            this.lbl3DHaze.Size = new System.Drawing.Size(90, 16);
            this.lbl3DHaze.TabIndex = 8;
            this.lbl3DHaze.Text = "Haze:";
            //
            // ud3DHaze
            //
            this.ud3DHaze.DecimalPlaces = 2;
            this.ud3DHaze.Increment = 0.02m;
            this.ud3DHaze.Location = new System.Drawing.Point(120, 125);
            this.ud3DHaze.Maximum = 1.00m;
            this.ud3DHaze.Minimum = 0.00m;
            this.ud3DHaze.Name = "ud3DHaze";
            this.ud3DHaze.Size = new System.Drawing.Size(56, 20);
            this.ud3DHaze.TabIndex = 9;
            this.toolTip1.SetToolTip(this.ud3DHaze, "Atmospheric haze strength - dims back rows (0=none, 1.0=full haze).");
            this.ud3DHaze.Value = 0.16m;
            this.ud3DHaze.ValueChanged += new System.EventHandler(this.ud3DHaze_ValueChanged);
            //
            // lbl3DLineCount
            //
            this.lbl3DLineCount.Image = null;
            this.lbl3DLineCount.Location = new System.Drawing.Point(12, 150);
            this.lbl3DLineCount.Name = "lbl3DLineCount";
            this.lbl3DLineCount.Size = new System.Drawing.Size(90, 16);
            this.lbl3DLineCount.TabIndex = 10;
            this.lbl3DLineCount.Text = "Depth Lines:";
            //
            // ud3DLineCount
            //
            this.ud3DLineCount.Location = new System.Drawing.Point(120, 147);
            this.ud3DLineCount.Maximum = new decimal(new int[] { 60, 0, 0, 0 });
            this.ud3DLineCount.Minimum = new decimal(new int[] { 2, 0, 0, 0 });
            this.ud3DLineCount.Name = "ud3DLineCount";
            this.ud3DLineCount.Size = new System.Drawing.Size(56, 20);
            this.ud3DLineCount.TabIndex = 11;
            this.toolTip1.SetToolTip(this.ud3DLineCount, "Number of historical spectrum traces receding into the distance.");
            this.ud3DLineCount.Value = new decimal(new int[] { 35, 0, 0, 0 });
            this.ud3DLineCount.ValueChanged += new System.EventHandler(this.ud3DLineCount_ValueChanged);
            //
            // lbl3DSpeed
            //
            this.lbl3DSpeed.Image = null;
            this.lbl3DSpeed.Location = new System.Drawing.Point(12, 172);
            this.lbl3DSpeed.Name = "lbl3DSpeed";
            this.lbl3DSpeed.Size = new System.Drawing.Size(90, 16);
            this.lbl3DSpeed.TabIndex = 12;
            this.lbl3DSpeed.Text = "Speed:";
            //
            // ud3DSpeed
            //
            this.ud3DSpeed.Location = new System.Drawing.Point(120, 169);
            this.ud3DSpeed.Maximum = new decimal(new int[] { 60, 0, 0, 0 });
            this.ud3DSpeed.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
            this.ud3DSpeed.Name = "ud3DSpeed";
            this.ud3DSpeed.Size = new System.Drawing.Size(56, 20);
            this.ud3DSpeed.TabIndex = 13;
            this.toolTip1.SetToolTip(this.ud3DSpeed, "How fast new rows are pushed (1-60 FPS). Low values give a slow, cinematic scroll.");
            this.ud3DSpeed.Value = new decimal(new int[] { 25, 0, 0, 0 });
            this.ud3DSpeed.ValueChanged += new System.EventHandler(this.ud3DSpeed_ValueChanged);
            //
            // lbl3DLineColor
            //
            this.lbl3DLineColor.Image = null;
            this.lbl3DLineColor.Location = new System.Drawing.Point(12, 198);
            this.lbl3DLineColor.Name = "lbl3DLineColor";
            this.lbl3DLineColor.Size = new System.Drawing.Size(90, 16);
            this.lbl3DLineColor.TabIndex = 14;
            this.lbl3DLineColor.Text = "Color:";
            //
            // clrbtn3DLineColor
            //
            this.clrbtn3DLineColor.Automatic = "Automatic";
            this.clrbtn3DLineColor.Color = System.Drawing.Color.Aquamarine;
            this.clrbtn3DLineColor.Image = null;
            this.clrbtn3DLineColor.Location = new System.Drawing.Point(120, 195);
            this.clrbtn3DLineColor.MoreColors = "More Colors...";
            this.clrbtn3DLineColor.Name = "clrbtn3DLineColor";
            this.clrbtn3DLineColor.Selectable = true;
            this.clrbtn3DLineColor.Size = new System.Drawing.Size(40, 23);
            this.clrbtn3DLineColor.TabIndex = 15;
            this.toolTip1.SetToolTip(this.clrbtn3DLineColor, "Ridge outline color (used when waterfall palette is off).");
            this.clrbtn3DLineColor.Changed += new System.EventHandler(this.clrbtn3DLineColor_Changed);
            //
            // chk3DFillColorEnable
            //
            this.chk3DFillColorEnable.AutoSize = true;
            this.chk3DFillColorEnable.Image = null;
            this.chk3DFillColorEnable.Location = new System.Drawing.Point(12, 226);
            this.chk3DFillColorEnable.Name = "chk3DFillColorEnable";
            this.chk3DFillColorEnable.Size = new System.Drawing.Size(100, 17);
            this.chk3DFillColorEnable.TabIndex = 16;
            this.chk3DFillColorEnable.Text = "Fill Color";
            this.toolTip1.SetToolTip(this.chk3DFillColorEnable, "Draw the front wall as a solid panel in this color, like the 2D pan fill (opacity slider below). The 3D history receding behind it is not affected.");
            this.chk3DFillColorEnable.CheckedChanged += new System.EventHandler(this.chk3DFillColorEnable_CheckedChanged);
            //
            // clrbtn3DFillColor
            //
            this.clrbtn3DFillColor.Automatic = "Automatic";
            this.clrbtn3DFillColor.Color = System.Drawing.Color.Aquamarine;
            this.clrbtn3DFillColor.Image = null;
            this.clrbtn3DFillColor.Location = new System.Drawing.Point(120, 225);
            this.clrbtn3DFillColor.MoreColors = "More Colors...";
            this.clrbtn3DFillColor.Name = "clrbtn3DFillColor";
            this.clrbtn3DFillColor.Selectable = true;
            this.clrbtn3DFillColor.Size = new System.Drawing.Size(40, 23);
            this.clrbtn3DFillColor.TabIndex = 17;
            this.toolTip1.SetToolTip(this.clrbtn3DFillColor, "Front data fill color (3D only). Does not affect the data line.");
            this.clrbtn3DFillColor.Changed += new System.EventHandler(this.clrbtn3DFillColor_Changed);
            //
            // tb3DFillOpacity
            //
            this.tb3DFillOpacity.AutoSize = false;
            this.tb3DFillOpacity.Location = new System.Drawing.Point(165, 225);
            this.tb3DFillOpacity.Maximum = 100;
            this.tb3DFillOpacity.Minimum = 0;
            this.tb3DFillOpacity.Name = "tb3DFillOpacity";
            this.tb3DFillOpacity.Size = new System.Drawing.Size(60, 23);
            this.tb3DFillOpacity.SmallChange = 5;
            this.tb3DFillOpacity.LargeChange = 10;
            this.tb3DFillOpacity.TabIndex = 18;
            this.tb3DFillOpacity.TickStyle = System.Windows.Forms.TickStyle.None;
            this.tb3DFillOpacity.Value = 55;
            this.toolTip1.SetToolTip(this.tb3DFillOpacity, "Fill Color opacity (0-100%). Default 55.");
            this.tb3DFillOpacity.Scroll += new System.EventHandler(this.tb3DFillOpacity_Scroll);
            //
            // lbl3DColorMap
            //
            this.lbl3DColorMap.Image = null;
            this.lbl3DColorMap.Location = new System.Drawing.Point(12, 258);
            this.lbl3DColorMap.Name = "lbl3DColorMap";
            this.lbl3DColorMap.Size = new System.Drawing.Size(90, 16);
            this.lbl3DColorMap.TabIndex = 18;
            this.lbl3DColorMap.Text = "Colormap:";
            //
            // combo3DColorMap
            //
            this.combo3DColorMap.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.combo3DColorMap.Items.AddRange(new object[] {
                "Classic",
                "Turbo",
                "Viridis",
                "Inferno"});
            this.combo3DColorMap.Location = new System.Drawing.Point(120, 255);
            this.combo3DColorMap.Name = "combo3DColorMap";
            this.combo3DColorMap.Size = new System.Drawing.Size(100, 21);
            this.combo3DColorMap.TabIndex = 19;
            this.toolTip1.SetToolTip(this.combo3DColorMap, "Perceptual surface colormap (ignored while Waterfall Sync is checked).");
            this.combo3DColorMap.SelectedIndexChanged += new System.EventHandler(this.combo3DColorMap_SelectedIndexChanged);
            //
            // lbl3DZCurve (Floor Lift)
            //
            this.lbl3DZCurve.Image = null;
            this.lbl3DZCurve.Location = new System.Drawing.Point(12, 280);
            this.lbl3DZCurve.Name = "lbl3DZCurve";
            this.lbl3DZCurve.Size = new System.Drawing.Size(90, 16);
            this.lbl3DZCurve.TabIndex = 20;
            this.lbl3DZCurve.Text = "Floor Lift:";
            //
            // ud3DZCurve (Floor Lift: 0.05-1.0, default 0.90)
            //
            this.ud3DZCurve.DecimalPlaces = 2;
            this.ud3DZCurve.Increment = 0.05m;
            this.ud3DZCurve.Location = new System.Drawing.Point(120, 277);
            this.ud3DZCurve.Maximum = 1.00m;
            this.ud3DZCurve.Minimum = 0.05m;
            this.ud3DZCurve.Name = "ud3DZCurve";
            this.ud3DZCurve.Size = new System.Drawing.Size(56, 20);
            this.ud3DZCurve.TabIndex = 21;
            this.toolTip1.SetToolTip(this.ud3DZCurve, "Lifts the noise floor up into the surface (lower = more floor).");
            this.ud3DZCurve.Value = 0.90m;
            this.ud3DZCurve.ValueChanged += new System.EventHandler(this.ud3DZCurve_ValueChanged);
            //
            // btn3DResetDefaults
            //
            this.btn3DResetDefaults.Image = null;
            this.btn3DResetDefaults.Location = new System.Drawing.Point(12, 315);
            this.btn3DResetDefaults.Name = "btn3DResetDefaults";
            this.btn3DResetDefaults.Size = new System.Drawing.Size(208, 26);
            this.btn3DResetDefaults.TabIndex = 22;
            this.btn3DResetDefaults.Text = "Reset Defaults";
            this.toolTip1.SetToolTip(this.btn3DResetDefaults, "Reset all 3D panadapter settings to defaults.");
            this.btn3DResetDefaults.Click += new System.EventHandler(this.btn3DResetDefaults_Click);
            //
            // frm3DPanadapter
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(232, 353);
            this.Controls.Add(this.chk3DWaterfallSync);
            this.Controls.Add(this.chk3DSideWalls);
            this.Controls.Add(this.lbl3DXOffset);
            this.Controls.Add(this.ud3DXOffset);
            this.Controls.Add(this.lbl3DYOffset);
            this.Controls.Add(this.ud3DYOffset);
            this.Controls.Add(this.lbl3DRidgeHeight);
            this.Controls.Add(this.ud3DRidgeHeight);
            this.Controls.Add(this.lbl3DHaze);
            this.Controls.Add(this.ud3DHaze);
            this.Controls.Add(this.lbl3DLineCount);
            this.Controls.Add(this.ud3DLineCount);
            this.Controls.Add(this.lbl3DSpeed);
            this.Controls.Add(this.ud3DSpeed);
            this.Controls.Add(this.lbl3DLineColor);
            this.Controls.Add(this.clrbtn3DLineColor);
            this.Controls.Add(this.chk3DFillColorEnable);
            this.Controls.Add(this.clrbtn3DFillColor);
            this.Controls.Add(this.tb3DFillOpacity);
            this.Controls.Add(this.lbl3DColorMap);
            this.Controls.Add(this.combo3DColorMap);
            this.Controls.Add(this.lbl3DZCurve);
            this.Controls.Add(this.ud3DZCurve);
            this.Controls.Add(this.btn3DResetDefaults);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "frm3DPanadapter";
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "3D Panadapter Settings";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.frm3DPanadapter_FormClosing);
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        private void PushAllSettings()
        {
            Display.Pan3DWaterfallSync = chk3DWaterfallSync.Checked;
            Display.Pan3DSideWalls = chk3DSideWalls.Checked;
            Display.Pan3DPerspective = (float)ud3DXOffset.Value;
            Display.Pan3DDepth = (float)ud3DYOffset.Value;
            Display.Pan3DRidgeHeight = (float)ud3DRidgeHeight.Value;
            Display.Pan3DDepthFade = (float)ud3DHaze.Value;
            Display.Pan3DLineCount = (int)ud3DLineCount.Value;
            Display.Pan3DZCurve = (float)ud3DZCurve.Value;
            Display.Pan3DSpeed = (int)ud3DSpeed.Value;
            Display.Pan3DLineColor = clrbtn3DLineColor.Color;
            Display.Pan3DFillColorEnabled = chk3DFillColorEnable.Checked;
            Display.Pan3DFillColor = clrbtn3DFillColor.Color;
            Display.Pan3DFillAlpha = tb3DFillOpacity.Value / 100f;
            Display.Pan3DColorMap = Math.Max(0, combo3DColorMap.SelectedIndex);
        }

        private void frm3DPanadapter_FormClosing(object sender, FormClosingEventArgs e)
        {
            Common.SaveForm(this, "3DPanadapter");

            if (e.CloseReason == CloseReason.UserClosing)
            {
                Hide();
                e.Cancel = true;
            }
        }

        private void chk3DWaterfallSync_CheckedChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DWaterfallSync = chk3DWaterfallSync.Checked;
        }

        private void chk3DSideWalls_CheckedChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DSideWalls = chk3DSideWalls.Checked;
        }

        private void ud3DXOffset_ValueChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DPerspective = (float)ud3DXOffset.Value;
        }

        private void ud3DYOffset_ValueChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DDepth = (float)ud3DYOffset.Value;
        }

        private void ud3DRidgeHeight_ValueChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DRidgeHeight = (float)ud3DRidgeHeight.Value;
        }

        private void ud3DHaze_ValueChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DDepthFade = (float)ud3DHaze.Value;
        }

        private void ud3DLineCount_ValueChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            _lineCountBeforeCap = -1;
            Display.Pan3DLineCount = (int)ud3DLineCount.Value;
        }

        private void ud3DZCurve_ValueChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DZCurve = (float)ud3DZCurve.Value;
        }

        private void ud3DSpeed_ValueChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DSpeed = (int)ud3DSpeed.Value;
        }

        private void clrbtn3DLineColor_Changed(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DLineColor = clrbtn3DLineColor.Color;
        }

        private void chk3DFillColorEnable_CheckedChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DFillColorEnabled = chk3DFillColorEnable.Checked;
        }

        private void clrbtn3DFillColor_Changed(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DFillColor = clrbtn3DFillColor.Color;
        }

        private void tb3DFillOpacity_Scroll(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DFillAlpha = tb3DFillOpacity.Value / 100f;
        }

        private void combo3DColorMap_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_initializing) return;
            Display.Pan3DColorMap = Math.Max(0, combo3DColorMap.SelectedIndex);
        }

        private void btn3DResetDefaults_Click(object sender, EventArgs e)
        {
            _initializing = true;

            chk3DWaterfallSync.Checked = true;
            chk3DSideWalls.Checked = true;
            ud3DXOffset.Value = 0.60m;
            ud3DYOffset.Value = 0.58m;
            ud3DRidgeHeight.Value = 0.46m;
            ud3DHaze.Value = 0.16m;
            ud3DLineCount.Value = 35;
            ud3DSpeed.Value = 25;
            ud3DZCurve.Value = 0.90m;
            clrbtn3DLineColor.Color = Color.Aquamarine;
            chk3DFillColorEnable.Checked = false;
            clrbtn3DFillColor.Color = Color.Aquamarine;
            tb3DFillOpacity.Value = 55;
            combo3DColorMap.SelectedIndex = 0;

            _initializing = false;

            PushAllSettings();
        }
    }
}
