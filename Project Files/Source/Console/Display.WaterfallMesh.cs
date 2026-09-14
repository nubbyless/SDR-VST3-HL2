// Tier 3 extension - GPU mesh waterfall
//
// Moves the waterfall surface off the D2D bitmap-scroll path onto a Direct3D11
// ring texture rendered straight into the shared swapchain backbuffer BEFORE the
// D2D frame begins (same pattern as Display.Pan3DMesh.cs). The D2D bitmap path in
// DrawWaterfallDX2D stays untouched and remains the permanent fallback (GPU
// fallback rule 1).
//
// Visual contract: PIXEL-IDENTICAL to the D2D path. Row colours are still baked
// on the CPU by the existing scheme switch (Custom/enhanced/... unchanged); this
// module only replaces storage + scrolling + presentation:
//   - per rx a W x rows B8G8R8A8_UNorm default-usage ring texture holds the
//     history; one row is appended per waterfall line via a small staging texture
//     + CopySubresourceRegion (replaces CreateBitmap + two full-height
//     CopyFromBitmap scrolls + CopyFromMemory every line)
//   - horizontal tuning anchoring replicates prepareWaterfallBitmapShift exactly:
//     a cumulative pixel-shift counter advances by the same wholeShift the D2D
//     path applies; each stored row remembers the counter value at write time and
//     the shader re-derives the source column per row, so old content stays
//     anchored to RF frequency and exposed strips render empty (identical to the
//     clear strips of the scroll path). Smear mode falls out naturally: the shift
//     output is always 0 there so the counter never advances.
//   - presentation is one textured quad per visible pane; the pixel shader does
//     the ring lookup, per-row anchoring and opacity (premultiplied output so the
//     AlphaBlend state composites as SourceOver, matching DrawBitmap opacity).
//
// Dispatch/lifecycle:
//   - DrawWaterfallDX2D captures per-pane geometry every frame (one-frame latency,
//     same as CaptureMeshFrameParams) and hands freshly built rows to
//     WaterfallMeshCommitLine when the master GPU mesh toggle is on and the render
//     path is Hardware (Force CPU / WARP automatically disables all mesh paths).
//   - RenderGpuWaterfall() presents captured panes pre-BeginDraw, sharing the
//     backdrop ownership protocol with the 3D mesh pass via EnsureGpuBackdrop().
//   - Any failure tears the ring down and returns false; the caller runs the D2D
//     scroll code for that line and re-owns presentation seamlessly.
using System;
// Third-party: GPU interop via Vortice.Windows (MIT License, Copyright (c) Amer Koleci and Contributors).
// Full license text ships with the app (Licenses folder) and lives in the repo under Project Files\lib\licenses\.
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using MapMode = Vortice.Direct3D11.MapMode;

namespace Thetis
{
    partial class Display
    {
        #region GPU waterfall mesh fields

        private const int WfSlotCount = 2;   // index 0 = rx1, index 1 = rx2

        private static bool _wfShadersBuilt;
        private static ID3D11VertexShader _wfVS;
        private static ID3D11PixelShader _wfPS;
        private static ID3D11InputLayout _wfIL;
        private static ID3D11Buffer _wfQuadVB;      // static unit quad
        private static ID3D11Buffer _wfQuadIB;
        private static ID3D11Buffer _wfCB;          // per-pane constants
        private static ID3D11BlendState _wfBlend;   // premultiplied SourceOver
        private static ID3D11RasterizerState _wfRS;
        private static ID3D11SamplerState _wfSampPoint;

        private struct WfRingState
        {
            public ID3D11Texture2D RowsTex;         // Width x Rows, BGRA8, default usage
            public ID3D11ShaderResourceView RowsSRV;
            public ID3D11Texture2D RowStaging;      // Width x 1 staging upload
            public ID3D11Texture2D AnchorTex;       // 1 x Rows, R32Float
            public ID3D11ShaderResourceView AnchorSRV;
            public ID3D11Texture2D AnchorStaging;   // 1 x 1 staging upload
            public int Width;                       // built dimensions
            public int Rows;
            public int Head;                        // next row to write
            public int ValidRows;                   // rows written since last clear
            public double CumAnchor;                // cumulative tuning-shift counter (px)
            public bool MeshOwnsPane;               // true while GPU path owns presentation
        }
        private static readonly WfRingState[] _wf = new WfRingState[WfSlotCount];

