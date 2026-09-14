/*  wsjtx_mod.c

    Native audio tap for the WSJT-X sidecar bridge.

    RX:  xwsjtx_rx(rx, buffs[0]) is called from xpipe() on the RX1/RX2
         audio pass.  The receiver audio (interleaved stereo doubles at
         rcvr[].ch_outrate) is de-interleaved to mono, downsampled to the
         WSJT-X base rate (8000 Hz) with WDSP xresampleFV and pushed into a
         per-receiver FIFO.  WsjtDrainRx() lets the managed bridge pull
         8 kHz samples out for the named-pipe write to WSJT-X.

    TX:  WSJT-X's SoundOutPipe serves the TXAUDIO pipe; the managed bridge
         reads it and pushes 8 kHz mono float via WsjtPushTx() into a TX
         FIFO.  xwsjtx_tx() is called from xpipe() on the TX mic pass,
         upsamples back to the TX input rate and injects into mic_io (the
         same pattern as the fldigi sidecar's xfldigi_tx).  Keyed but with no
         modem audio pending, the mic path is silenced.

    Copyright (C) 2026  SDR-VST3 Thetis integration (WSJT-X sidecar)

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.
*/

#include "cmcomm.h"
#include "wsjtx_mod.h"

#ifndef _MSC_VER
#include <string.h>
#endif

extern void* create_resampleFV(int in_rate, int out_rate);
extern void  xresampleFV(float* input, float* output, int numsamps,
                         int* outsamps, void* ptr);
extern void  destroy_resampleFV(void* ptr);

#define WSJTX_MODEM_RATE  8000
#define WSJTX_MAX_BLOCK   8192
#define WSJTX_RX_FIFO_CAP 65536   /* 8 s of 8 kHz mono float per receiver */
#define WSJTX_TX_FIFO_CAP 65536   /* 8 s of 8 kHz mono float before injection */

/* ============================================================
 * State
 * ============================================================ */

static volatile long g_wx_initialized = 0;

static volatile long g_wx_rx_enabled[WSJTX_NRX] = { 0 };

static CRITICAL_SECTION g_wx_cs;    /* guards the RX FIFO set */
static int              g_wx_cs_inited = 0;

static void*            g_wx_rx_resamp[WSJTX_NRX] = { NULL };
static int              g_wx_rx_outrate[WSJTX_NRX] = { 0 };
static float*           g_wx_rx_mono[WSJTX_NRX]   = { NULL };
static float*           g_wx_rx_8k[WSJTX_NRX]     = { NULL };
static float*           g_wx_rx_fifo[WSJTX_NRX]   = { NULL };
static int              g_wx_rx_fifo_n[WSJTX_NRX] = { 0 };
static long             g_wx_rx_ovrun[WSJTX_NRX]  = { 0 };

static long             g_wx_rx_block_count[WSJTX_NRX] = { 0 };

/* ---- TX tap state ---- */
static volatile long g_wx_tx_enabled = 0;
static volatile long g_wx_mox        = 0;

static void*            g_wx_tx_resamp = NULL;
static int              g_wx_tx_outrate = 0;
static float*           g_wx_tx_scratch   = NULL;
static float*           g_wx_tx_out       = NULL;
static float*           g_wx_tx_fifo      = NULL;
static int              g_wx_tx_fifo_n    = 0;
static long             g_wx_tx_ovrun     = 0;
static long             g_wx_tx_block_count = 0;

/* ============================================================
 * FIFO helpers
 * ============================================================ */

static void wx_fifo_push_check(float* buf, int* n, int cap, const float* src,
                               int count, long* counter, const char* tag)
{
    int avail = cap - *n;
    int take  = (count < avail) ? count : avail;
    if (take > 0)
    {
        memcpy(buf + *n, src, take * sizeof(float));
        *n += take;
    }
    if (take < count)
    {
        long c = ++(*counter);
        if (c == 1 || (c % 250) == 0)
        {
            char log[160];
            sprintf_s(log, sizeof(log),
                "[WSJTX] %s OVRUN dropped=%d total=%ld\n",
                tag, count - take, c);
            OutputDebugStringA(log);
        }
    }
}

