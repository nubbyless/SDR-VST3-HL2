// Tier 3 extension - GPU mesh 2D panadapter fill
//
// Replaces the per-column crest-to-baseline D2D fill lines in DrawPanadapterDX2D
// (the "pana fill" block) with a GPU-rendered curtain sheet: one quad per
// decimated column from the crest down to the baseline, rendered into an
// offscreen W x H texture and composited by ONE D2D DrawBitmap at exactly the
// stacking position the per-column lines occupied (after the grid, before the
// spectral-peak / data-line strokes). The data line, glow, peak-hold and every
// overlay stay D2D - this module only owns the FILL.
//
// Visual contract: matches the D2D fill for the plain 2D panadapter:
//   - crest mapping replicates Y = shift + H*(1-s) - 0.5 with
//     s = clamp((dBm + offset - grid_min) / yRange), same normalisation as the
//     column loop (the -0.5 floor mimic included)
//   - colour is a vertical gradient sampled from a 256-entry LUT rebuilt each
//     call: solid mode = uniform data_fill_color(_tx); linear-gradient mode =
//     the same ucLGPicker stop list buildLinearGradientBrush(RX/TX) feeds the
//     D2D brushes (bottom -> top axis, alpha = data_fill_color.A). LUT entries
//     are premultiplied so the D2D blit composites identically to the strokes.
//   - the sheet is scissored to the pane rect, replicating the
//     PushAxisAlignedClip the D2D path draws under
// Known minor divergence: D2D antialiases stroke edges, GPU quads rasterise
// hard-edged - visible only as sub-pixel crispness on shallow crest slopes,
// same character as the accepted 3D mesh surface.
//
// Scope guards: engages only for plain 2D panadapters (!draw3DHistory - when
// the 3D history overlay is active its colormap/waterfall-sync/custom fills
// and the mesh surface own the pane, untouched here), pan_fill on, master GPU
// mesh toggle + Hardware render path. Any failure returns false and the
// legacy per-column loop runs unchanged (GPU fallback rule 1).
//
// Threading/interop: called on the display thread INSIDE an open D2D
// BeginDraw, after modifyDataForNotches. The target is flushed before D3D
// work and the immediate context after it, so the single command queue orders
// grid strokes -> sheet render -> sheet blit deterministically.
using System;
// Third-party: GPU interop via Vortice.Windows (MIT License, Copyright (c) Amer Koleci and Contributors).
// Full license text ships with the app (Licenses folder) and lives in the repo under Project Files\lib\licenses\.
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using MapMode = Vortice.Direct3D11.MapMode;

namespace Thetis
{
    partial class Display
    {
        #region GPU 2D panafill fields

        private const int SpecSlotCount = 2;   // index 0 = rx1, index 1 = rx2

        private static bool _specShadersBuilt;
        private static ID3D11VertexShader _specVS;
        private static ID3D11PixelShader _specPS;
        private static ID3D11Buffer _specCB;        // per-pane constants
        private static ID3D11RasterizerState _specRS; // CullNone + scissor enabled
        private static ID3D11Texture2D _specLutTex;   // 256 x 1, BGRA dynamic
        private static ID3D11ShaderResourceView _specLutSRV;
        private static ID3D11SamplerState _specSampler;

        private struct SpecSheetState
        {
            public ID3D11Texture2D SheetTex;        // W x H, BGRA8, RT binding
            public ID3D11RenderTargetView SheetRTV;
            public ID2D1Bitmap SheetBitmap;         // shared wrapping of SheetTex
            public ID3D11Texture2D HeightTex;       // cols x 1, R32F dynamic
            public ID3D11ShaderResourceView HeightSRV;
            public int W, H, Cols;
        }
        private static readonly SpecSheetState[] _spec = new SpecSheetState[SpecSlotCount];

        private static float[] _specHeightScratch;

        // gradient LUT source cache (fetched only when a key below changes)
        private static int _lgBrushVersion;         // bumped by brush rebuild hooks
        private struct SpecLutKey
        {
            public bool Valid, Tx, LG;
            public int Version, GridMin, GridMax, Alpha;
        }
        private static readonly SpecLutKey[] _specLutKey = new SpecLutKey[SpecSlotCount];
        private static readonly float[][] _specLutStopsPct = new float[SpecSlotCount][];
        private static readonly byte[][] _specLutStopsR = new byte[SpecSlotCount][];
        private static readonly byte[][] _specLutStopsG = new byte[SpecSlotCount][];
        private static readonly byte[][] _specLutStopsB = new byte[SpecSlotCount][];

