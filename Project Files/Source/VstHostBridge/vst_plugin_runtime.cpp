/*  vst_plugin_runtime.cpp

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

#include "vst_plugin_runtime.h"

#include "vst_runtime.h"

#include <cwchar>
#include <new>

struct ThetisPluginRuntime
{
	volatile LONG ref_count = 1;
	VstPluginFormat format = VST_PLUGIN_FORMAT_UNKNOWN;
	VstPluginRuntime* vst3_runtime = nullptr;
};

namespace
{
	VstPluginFormat detect_plugin_format(const wchar_t* plugin_path)
	{
		const wchar_t* extension;

		if (!plugin_path || !plugin_path[0])
			return VST_PLUGIN_FORMAT_UNKNOWN;

		extension = wcsrchr(plugin_path, L'.');
		if (!extension)
			return VST_PLUGIN_FORMAT_UNKNOWN;
		if (_wcsicmp(extension, L".vst3") == 0)
			return VST_PLUGIN_FORMAT_VST3;
		return VST_PLUGIN_FORMAT_UNKNOWN;
	}
}

int ThetisPluginRuntime_Create(
	ThetisPluginRuntime*& runtime,
	const wchar_t* plugin_path,
	int sample_rate,
	int max_block_size,
	int num_channels,
	wchar_t* plugin_name,
	size_t plugin_name_count,
	const wchar_t* plugin_cid)
{
	ThetisPluginRuntime* wrapper = nullptr;
	VstPluginRuntime* vst3_runtime = nullptr;
	VstPluginFormat format;
	int result;

	runtime = nullptr;
	format = detect_plugin_format(plugin_path);
	if (format == VST_PLUGIN_FORMAT_VST3)
	{
		result = VstRuntime_Create(
			vst3_runtime,
			plugin_path,
			sample_rate,
			max_block_size,
			num_channels,
			plugin_name,
			plugin_name_count,
			plugin_cid);
	}
	else
		return -51;
	if (result != 0)
		return result;

	wrapper = new (std::nothrow) ThetisPluginRuntime();
	if (!wrapper)
	{
		VstRuntime_Destroy(vst3_runtime);
		return -52;
	}

	wrapper->format = format;
	wrapper->vst3_runtime = vst3_runtime;
	runtime = wrapper;
	return 0;
}

void ThetisPluginRuntime_Retain(ThetisPluginRuntime* runtime)
{
	if (runtime)
		InterlockedIncrement(&runtime->ref_count);
}

void ThetisPluginRuntime_Destroy(ThetisPluginRuntime*& runtime)
{
	ThetisPluginRuntime* local_runtime = runtime;

	runtime = nullptr;
	if (!local_runtime)
		return;
	if (InterlockedDecrement(&local_runtime->ref_count) != 0)
		return;

	if (local_runtime->format == VST_PLUGIN_FORMAT_VST3)
		VstRuntime_Destroy(local_runtime->vst3_runtime);
	delete local_runtime;
}

int ThetisPluginRuntime_Reconfigure(ThetisPluginRuntime* runtime, int sample_rate, int max_block_size, int num_channels)
{
	if (!runtime)
		return -1;

	switch (runtime->format)
	{
	case VST_PLUGIN_FORMAT_VST3:
		return VstRuntime_Reconfigure(runtime->vst3_runtime, sample_rate, max_block_size, num_channels);
	default:
		return -1;
	}
}

int ThetisPluginRuntime_Process(ThetisPluginRuntime* runtime, double* interleaved_buffer, int frames, int chain_channels)
{
	if (!runtime)
		return -1;

	switch (runtime->format)
	{
	case VST_PLUGIN_FORMAT_VST3:
		return VstRuntime_Process(runtime->vst3_runtime, interleaved_buffer, frames, chain_channels);
	default:
		return -1;
	}
}

int ThetisPluginRuntime_GetStateSize(ThetisPluginRuntime* runtime)
{
	if (!runtime)
		return -1;

	switch (runtime->format)
	{
	case VST_PLUGIN_FORMAT_VST3:
		return VstRuntime_GetStateSize(runtime->vst3_runtime);
	default:
		return -1;
	}
}

int ThetisPluginRuntime_GetState(ThetisPluginRuntime* runtime, void* buffer, int buffer_size, int* bytes_written)
{
	if (!runtime)
		return -1;

	switch (runtime->format)
	{
	case VST_PLUGIN_FORMAT_VST3:
		return VstRuntime_GetState(runtime->vst3_runtime, buffer, buffer_size, bytes_written);
	default:
		return -1;
	}
}

int ThetisPluginRuntime_SetState(ThetisPluginRuntime* runtime, const void* buffer, int buffer_size)
{
	if (!runtime)
		return -1;

	switch (runtime->format)
	{
	case VST_PLUGIN_FORMAT_VST3:
		return VstRuntime_SetState(runtime->vst3_runtime, buffer, buffer_size);
	default:
		return -1;
	}
}

void ThetisPluginRuntime_SetStateDirtyCallback(ThetisPluginRuntime* runtime, ThetisPluginRuntimeStateDirtyCallback callback, void* context)
{
	if (!runtime)
		return;

	switch (runtime->format)
	{
	case VST_PLUGIN_FORMAT_VST3:
		VstRuntime_SetStateDirtyCallback(runtime->vst3_runtime, callback, context);
		break;
	default:
		break;
	}
}

int ThetisPluginRuntime_OpenEditor(ThetisPluginRuntime* runtime, VstProcessingState* owner_state, int plugin_index, HWND parent_window, int& width, int& height, int& can_resize, VstEditorSession*& session)
{
	if (!runtime)
		return -1;

	switch (runtime->format)
	{
	case VST_PLUGIN_FORMAT_VST3:
		return VstRuntime_OpenEditor(runtime->vst3_runtime, owner_state, plugin_index, parent_window, width, height, can_resize, session);
	default:
		return -1;
	}
}

int ThetisPluginRuntime_CloseOpenEditor(ThetisPluginRuntime* runtime)
{
	if (!runtime)
		return -1;

	switch (runtime->format)
	{
	case VST_PLUGIN_FORMAT_VST3:
		return VstRuntime_CloseOpenEditor(runtime->vst3_runtime);
	default:
		return -1;
	}
}

int ThetisPluginRuntime_CloseEditor(VstEditorSession*& session)
{
	return VstRuntime_CloseEditor(session);
}

int ThetisPluginRuntime_ResizeEditor(VstEditorSession* session, int width, int height)
{
	return VstRuntime_ResizeEditor(session, width, height);
}

VstPluginFormat ThetisPluginRuntime_GetFormat(ThetisPluginRuntime* runtime)
{
	return runtime ? runtime->format : VST_PLUGIN_FORMAT_UNKNOWN;
}