static void wx_fifo_pop(float* buf, int* n, float* dst, int count)
{
    if (count <= 0 || count > *n) return;
    memcpy(dst, buf, count * sizeof(float));
    *n -= count;
    if (*n > 0) memmove(buf, buf + count, (*n) * sizeof(float));
}

static void wx_dbg(const char* s)
{
    char log[256];
    sprintf_s(log, sizeof(log), "[WSJTX] %s\n", s);
    OutputDebugStringA(log);
}

/* ============================================================
 * Lifecycle
 * ============================================================ */

void create_wsjtx(void)
{
    if (_InterlockedAnd(&g_wx_initialized, 1)) return;

    int i;
    if (!g_wx_cs_inited)
    {
        InitializeCriticalSectionAndSpinCount(&g_wx_cs, 4000);
        g_wx_cs_inited = 1;
    }

    for (i = 0; i < WSJTX_NRX; i++)
    {
        g_wx_rx_mono[i] = (float*)calloc(WSJTX_MAX_BLOCK, sizeof(float));
        g_wx_rx_8k[i]   = (float*)calloc(WSJTX_MAX_BLOCK, sizeof(float));
        g_wx_rx_fifo[i] = (float*)calloc(WSJTX_RX_FIFO_CAP, sizeof(float));
        g_wx_rx_fifo_n[i] = 0;
    }

    g_wx_tx_scratch = (float*)calloc(WSJTX_MAX_BLOCK, sizeof(float));
    g_wx_tx_out     = (float*)calloc(WSJTX_MAX_BLOCK, sizeof(float));
    g_wx_tx_fifo    = (float*)calloc(WSJTX_TX_FIFO_CAP, sizeof(float));
    g_wx_tx_fifo_n  = 0;

    _InterlockedExchange(&g_wx_initialized, 1);
    wx_dbg("module created");
}

void destroy_wsjtx(void)
{
    _InterlockedExchange(&g_wx_initialized, 0);

    int i;
    for (i = 0; i < WSJTX_NRX; i++)
    {
        if (g_wx_rx_resamp[i]) { destroy_resampleFV(g_wx_rx_resamp[i]); g_wx_rx_resamp[i] = NULL; }
        free(g_wx_rx_mono[i]);  g_wx_rx_mono[i]  = NULL;
        free(g_wx_rx_8k[i]);    g_wx_rx_8k[i]    = NULL;
        free(g_wx_rx_fifo[i]);  g_wx_rx_fifo[i]  = NULL;
        g_wx_rx_fifo_n[i] = 0;
    }

    if (g_wx_tx_resamp) { destroy_resampleFV(g_wx_tx_resamp); g_wx_tx_resamp = NULL; }
    free(g_wx_tx_scratch);  g_wx_tx_scratch  = NULL;
    free(g_wx_tx_out);      g_wx_tx_out      = NULL;
    free(g_wx_tx_fifo);     g_wx_tx_fifo     = NULL;
    g_wx_tx_fifo_n  = 0;
    g_wx_tx_enabled = 0;
    g_wx_mox        = 0;

    if (g_wx_cs_inited)
    {
        DeleteCriticalSection(&g_wx_cs);
        g_wx_cs_inited = 0;
    }
    wx_dbg("module destroyed");
}

/* ============================================================
 * Setters
 * ============================================================ */

PORT void SetWsjtRxEnable(int rx, int enable)
{
    if (rx < 0 || rx >= WSJTX_NRX) return;
    long prev = _InterlockedExchange(&g_wx_rx_enabled[rx], enable ? 1 : 0);
    if (prev && !enable)
    {
        /* Disable edge: drop any stale queued RX audio. */
        if (_InterlockedAnd(&g_wx_initialized, 1) && g_wx_cs_inited)
        {
            EnterCriticalSection(&g_wx_cs);
            g_wx_rx_fifo_n[rx] = 0;
            LeaveCriticalSection(&g_wx_cs);
        }
    }
}

PORT int GetWsjtRxEnable(int rx)
{
    if (rx < 0 || rx >= WSJTX_NRX) return 0;
    return (int)_InterlockedAnd(&g_wx_rx_enabled[rx], 1);
}