        private static byte[] _specLutPixels;       // 256*4 premultiplied BGRA scratch
        private static bool _specLoggedActive;
        private static readonly bool[] _specGuardLogged = new bool[SpecSlotCount];   // TEMP diag
        private static readonly bool[] _specSlotLogged = new bool[SpecSlotCount];    // TEMP diag

        #endregion

        #region GPU 2D panafill HLSL

        private const string SPEC_HLSL = @"
            cbuffer SpecCB : register(b0)
            {
                float4 CB_A;   // paneY(shift), paneH(H), sheetW, sheetH
                float4 CB_B;   // cols, dec, unused0, unused1
            };

            Texture2D SpecHeights : register(t0);
            Texture2D SpecLUT : register(t1);
            SamplerState SpecPointSamp : register(s0);

            struct SO
            {
                float4 pos : SV_POSITION;
                float py : TEXCOORD0;   // pixel offset within the pane (top = 0)
            };

            // Quad soup straight off SV_VertexID: 6 verts per decimated column.
            // Corner table: tri A = (x0,top)(x1,top)(x0,base), tri B = (x1,top)(x1,base)(x0,base)
            // Positions are SHEET-LOCAL: the sheet is only H tall, the pane sits at
            // 'shift' in target space, so subtract it before normalising.
            SO vs_spec(uint vid : SV_VertexID)
            {
                SO o;
                uint col = vid / 6u;
                static const uint corner[6] = { 0u, 1u, 2u, 1u, 3u, 2u };
                uint k = corner[vid % 6u];

                float s = SpecHeights.Load(int3(col, 0, 0)).r;      // 0..1 strength
                float x0 = (float)col * CB_B.y;
                float yTop = CB_A.x + CB_A.y * (1.0 - s) - 0.5;     // floor mimic of the D2D loop
                float yBase = CB_A.x + CB_A.y;

                float px = (k & 1u) ? (x0 + CB_B.y) : x0;
                float pyPane = ((k & 2u) ? yBase : yTop) - CB_A.x;  // sheet-local y

                o.pos = float4(px / CB_A.z * 2.0 - 1.0, 1.0 - pyPane / CB_A.w * 2.0, 0.0, 1.0);
                o.py = pyPane;
                return o;
            }

            // LUT index 0 = bottom stop ... 255 = top stop (brush axis bottom->top).
            // Entries are premultiplied BGRA built CPU-side from the same stops the
            // D2D brushes use, so returning them verbatim composites identically.
            float4 ps_spec(SO i) : SV_Target
            {
                float tY = saturate(i.py / CB_A.y);
                float u = (floor((1.0 - tY) * 255.0) + 0.5) / 256.0;
                return SpecLUT.SampleLevel(SpecPointSamp, float2(u, 0.5), 0);
            }
            ";

        #endregion

        #region GPU 2D panafill lifecycle

        /// <summary>Call whenever the LG data-fill brushes are rebuilt so cached
        /// gradient stops are refetched.</summary>
        private static void SpectrumFillBrushesChanged()
        {
            _lgBrushVersion++;
        }

        private static bool SpecMeshArmed
        {
            get { return GpuMeshEnabled && m_eRenderPath == DXRenderPath.Hardware && _device != null && _bDX2Setup; }
        }

        /// <summary>Releases the D2D-side wrappers only - used on render-target
        /// recreation (resize) where the textures themselves survive.</summary>
        private static void ReleaseSpectrumFillFrameState()
        {
            for (int i = 0; i < SpecSlotCount; i++)
            {
                _spec[i].SheetBitmap?.Dispose();
                _spec[i].SheetBitmap = null;
            }
        }

        private static void ReleaseSpectrumFillObjects()
        {
            ReleaseSpectrumFillFrameState();
            for (int i = 0; i < SpecSlotCount; i++)
            {
                ref SpecSheetState s = ref _spec[i];
                s.SheetRTV?.Dispose(); s.SheetRTV = null;
                s.SheetTex?.Dispose(); s.SheetTex = null;
                s.HeightSRV?.Dispose(); s.HeightSRV = null;
                s.HeightTex?.Dispose(); s.HeightTex = null;
                s.W = 0; s.H = 0; s.Cols = 0;
            }
            _specVS?.Dispose(); _specVS = null;
            _specPS?.Dispose(); _specPS = null;
            _specCB?.Dispose(); _specCB = null;
            _specRS?.Dispose(); _specRS = null;
            _specLutTex?.Dispose(); _specLutTex = null;
            _specLutSRV?.Dispose(); _specLutSRV = null;
            _specSampler?.Dispose(); _specSampler = null;
            _specShadersBuilt = false;
        }

