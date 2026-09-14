/*  wsjtx_mod.h

    Wrapper module that bridges the WSJT-X FT8/FT4 sidecar into Thetis.
    The WSJT-X process is spawned by the managed WsjtManager and talks over
    two named pipes (AUDIO + CTL, 8 kHz mono float32 audio wire); this native
    module provides the taps so post-DSP receiver audio moves Thetis ->
    WSJT-X for decode and modem TX audio moves WSJT-X -> the mic path TX.

    P1 is decode-only: no TX tap and no CTL pipe yet (the sidecar runs with
    rig control disabled).  P2 adds a control pipe + TX path.

    Copyright (C) 2026  SDR-VST3 Thetis integration (WSJT-X sidecar)

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
    Foundation, Inc., 51 Franklin Street, Fifth Floor,
    Boston, MA  02110-1301  USA
*/

#ifndef _wsjtx_mod_h
#define _wsjtx_mod_h

#ifndef PORT
#define PORT __declspec(dllexport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define WSJTX_NRX 2   /* RX1 + RX2; current Tier-2 bridge uses RX1 only */

/* ============================================================
 * Lifecycle -- called once from create_pipe() / destroy_pipe().
 * ============================================================ */
void create_wsjtx(void);
void destroy_wsjtx(void);

/* ============================================================
 * Public C# API (PORT-exported from ChannelMaster.dll).
 * ============================================================ */

/* RX master enable, per receiver. */
PORT void SetWsjtRxEnable(int rx, int enable);
PORT int  GetWsjtRxEnable(int rx);

/* Transport FIFO.  The managed WsjtAudioBridge drains the downsampled
 * RX audio (8 kHz mono float) and writes it into the WSJT-X AUDIO pipe. */
PORT int  WsjtDrainRx(int rx, float* out, int maxCount);  /* returns # floats drained */
PORT void WsjtFlush(void);                                /* clear all FIFOs */

/* TX path (P2): master enable, MOX mirror and the 8 kHz TX audio injection
 * FIFO.  The managed bridge reads the WSJT-X TXAUDIO pipe and pushes it
 * here; xwsjtx_tx() upsamples into the mic path while keyed. */
PORT void SetWsjtTxEnable(int enable);
PORT int  GetWsjtTxEnable(void);
PORT void SetWsjtMoxState(int mox);
PORT void WsjtPushTx(const float* in8k, int count);

/* ============================================================
 * Hot-path entry points called from xpipe() in pipe.c.
 * ============================================================ */
void xwsjtx_rx(int rx, double* rbuff_io);
void xwsjtx_tx(double* mic_io);

#ifdef __cplusplus
}
#endif

#endif /* _wsjtx_mod_h */