PORT int WsjtDrainRx(int rx, float* out, int maxCount)
{
    if (!_InterlockedAnd(&g_wx_initialized, 1) || rx < 0 || rx >= WSJTX_NRX)
        return 0;
    if (!out || maxCount <= 0) return 0;
    int have = 0;
    if (g_wx_cs_inited) EnterCriticalSection(&g_wx_cs);
    have = (g_wx_rx_fifo_n[rx] < maxCount) ? g_wx_rx_fifo_n[rx] : maxCount;
    if (have > 0)
    {
        wx_fifo_pop(g_wx_rx_fifo[rx], &g_wx_rx_fifo_n[rx], out, have);
    }
    if (g_wx_cs_inited) LeaveCriticalSection(&g_wx_cs);
    return have;
}

PORT void WsjtFlush(void)
{
    if (!_InterlockedAnd(&g_wx_initialized, 1) || !g_wx_cs_inited) return;
    EnterCriticalSection(&g_wx_cs);
    for (int rx = 0; rx < WSJTX_NRX; rx++)
        g_wx_rx_fifo_n[rx] = 0;
    g_wx_tx_fifo_n = 0;
    LeaveCriticalSection(&g_wx_cs);
}

PORT void SetWsjtTxEnable(int enable)
{
    _InterlockedExchange(&g_wx_tx_enabled, enable ? 1 : 0);
    if (_InterlockedAnd(&g_wx_initialized, 1) && g_wx_cs_inited)
    {
        EnterCriticalSection(&g_wx_cs);
        g_wx_tx_fifo_n = 0;
        LeaveCriticalSection(&g_wx_cs);
    }
}

PORT int GetWsjtTxEnable(void)
{
    return (int)_InterlockedAnd(&g_wx_tx_enabled, 1);
}

PORT void SetWsjtMoxState(int mox)
{
    long prev = _InterlockedExchange(&g_wx_mox, mox ? 1 : 0);
    if (prev && !mox)
    {
        /* Un-key edge: clear the TX FIFO so stale modem audio can never
         * leak into the next over. */
        if (_InterlockedAnd(&g_wx_initialized, 1) && g_wx_cs_inited)
        {
            EnterCriticalSection(&g_wx_cs);
            g_wx_tx_fifo_n = 0;
            LeaveCriticalSection(&g_wx_cs);
        }
    }
}

PORT void WsjtPushTx(const float* in8k, int count)
{
    if (!_InterlockedAnd(&g_wx_initialized, 1) || !in8k || count <= 0) return;
    if (g_wx_cs_inited) EnterCriticalSection(&g_wx_cs);
    wx_fifo_push_check(g_wx_tx_fifo, &g_wx_tx_fifo_n, WSJTX_TX_FIFO_CAP,
                       in8k, count, &g_wx_tx_ovrun, "tx_fifo");
    if (g_wx_cs_inited) LeaveCriticalSection(&g_wx_cs);
}

/* ============================================================
 * Hot path: RX.  Called from xpipe() on the RX audio pass.
 * ============================================================ */