        private static bool BuildSpectrumPipeline(ID3D11Device device)
        {
            if (_specShadersBuilt) return true;
            try
            {
                byte[] vsBytes = Vortice.D3DCompiler.Compiler.Compile(SPEC_HLSL, "vs_spec", "pan2dmesh.hlsl", "vs_5_0",
                    Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None).ToArray();
                byte[] psBytes = Vortice.D3DCompiler.Compiler.Compile(SPEC_HLSL, "ps_spec", "pan2dmesh.hlsl", "ps_5_0",
                    Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None).ToArray();
                _specVS = device.CreateVertexShader(vsBytes, null);
                _specPS = device.CreatePixelShader(psBytes);

                _specCB = device.CreateBuffer(new BufferDescription(32, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
                _specRS = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, Vortice.Direct3D11.FillMode.Solid)
                {
                    ScissorEnable = true,
                });

                _specLutTex = device.CreateTexture2D(new Texture2DDescription()
                {
                    Width = 256,
                    Height = 1,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Dynamic,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.Write,
                });
                _specLutSRV = device.CreateShaderResourceView(_specLutTex);
                _specSampler = device.CreateSamplerState(new SamplerDescription(
                    Vortice.Direct3D11.Filter.MinMagMipPoint, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));

                _specLutPixels = new byte[256 * 4];
                _specHeightScratch = new float[4096];

                _specShadersBuilt = true;
                return true;
            }
            catch (Exception e)
            {
                Common.MeshDiagLog("GPU 2D panafill: pipeline build failed - " + e.Message);
                ReleaseSpectrumFillObjects();
                return false;
            }
        }