        /// <summary>Per-frame pane geometry captured by DrawWaterfallDX2D, consumed
        /// by the pre-BeginDraw pass of the NEXT frame.</summary>
        private struct WfPaneParams
        {
            public bool Valid;
            public float Shift, W, H;
        }
        private static readonly WfPaneParams[] _wfPane = new WfPaneParams[WfSlotCount];

        #endregion

        #region GPU waterfall mesh control

        /// <summary>Master gate: experimental GPU mesh toggle, hardware path only.
        /// Force-CPU / WARP sessions never arm any mesh path.</summary>
        private static bool WfMeshArmed
        {
            get { return GpuMeshEnabled && m_eRenderPath == DXRenderPath.Hardware && _device != null && _bDX2Setup; }
        }

        /// <summary>True while the GPU ring owns presentation of this rx's pane (the
        /// D2D DrawBitmap present is skipped). Cleared automatically on any failure
        /// so the D2D path takes back over.</summary>
        private static bool WfMeshOwnsPane(int rx)
        {
            return _wf[rx == 2 ? 1 : 0].MeshOwnsPane;
        }

        private static void CaptureWaterfallPaneParams(int nVerticalShift, int W, int H, int rx)
        {
            _wfPane[rx == 2 ? 1 : 0] = new WfPaneParams
            {
                Valid = true,
                Shift = nVerticalShift,
                W = W,
                H = H,
            };
        }

        private static void ClearWaterfallPaneCaptures()
        {
            _wfPane[0].Valid = false;
            _wfPane[1].Valid = false;
        }

        #endregion

        #region GPU waterfall mesh HLSL

        private const string WF_HLSL = @"
            cbuffer WfCB : register(b0)
            {
                float4 CB_Geom;    // paneX, paneY, paneW, paneH (px)
                float4 CB_Target;  // targetW, targetH, texW, texH
                float4 CB_State;   // head, validRows, anchorNow, opacity
            };

            Texture2D WfRowsTex : register(t0);
            Texture2D WfAnchorTex : register(t1);
            SamplerState WfPointSamp : register(s0);

            struct WFOUT
            {
                float4 pos : SV_POSITION;
                float2 px : TEXCOORD0;   // pixel coords within the pane
            };

            WFOUT vs_wf(float2 uv : POSITION)
            {
                WFOUT o;
                float2 p = CB_Geom.xy + uv * CB_Geom.zw;
                o.pos = float4(p.x / CB_Target.x * 2.0 - 1.0, 1.0 - p.y / CB_Target.y * 2.0, 0.0, 1.0);
                o.px = uv * CB_Geom.zw;
                return o;
            }

            // Ring lookup: screen row -> age -> ring row; screen column -> write-time
            // column via the row's stored tuning anchor. Rows outside the valid window
            // or columns outside [0,texW) return transparent black, invisible under
            // premultiplied SourceOver - matches the cleared strips of the D2D scroll.
            // Rows are baked BGRA pixels with alpha 255, so col.a is 1 for real data.
            float4 ps_wf(WFOUT i) : SV_Target
            {
                float x = floor(i.px.x);
                float age = floor(i.px.y);          // 0 = newest line (top of pane)

                if (age >= CB_State.y) return float4(0, 0, 0, 0);

                float rr = CB_State.x - 1.0 - age;  // ring row of this age
                rr = (rr < 0.0) ? rr + CB_Target.w : rr;
                float v = (rr + 0.5) / CB_Target.w;

                float anch = WfAnchorTex.SampleLevel(WfPointSamp, float2(0.5, v), 0).r;
                float cw = x - (CB_State.z - anch); // write-time source column
                if (cw < 0.0 || cw >= CB_Target.z) return float4(0, 0, 0, 0);

                float u = (floor(cw) + 0.5) / CB_Target.z;
                float4 col = WfRowsTex.SampleLevel(WfPointSamp, float2(u, v), 0);

                float a = col.a * saturate(CB_State.w);
                return float4(col.rgb * a, a);      // premultiplied SourceOver
            }
            ";

        #endregion

        #region GPU waterfall mesh implementation

        private static void ReleaseWaterfallMeshObjects()
        {
            for (int i = 0; i < WfSlotCount; i++) ReleaseWaterfallRing(ref _wf[i]);
            _wfIL?.Dispose(); _wfIL = null;
            _wfVS?.Dispose(); _wfVS = null;
            _wfPS?.Dispose(); _wfPS = null;
            _wfQuadVB?.Dispose(); _wfQuadVB = null;
            _wfQuadIB?.Dispose(); _wfQuadIB = null;
            _wfCB?.Dispose(); _wfCB = null;
            _wfBlend?.Dispose(); _wfBlend = null;
            _wfRS?.Dispose(); _wfRS = null;
            _wfSampPoint?.Dispose(); _wfSampPoint = null;
            _wfShadersBuilt = false;
        }