void xwsjtx_rx(int rx, double* rbuff_io)
{
    if (rx < 0 || rx >= WSJTX_NRX) return;
    if (!_InterlockedAnd(&g_wx_initialized, 1)) return;
    long en = _InterlockedAnd(&g_wx_rx_enabled[rx], 1);
    if (!en) return;
    if (!pcm || !rbuff_io) return;

    int outrate = pcm->rcvr[rx].ch_outrate;
    int outsize = pcm->rcvr[rx].ch_outsize;
    if (outrate <= 0 || outsize <= 0) return;
    if (outsize > WSJTX_MAX_BLOCK) return;

    if (!g_wx_rx_resamp[rx] || g_wx_rx_outrate[rx] != outrate)
    {
        if (g_wx_rx_resamp[rx]) destroy_resampleFV(g_wx_rx_resamp[rx]);
        g_wx_rx_resamp[rx] = create_resampleFV(outrate, WSJTX_MODEM_RATE);
        g_wx_rx_outrate[rx] = outrate;
    }

    /* De-interleave L channel to mono. */
    for (int i = 0; i < outsize; i++)
        g_wx_rx_mono[rx][i] = (float)rbuff_io[2 * i];

    int n8 = 0;
    xresampleFV(g_wx_rx_mono[rx], g_wx_rx_8k[rx], outsize, &n8,
                g_wx_rx_resamp[rx]);
    if (n8 > 0 && g_wx_cs_inited)
    {
        EnterCriticalSection(&g_wx_cs);
        wx_fifo_push_check(g_wx_rx_fifo[rx], &g_wx_rx_fifo_n[rx],
                           WSJTX_RX_FIFO_CAP, g_wx_rx_8k[rx], n8,
                           &g_wx_rx_ovrun[rx], "rx_fifo");
        LeaveCriticalSection(&g_wx_cs);
    }

    if (++g_wx_rx_block_count[rx] >= 625)   /* ~ once a second at 48k/750 */
    {
        char log[140];
        sprintf_s(log, sizeof(log),
            "[WSJTX] RX%d feeding %d Hz -> %d Hz (fifo=%d)\n",
            rx + 1, outrate, WSJTX_MODEM_RATE, g_wx_rx_fifo_n[rx]);
        OutputDebugStringA(log);
        g_wx_rx_block_count[rx] = 0;
    }
}

/* ============================================================
 * Hot path: TX.  Called from xpipe() on the TX mic pass.
 * ============================================================ */

void xwsjtx_tx(double* mic_io)
{
    if (!_InterlockedAnd(&g_wx_initialized, 1)) return;
    long en = _InterlockedAnd(&g_wx_tx_enabled, 1);
    if (!en) return;
    if (!pcm || !mic_io) return;

    int outrate = pcm->xcm_inrate[inid(1, 0)];
    int outsize = pcm->xcm_insize[inid(1, 0)];
    if (outrate <= 0 || outsize <= 0) return;
    if (outsize > WSJTX_MAX_BLOCK) return;

    long mox = _InterlockedAnd(&g_wx_mox, 1);
    if (!mox)
        return;   /* pass mic through untouched when not keyed */

    if (!g_wx_tx_resamp || g_wx_tx_outrate != outrate)
    {
        if (g_wx_tx_resamp) destroy_resampleFV(g_wx_tx_resamp);
        g_wx_tx_resamp = create_resampleFV(WSJTX_MODEM_RATE, outrate);
        g_wx_tx_outrate = outrate;
    }

    /* How many 8 kHz samples does a full outrate block need? */
    int need8k = (int)(((int64_t)outsize * WSJTX_MODEM_RATE + outrate - 1) / outrate);
    if (need8k > WSJTX_MAX_BLOCK) need8k = WSJTX_MAX_BLOCK;

    int have = 0;
    if (g_wx_cs_inited) EnterCriticalSection(&g_wx_cs);
    have = (g_wx_tx_fifo_n < need8k) ? g_wx_tx_fifo_n : need8k;
    if (have > 0)
        wx_fifo_pop(g_wx_tx_fifo, &g_wx_tx_fifo_n, g_wx_tx_scratch, have);
    if (g_wx_cs_inited) LeaveCriticalSection(&g_wx_cs);

    if (have > 0)
    {
        int nout = 0;
        xresampleFV(g_wx_tx_scratch, g_wx_tx_out, have, &nout, g_wx_tx_resamp);
        for (int i = 0; i < outsize; i++)
        {
            double s = (i < nout) ? (double)g_wx_tx_out[i] : 0.0;
            mic_io[2 * i]     = s;
            mic_io[2 * i + 1] = 0.0;
        }
    }
    else
    {
        /* Keyed but no modem audio pending: keyed silence. */
        for (int i = 0; i < outsize; i++)
        {
            mic_io[2 * i]     = 0.0;
            mic_io[2 * i + 1] = 0.0;
        }
    }

    if (++g_wx_tx_block_count >= 125)   /* ~ a few times a second */
    {
        char log[140];
        sprintf_s(log, sizeof(log),
            "[WSJTX] TX injecting %d Hz (fifo=%d mox=1)\n",
            outrate, g_wx_tx_fifo_n);
        OutputDebugStringA(log);
        g_wx_tx_block_count = 0;
    }
}