        private static bool EnsureSpecSheet(ID3D11Device device, int slot, int w, int h, int cols)
        {
            ref SpecSheetState s = ref _spec[slot];
            if (s.SheetTex != null && s.W == w && s.H == h && s.Cols == cols)
            {
                // textures survive render-target recreation, but the D2D wrapper
                // dies with the old target (ReleaseSpectrumFillFrameState) -
                // rebuild just that wrapper or the blit silently no-ops forever
                if (s.SheetBitmap != null) return true;
                try
                {
                    using IDXGISurface surf = s.SheetTex.QueryInterface<IDXGISurface>();
                    s.SheetBitmap = _d2dRenderTarget.CreateSharedBitmap(surf, new BitmapProperties(
                        new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
                    return true;
                }
                catch (Exception e)
                {
                    Common.MeshDiagLog("GPU 2D panafill: shared bitmap rebuild failed - " + e.Message);
                    s.SheetBitmap?.Dispose(); s.SheetBitmap = null;
                    return false;
                }
            }

            try
            {
                s.SheetBitmap?.Dispose(); s.SheetBitmap = null;
                s.SheetRTV?.Dispose(); s.SheetRTV = null;
                s.HeightSRV?.Dispose(); s.HeightSRV = null;
                s.HeightTex?.Dispose(); s.HeightTex = null;
                s.SheetTex?.Dispose(); s.SheetTex = null;

                s.SheetTex = device.CreateTexture2D(new Texture2DDescription()
                {
                    Width = (uint)w,
                    Height = (uint)h,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    // ShaderResource is REQUIRED - without it D2D wraps the surface
                    // as CANNOT_DRAW and every DrawBitmap of it fails at flush
                    // (EndDraw => D2DERR_BITMAP_CANNOT_DRAW, whole batch discarded)
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                });
                s.SheetRTV = device.CreateRenderTargetView(s.SheetTex);

                using IDXGISurface surf = s.SheetTex.QueryInterface<IDXGISurface>();
                s.SheetBitmap = _d2dRenderTarget.CreateSharedBitmap(surf, new BitmapProperties(
                    new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));

                s.HeightTex = device.CreateTexture2D(new Texture2DDescription()
                {
                    Width = (uint)cols,
                    Height = 1,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.R32_Float,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Dynamic,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.Write,
                });
                s.HeightSRV = device.CreateShaderResourceView(s.HeightTex);

                s.W = w; s.H = h; s.Cols = cols;
                return true;
            }
            catch (Exception e)
            {
                Common.MeshDiagLog("GPU 2D panafill: sheet build failed (" + w + "x" + h + ") - " + e.Message);
                s.SheetBitmap?.Dispose(); s.SheetBitmap = null;
                s.SheetRTV?.Dispose(); s.SheetRTV = null;
                s.HeightSRV?.Dispose(); s.HeightSRV = null;
                s.HeightTex?.Dispose(); s.HeightTex = null;
                s.SheetTex?.Dispose(); s.SheetTex = null;
                s.W = 0; s.H = 0; s.Cols = 0;
                return false;
            }
        }

        #endregion

        #region GPU 2D panafill LUT

        /// <summary>Refreshes the cached gradient stop list when anything that
        /// feeds it changed. Mirrors buildLinearGradientBrush(RX/TX): both rx use
        /// RX1GradPicker, TX uses TXGradPicker, alpha comes from data_fill_color
        /// (tx variant uses data_fill_color_tx).</summary>
        private static bool EnsureSpecLutStops(int slot, bool tx, bool lg, int gridMin, int gridMax)
        {
            int alpha = tx ? data_fill_color_tx.A : data_fill_color.A;
            ref SpecLutKey key = ref _specLutKey[slot];

            if (key.Valid && key.Tx == tx && key.LG == lg && key.Version == _lgBrushVersion &&
                key.GridMin == gridMin && key.GridMax == gridMax && key.Alpha == alpha)
                return true;

            if (!lg)
            {
                key = new SpecLutKey { Valid = true, Tx = tx, LG = false, Version = _lgBrushVersion, GridMin = gridMin, GridMax = gridMax, Alpha = alpha };
                return true;
            }

            try
            {
                var picker = tx ? console.SetupForm.TXGradPicker : console.SetupForm.RX1GradPicker;
                var lst = picker.GetColourGradientDataForDBMRange(gridMin, gridMax);
                int n = lst.Count;
                if (n == 0) return false;

                if (_specLutStopsPct[slot] == null || _specLutStopsPct[slot].Length < n)
                {
                    _specLutStopsPct[slot] = new float[n];
                    _specLutStopsR[slot] = new byte[n];
                    _specLutStopsG[slot] = new byte[n];
                    _specLutStopsB[slot] = new byte[n];
                }
                for (int i = 0; i < n; i++)
                {
                    _specLutStopsPct[slot][i] = lst[i].percent;
                    _specLutStopsR[slot][i] = lst[i].color.R;
                    _specLutStopsG[slot][i] = lst[i].color.G;
                    _specLutStopsB[slot][i] = lst[i].color.B;
                }

                key = new SpecLutKey { Valid = true, Tx = tx, LG = true, Version = _lgBrushVersion, GridMin = gridMin, GridMax = gridMax, Alpha = alpha };
                return true;
            }
            catch
            {
                key.Valid = false;
                return false;
            }
        }

        /// <summary>Fills the 256-entry premultiplied BGRA LUT: solid mode paints
        /// a uniform colour, LG mode lerps the cached stops exactly like the D2D
        /// gradient (clamped outside the end stops).</summary>
        private static void BuildSpecLutPixels(int slot, bool tx, bool lg)
        {
            byte aByte = (byte)(tx ? data_fill_color_tx.A : data_fill_color.A);
            byte rS, gS, bS;
            if (tx) { rS = data_fill_color_tx.R; gS = data_fill_color_tx.G; bS = data_fill_color_tx.B; }
            else { rS = data_fill_color.R; gS = data_fill_color.G; bS = data_fill_color.B; }

            float[] pct = _specLutStopsPct[slot];
            byte[] sr = _specLutStopsR[slot];
            byte[] sg = _specLutStopsG[slot];
            byte[] sb = _specLutStopsB[slot];
            int n = pct == null ? 0 : pct.Length;

            byte[] px = _specLutPixels;
            for (int i = 0; i < 256; i++)
            {
                byte r, g, b, a;
                if (!lg || n == 0)
                {
                    r = rS; g = gS; b = bS; a = aByte;
                }
                else
                {
                    float pos = i / 255f;
                    // find segment (stops assumed ascending by percent, as the picker provides)
                    int seg = 0;
                    while (seg < n - 1 && pct[seg + 1] < pos) seg++;
                    float p0 = pct[seg], p1 = seg + 1 < n ? pct[seg + 1] : pct[seg];
                    float t = p1 > p0 ? (pos - p0) / (p1 - p0) : 0f;
                    if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
                    r = (byte)(sr[seg] + (sr[Math.Min(seg + 1, n - 1)] - sr[seg]) * t);
                    g = (byte)(sg[seg] + (sg[Math.Min(seg + 1, n - 1)] - sg[seg]) * t);
                    b = (byte)(sb[seg] + (sb[Math.Min(seg + 1, n - 1)] - sb[seg]) * t);
                    a = aByte;
                }

                float fa = a / 255f;
                px[i * 4 + 0] = (byte)(b * fa + 0.5f);   // B premultiplied
                px[i * 4 + 1] = (byte)(g * fa + 0.5f);   // G
                px[i * 4 + 2] = (byte)(r * fa + 0.5f);   // R
                px[i * 4 + 3] = a;
            }
        }

        #endregion

        #region GPU 2D panafill render + composite

        private struct SpecConstants
        {
            public float PaneY, PaneH, SheetW, SheetH;   // CB_A
            public float Cols, Dec, Unused0, Unused1;    // CB_B
        }

        /// <summary>
        /// Renders the crest-to-baseline fill for this pane into the offscreen GPU
        /// sheet. Called from DrawPanadapterDX2D after the visual-notch data
        /// modification and before the column loop; returns true when the caller
        /// must composite the sheet (BlitSpectrumFillMesh) and skip its per-column
        /// fill strokes.
        /// </summary>
        private static bool TryRenderSpectrumFillMesh(int rx, int nVerticalShift, int W, int H,
            int nDecimatedWidth, float[] data, float fOffset, int grid_min, int grid_max, bool local_mox)
        {
            if (!SpecMeshArmed || _paused_display) return false;
            int slot = rx == 2 ? 1 : 0;
            if (data == null || data.Length < nDecimatedWidth || nDecimatedWidth < 2)
            {
                if (!_specGuardLogged[slot])
                {
                    _specGuardLogged[slot] = true;
                    Common.MeshDiagLog("GPU 2D panafill diag: rx" + rx + " guard DATA len=" +
                        (data == null ? -1 : data.Length) + " cols=" + nDecimatedWidth);
                }
                return false;
            }
            int yRange = grid_max - grid_min;
            if (yRange <= 0 || H <= 0 || W <= 0)
            {
                if (!_specGuardLogged[slot])
                {
                    _specGuardLogged[slot] = true;
                    Common.MeshDiagLog("GPU 2D panafill diag: rx" + rx + " guard GEOM yRange=" + yRange +
                        " H=" + H + " W=" + W);
                }
                return false;
            }

            try
            {
                if (!BuildSpectrumPipeline(_device)) return false;
                if (!EnsureSpecSheet(_device, slot, W, H, nDecimatedWidth)) return false;
                if (!EnsureMeshRTV(_device)) return false;   // for the backbuffer restore below
                ref SpecSheetState s = ref _spec[slot];

                // ---- strengths (same normalisation as the D2D column loop) ----
                float[] scratch = _specHeightScratch;
                if (scratch.Length < nDecimatedWidth) _specHeightScratch = scratch = new float[Math.Max(nDecimatedWidth, scratch.Length * 2)];

                // Tier 3 GPU compute shaders: when armed, offload normalisation
                // to a GPU compute shader.  Falls back to the CPU loop on failure.
                bool bComputeNormalised = ComputeArmed && TryDispatchSpectrumCompute(
                    data, fOffset, grid_min, grid_max, nDecimatedWidth);

                if (!bComputeNormalised)
                {
                    float invRange = 1f / yRange;
                    for (int i = 0; i < nDecimatedWidth; i++)
                    {
                        float v = (data[i] + fOffset - grid_min) * invRange;
                        scratch[i] = v < 0f ? 0f : (v > 1f ? 1f : v);
                    }
                }

                ID3D11DeviceContext dc = _device.ImmediateContext;

                MappedSubresource hm = dc.Map(s.HeightTex, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                unsafe
                {
                    fixed (float* src = scratch)
                    {
                        uint bytes = (uint)nDecimatedWidth * sizeof(float);
                        Buffer.MemoryCopy(src, (void*)hm.DataPointer, bytes, bytes);
                    }
                }
                dc.Unmap((ID3D11Resource)s.HeightTex, 0);

                // ---- colour LUT (rebuilt every call; 256 texels are free) ----
                bool lg = local_mox ? m_bUseLinearGradientTX : m_bUseLinearGradient;
                if (!EnsureSpecLutStops(slot, local_mox, lg, grid_min, grid_max)) return false;
                BuildSpecLutPixels(slot, local_mox, lg);
                MappedSubresource lm = dc.Map((ID3D11Resource)_specLutTex, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                unsafe
                {
                    fixed (byte* src = _specLutPixels)
                    {
                        uint bytes = 256 * 4;
                        Buffer.MemoryCopy(src, (void*)lm.DataPointer, bytes, bytes);
                    }
                }
                dc.Unmap((ID3D11Resource)_specLutTex, 0);

                var cb = new SpecConstants
                {
                    PaneY = nVerticalShift,
                    PaneH = H,
                    SheetW = W,     // sheet-local normalisation - NOT target dims
                    SheetH = H,
                    Cols = nDecimatedWidth,
                    Dec = m_nDecimation,
                };
                MappedSubresource cbMap = dc.Map((ID3D11Resource)_specCB, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
                unsafe { System.Runtime.CompilerServices.Unsafe.Write((void*)cbMap.DataPointer, cb); }
                dc.Unmap((ID3D11Resource)_specCB, 0);

                // pending D2D batch first, then our D3D pass, then back to D2D -
                // keeps the interleaving deterministic on the shared queue
                ulong flushTag1, flushTag2;
                _d2dRenderTarget.Flush(out flushTag1, out flushTag2);

                dc.OMSetRenderTargets(new[] { s.SheetRTV }, null);
                // fresh sheet every frame - without this the fill overdraws last
                // frame's fill and the alpha stacks up to an opaque frozen block
                dc.ClearRenderTargetView(s.SheetRTV, new Color4(0f, 0f, 0f, 0f));
                dc.OMSetBlendState(null);
                dc.RSSetState(_specRS);
                // viewport + scissor are SHEET-LOCAL: the RT is the W x H sheet,
                // quads carry sheet-local coords (shift subtracted in the VS)
                dc.RSSetScissorRects(new[] { new Vortice.RawRect(0, 0, W, H) });
                dc.RSSetViewport(new Viewport(0f, 0f, (float)W, (float)H));
                dc.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
                dc.VSSetShader(_specVS);
                dc.VSSetConstantBuffer(0, _specCB);
                dc.VSSetShaderResource(0, s.HeightSRV);
                dc.PSSetShader(_specPS);
                dc.PSSetConstantBuffer(0, _specCB);
                dc.PSSetShaderResource(1, _specLutSRV);   // PS declares SpecLUT at t1
                dc.PSSetSamplers(0, new[] { _specSampler });
                dc.Draw((uint)nDecimatedWidth * 6u, 0u);
                dc.Flush();

                // CRITICAL: we changed OM inside an open D2D BeginDraw session and
                // D2D does not rebind its own target until the NEXT BeginDraw, so
                // every later D2D draw of THIS frame (trace, peaks, overlays, fps
                // text) would land inside the sheet instead of on screen. Restore
                // the backbuffer binding + full scissor before returning to D2D.
                dc.OMSetRenderTargets(new[] { _meshRTV }, null);
                dc.RSSetScissorRects(new[] { new Vortice.RawRect(0, 0,
                    (int)displayTargetWidth, (int)displayTargetHeight) });
                dc.Flush();

                if (!_specLoggedActive)
                {
                    _specLoggedActive = true;
                    Common.MeshDiagLog("GPU 2D panafill mesh active (" + nDecimatedWidth + " cols)");
                }
                if (!_specSlotLogged[slot])
                {
                    _specSlotLogged[slot] = true;
                    Common.MeshDiagLog("GPU 2D panafill diag: rx" + rx + " render OK shift=" + nVerticalShift +
                        " H=" + H + " W=" + W + " cols=" + nDecimatedWidth + " mox=" + local_mox);
                }
                SpecMeshWasUsedThisFrame = true;   // TEMP diag
                return true;
            }
            catch (Exception e)
            {
                Common.MeshDiagLog("GPU 2D panafill: render failed - falling back to D2D columns : " + e.Message);
                ReleaseSpectrumFillObjects();
                return false;
            }
        }

        /// <summary>Composites the GPU sheet at the exact position the per-column
        /// fill strokes occupied. Must be called inside the spectrum clip region.</summary>
        private static void BlitSpectrumFillMesh(int rx, int nVerticalShift, int W, int H)
        {
            ID2D1Bitmap bmp = _spec[rx == 2 ? 1 : 0].SheetBitmap;
            if (bmp == null) return;
            _d2dRenderTarget.DrawBitmap(bmp,
                new Rect(0f, nVerticalShift, W, H),
                1f, BitmapInterpolationMode.Linear, null);
        }

        #endregion
    }
}
