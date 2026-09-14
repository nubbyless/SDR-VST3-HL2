/*  vst_plugin_runtime.h

This file is part of a program that implements a Software-Defined Radio.

This code/file can be found on GitHub : https://github.com/nubbyless/Thetis-Plus

Copyright (C) 2026 ChasingCoffee
Copyright (C) 2026 nubbyless <nubbyless@yahoo.com>

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

#ifndef _vst_plugin_runtime_h
#define _vst_plugin_runtime_h

#include <Windows.h>
#include <stddef.h>

#include "vst_host_bridge.h"

struct ThetisPluginRuntime;
struct VstEditorSession;
struct VstProcessingState;
typedef void (*ThetisPluginRuntimeStateDirtyCallback)(void* context);

int ThetisPluginRuntime_Create(
	ThetisPluginRuntime*& runtime,
	const wchar_t* plugin_path,
	int sample_rate,
	int max_block_size,
	int num_channels,
	wchar_t* plugin_name,
	size_t plugin_name_count,
	const wchar_t* plugin_cid);

void ThetisPluginRuntime_Retain(ThetisPluginRuntime* runtime);
void ThetisPluginRuntime_Destroy(ThetisPluginRuntime*& runtime);
int ThetisPluginRuntime_Reconfigure(ThetisPluginRuntime* runtime, int sample_rate, int max_block_size, int num_channels);
int ThetisPluginRuntime_Process(ThetisPluginRuntime* runtime, double* interleaved_buffer, int frames, int chain_channels);
int ThetisPluginRuntime_GetStateSize(ThetisPluginRuntime* runtime);
int ThetisPluginRuntime_GetState(ThetisPluginRuntime* runtime, void* buffer, int buffer_size, int* bytes_written);
int ThetisPluginRuntime_SetState(ThetisPluginRuntime* runtime, const void* buffer, int buffer_size);
void ThetisPluginRuntime_SetStateDirtyCallback(ThetisPluginRuntime* runtime, ThetisPluginRuntimeStateDirtyCallback callback, void* context);
int ThetisPluginRuntime_OpenEditor(ThetisPluginRuntime* runtime, VstProcessingState* owner_state, int plugin_index, HWND parent_window, int& width, int& height, int& can_resize, VstEditorSession*& session);
int ThetisPluginRuntime_CloseOpenEditor(ThetisPluginRuntime* runtime);
int ThetisPluginRuntime_CloseEditor(VstEditorSession*& session);
int ThetisPluginRuntime_ResizeEditor(VstEditorSession* session, int width, int height);
VstPluginFormat ThetisPluginRuntime_GetFormat(ThetisPluginRuntime* runtime);

#endif