        private static void ReleaseWaterfallRing(ref WfRingState r)
        {
            r.RowsSRV?.Dispose();
            r.RowsTex?.Dispose();
            r.RowStaging?.Dispose();
            r.AnchorSRV?.Dispose();
            r.AnchorTex?.Dispose();
            r.AnchorStaging?.Dispose();
            r.RowsTex = null; r.RowsSRV = null; r.RowStaging = null;
            r.AnchorTex = null; r.AnchorSRV = null; r.AnchorStaging = null;
            r.Width = 0; r.Rows = 0;
            r.Head = 0; r.ValidRows = 0;
            r.CumAnchor = 0;
            r.MeshOwnsPane = false;
        }

        private static bool BuildWaterfallPipeline(ID3D11Device device)
        {
            if (_wfShadersBuilt) return true;
            try
            {
                byte[] vsBytes = Vortice.D3DCompiler.Compiler.Compile(WF_HLSL, "vs_wf", "waterfallmesh.hlsl", "vs_5_0",
                    Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None).ToArray();
                byte[] psBytes = Vortice.D3DCompiler.Compiler.Compile(WF_HLSL, "ps_wf", "waterfallmesh.hlsl", "ps_5_0",
                    Vortice.D3DCompiler.ShaderFlags.None, Vortice.D3DCompiler.EffectFlags.None).ToArray();
                _wfVS = device.CreateVertexShader(vsBytes, null);
                _wfPS = device.CreatePixelShader(psBytes);
                _wfIL = device.CreateInputLayout(new Vortice.Direct3D11.InputElementDescription[]
                {
                    new Vortice.Direct3D11.InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
                }, vsBytes);

                float[] quad = new float[] { 0f, 0f, 1f, 0f, 0f, 1f, 1f, 1f };
                uint[] idx = new uint[] { 0, 1, 2, 2, 1, 3 };
                _wfQuadVB = device.CreateBuffer(quad, new BufferDescription(
                    (uint)(quad.Length * sizeof(float)), BindFlags.VertexBuffer, ResourceUsage.Immutable));
                _wfQuadIB = device.CreateBuffer(idx, new BufferDescription(
                    (uint)(idx.Length * sizeof(uint)), BindFlags.IndexBuffer, ResourceUsage.Immutable));

                _wfCB = device.CreateBuffer(new BufferDescription(48, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
                _wfBlend = device.CreateBlendState(Vortice.Direct3D11.BlendDescription.AlphaBlend);
                _wfRS = device.CreateRasterizerState(new Vortice.Direct3D11.RasterizerDescription(CullMode.None, Vortice.Direct3D11.FillMode.Solid));
                _wfSampPoint = device.CreateSamplerState(new SamplerDescription(
                    Vortice.Direct3D11.Filter.MinMagMipPoint, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));

                _wfShadersBuilt = true;
                return true;
            }
            catch (Exception e)
            {
                Common.MeshDiagLog("GPU waterfall mesh: pipeline build failed - " + e.Message);
                ReleaseWaterfallMeshObjects();
                return false;
            }
        }

        /// <summary>Builds (or rebuilds on dimension change) the ring textures for a
        /// slot. Rebuild clears history - same as a width-change clear on the D2D
        /// path; height-only changes are handled by rebuilding too (the D2D path
        /// stretches stale content instead, mesh starts fresh).</summary>
        private static bool EnsureWaterfallRing(ID3D11Device device, int slot, int width, int rows)
        {
            ref WfRingState r = ref _wf[slot];
            if (r.RowsTex != null && r.Width == width && r.Rows == rows) return true;

            try
            {
                ReleaseWaterfallRing(ref r);

                r.RowsTex = device.CreateTexture2D(new Texture2DDescription()
                {
                    Width = (uint)width,
                    Height = (uint)rows,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                });
                r.RowsSRV = device.CreateShaderResourceView(r.RowsTex);

                r.RowStaging = device.CreateTexture2D(new Texture2DDescription()
                {
                    Width = (uint)width,
                    Height = 1,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    CPUAccessFlags = CpuAccessFlags.Write,
                });

                r.AnchorTex = device.CreateTexture2D(new Texture2DDescription()
                {
                    Width = 1,
                    Height = (uint)rows,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.R32_Float,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                });
                r.AnchorSRV = device.CreateShaderResourceView(r.AnchorTex);

                r.AnchorStaging = device.CreateTexture2D(new Texture2DDescription()
                {
                    Width = 1,
                    Height = 1,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.R32_Float,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    CPUAccessFlags = CpuAccessFlags.Write,
                });

                r.Width = width;
                r.Rows = rows;
                r.Head = 0;
                r.ValidRows = 0;
                r.CumAnchor = 0;
                return true;
            }
            catch (Exception e)
            {
                Common.MeshDiagLog("GPU waterfall mesh: ring build failed (" + width + "x" + rows + ") - " + e.Message);
                ReleaseWaterfallRing(ref r);
                return false;
            }
        }

        private struct WfConstants
        {
            public float PaneX, PaneY, PaneW, PaneH;    // 0-15
            public float TargetW, TargetH, TexW, TexH;  // 16-31
            public float Head, ValidRows, AnchorNow, Opacity; // 32-47
        }

        /// <summary>
        /// Hands one freshly coloured waterfall line to the GPU ring. Called from
        /// DrawWaterfallDX2D INSTEAD of the D2D bitmap scroll when armed; returns
        /// false (caller then runs the D2D code) whenever anything is off. The GPU
        /// path takes over presentation of the pane with the FIRST committed row;
        /// hold frames (addRow=false) are handled internally once it owns the pane.
        /// </summary>
        /// <param name="row">BGRA bytes, W*4 long, exactly as built for CopyFromMemory</param>
        /// <param name="paneRows">ring height for this pane = H - 20</param>
        /// <param name="addRow">false = tuning-settle hold: advance anchors only</param>
        /// <param name="shiftPixels">wholeShift from prepareWaterfallBitmapShift</param>
        /// <param name="clearExisting">width-change/full-clear request from same</param>
        private static bool WaterfallMeshCommitLine(int rx, byte[] row, int paneRows, bool addRow, int shiftPixels, bool clearExisting)
        {
            if (!WfMeshArmed) { SetOwns(rx, false); return false; }

            int slot = rx == 2 ? 1 : 0;
            try
            {
                ID3D11DeviceContext dc = _device.ImmediateContext;
                if (!BuildWaterfallPipeline(_device)) { SetOwns(rx, false); return false; }

                ref WfRingState r = ref _wf[slot];
                int width = row.Length / 4;

                // anchor bookkeeping first - mirrors the order the D2D path applies:
                // clear wipes history, shifts re-anchor existing content, then the new
                // row is written under the post-shift counter value
                if (clearExisting)
                {
                    r.ValidRows = 0;
                    r.CumAnchor = 0;
                }
                else
                {
                    r.CumAnchor += shiftPixels;
                }

                if (!EnsureWaterfallRing(_device, slot, width, Math.Max(2, paneRows)))
                {
                    SetOwns(rx, false);
                    return false;
                }

                if (addRow)
                {
                    UploadRow(dc, ref r, row);
                    UploadAnchor(dc, ref r, (float)r.CumAnchor, r.Head);
                    r.Head = (r.Head + 1) % r.Rows;
                    if (r.ValidRows < r.Rows) r.ValidRows++;
                    r.MeshOwnsPane = true;   // GPU owns presentation from here on
                }

                return r.MeshOwnsPane;
            }
            catch (Exception e)
            {
                Common.MeshDiagLog("GPU waterfall mesh: commit failed - falling back to D2D : " + e.Message);
                ReleaseWaterfallRing(ref _wf[slot]);
                SetOwns(rx, false);
                return false;
            }
        }

        private static void SetOwns(int rx, bool owns)
        {
            _wf[rx == 2 ? 1 : 0].MeshOwnsPane = owns;
        }

        private static void UploadRow(ID3D11DeviceContext dc, ref WfRingState r, byte[] row)
        {
            MappedSubresource m = dc.Map(r.RowStaging, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
            unsafe
            {
                fixed (byte* src = row)
                {
                    uint bytes = (uint)row.Length;
                    Buffer.MemoryCopy(src, (void*)m.DataPointer, bytes, bytes);
                }
            }
            dc.Unmap((ID3D11Resource)r.RowStaging, 0);
            dc.CopySubresourceRegion((ID3D11Resource)r.RowsTex, 0, 0u, (uint)r.Head, 0u, (ID3D11Resource)r.RowStaging, 0, null);
        }

        private static void UploadAnchor(ID3D11DeviceContext dc, ref WfRingState r, float anchor, int ringRow)
        {
            MappedSubresource m = dc.Map(r.AnchorStaging, 0, MapMode.Write, Vortice.Direct3D11.MapFlags.None);
            unsafe { System.Runtime.CompilerServices.Unsafe.Write<float>((void*)m.DataPointer, anchor); }
            dc.Unmap((ID3D11Resource)r.AnchorStaging, 0);
            dc.CopySubresourceRegion((ID3D11Resource)r.AnchorTex, 0, 0u, (uint)ringRow, 0u, (ID3D11Resource)r.AnchorStaging, 0, null);
        }

        /// <summary>
        /// Pre-BeginDraw pass: renders every captured waterfall pane into the
        /// swapchain backbuffer. Returns true when at least one pane was drawn (the
        /// frame must then skip the D2D global clear/background block). Runs under
        /// _objDX2Lock alongside RenderGpuMesh3D().
        /// </summary>
        private static bool RenderGpuWaterfall()
        {
            if (!WfMeshArmed) return false;
            if (_paused_display) return false;

            bool drew = false;
            try
            {
                for (int slot = 0; slot < WfSlotCount; slot++)
                {
                    if (!_wfPane[slot].Valid) continue;
                    if (!RenderWaterfallPane(slot)) continue;
                    drew = true;
                }

                if (drew)
                {
                    _device.ImmediateContext.Flush();
                    if (!_wfLoggedActive)
                    {
                        _wfLoggedActive = true;
                        Common.MeshDiagLog("GPU waterfall mesh active");
                    }
                }
                return drew;
            }
            catch (Exception e)
            {
                Common.MeshDiagLog("GPU waterfall mesh: render failed - falling back to D2D bitmap : " + e.Message);
                ReleaseWaterfallMeshObjects();
                ClearWaterfallPaneCaptures();
                return false;
            }
        }

        private static bool _wfLoggedActive;

        private static bool RenderWaterfallPane(int slot)
        {
            WfPaneParams p = _wfPane[slot];
            ref WfRingState r = ref _wf[slot];
            if (r.RowsTex == null || r.RowsSRV == null || r.AnchorSRV == null || r.ValidRows == 0) return false;
            if (!EnsureMeshRTV(_device)) return false;

            ID3D11DeviceContext dc = _device.ImmediateContext;

            // backdrop ownership: skin image prepass or full background clear,
            // exactly once per frame across all mesh passes
            EnsureGpuBackdrop(dc);

            float paneH = p.H - 20;   // top 20px of the pane stay with D2D, like DrawBitmap
            var cb = new WfConstants
            {
                PaneX = 0f,
                PaneY = p.Shift + 20f,
                PaneW = p.W,
                PaneH = paneH,
                TargetW = displayTargetWidth,
                TargetH = displayTargetHeight,
                TexW = r.Width,
                TexH = r.Rows,
                Head = r.Head,
                ValidRows = r.ValidRows,
                AnchorNow = (float)r.CumAnchor,
                Opacity = slot == 0 ? m_fRX1WaterfallOpacity : m_fRX2WaterfallOpacity,
            };
            MappedSubresource cbMap = dc.Map((ID3D11Resource)_wfCB, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            unsafe { System.Runtime.CompilerServices.Unsafe.Write((void*)cbMap.DataPointer, cb); }
            dc.Unmap((ID3D11Resource)_wfCB, 0);

            dc.OMSetRenderTargets(new[] { _meshRTV }, null);
            dc.IASetInputLayout(_wfIL);
            dc.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
            dc.IASetVertexBuffer(0, _wfQuadVB, 8, 0);
            dc.IASetIndexBuffer(_wfQuadIB, Format.R32_UInt, 0);
            dc.VSSetShader(_wfVS);
            dc.VSSetConstantBuffer(0, _wfCB);
            dc.PSSetShader(_wfPS);
            dc.PSSetConstantBuffer(0, _wfCB);
            dc.PSSetShaderResource(0, r.RowsSRV);
            dc.PSSetShaderResource(1, r.AnchorSRV);
            dc.PSSetSamplers(0, new[] { _wfSampPoint });
            dc.OMSetBlendState(_wfBlend);
            dc.RSSetState(_wfRS);
            dc.RSSetViewport(new Viewport(0f, 0f, displayTargetWidth, displayTargetHeight));
            dc.DrawIndexed(6, 0, 0);
            return true;
        }

        #endregion
    }
}
