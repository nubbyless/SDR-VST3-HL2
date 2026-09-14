/*  vst_runtime.h

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

#ifndef _vst_runtime_h
#define _vst_runtime_h

#include <Windows.h>
#include <stddef.h>

#include "vst_host_bridge.h"

struct VstPluginRuntime;
struct VstEditorSession;
struct VstProcessingState;
typedef void (*VstRuntimeStateDirtyCallback)(void* context);

void VstProcessingState_Retain(VstProcessingState* state);
void VstProcessingState_Release(VstProcessingState*& state);

int VstRuntime_Create(
	VstPluginRuntime*& runtime,
	const wchar_t* plugin_path,
	int sample_rate,
	int max_block_size,
	int num_channels,
	wchar_t* plugin_name,
	size_t plugin_name_count,
	const wchar_t* plugin_cid);

void VstRuntime_Retain(VstPluginRuntime* runtime);
void VstRuntime_Destroy(VstPluginRuntime*& runtime);
int VstRuntime_Reconfigure(VstPluginRuntime* runtime, int sample_rate, int max_block_size, int num_channels);
int VstRuntime_Process(VstPluginRuntime* runtime, double* interleaved_buffer, int frames, int chain_channels);
int VstRuntime_GetStateSize(VstPluginRuntime* runtime);
int VstRuntime_GetState(VstPluginRuntime* runtime, void* buffer, int buffer_size, int* bytes_written);
int VstRuntime_SetState(VstPluginRuntime* runtime, const void* buffer, int buffer_size);
void VstRuntime_SetStateDirtyCallback(VstPluginRuntime* runtime, VstRuntimeStateDirtyCallback callback, void* context);
int VstRuntime_ProbePluginMetadataOnly(const wchar_t* plugin_path, VstPluginProbeInfo* info);
int VstRuntime_ProbePluginAllClasses(const wchar_t* plugin_path, VstPluginProbeInfo* infos, int max_count, int* actual_count);
int VstRuntime_OpenEditor(VstPluginRuntime* runtime, VstProcessingState* owner_state, int plugin_index, HWND parent_window, int& width, int& height, int& can_resize, VstEditorSession*& session);
VstProcessingState* VstRuntime_GetEditorOwnerState(VstEditorSession* session);
int VstRuntime_CloseOpenEditor(VstPluginRuntime* runtime);
int VstRuntime_CloseEditor(VstEditorSession*& session);
int VstRuntime_ResizeEditor(VstEditorSession* session, int width, int height);

#endif
