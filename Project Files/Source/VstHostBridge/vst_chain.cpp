/*  vst_chain.cpp

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

#include "vst_chain.h"

#include "vst_plugin_runtime.h"

#include <cmath>
#include <cstring>
#include <cwchar>
#include <cstdio>
#include <memory>
#include <new>
#include <vector>
#include <process.h>

static void chain_log(const wchar_t* msg)
{
	static bool checked = false;
	static bool enabled = false;
	if (!checked)
	{
		wchar_t value[2] = {};
		checked = true;
		enabled = GetEnvironmentVariableW(L"THETIS_VST_FILE_LOG", value, _countof(value)) > 0;
	}

	wchar_t path[MAX_PATH];
	if (enabled && GetTempPathW(_countof(path), path) > 0)
	{
		wcscat_s(path, L"ThetisVstChain.log");
		FILE* f = 0;
		if (_wfopen_s(&f, path, L"a+, ccs=UTF-8") == 0 && f)
		{
			fputws(msg, f);
			fclose(f);
		}
	}
	OutputDebugStringW(msg);
}

/*
Deferred destroy: runtimes that need to be destroyed are handed off to a
background thread so the IPC thread (which calls release_processing_state)
is not blocked by ThetisPluginRuntime_Destroy (which can take 10+ seconds
if the host thread is stuck in a VST plugin call).
*/
namespace
{
	struct DeferredDestroyItem
	{
		ThetisPluginRuntime* runtime;
	};

	SRWLOCK g_deferred_lock = SRWLOCK_INIT;
	std::vector<DeferredDestroyItem> g_deferred_items;
	HANDLE g_deferred_thread = nullptr;
	HANDLE g_deferred_event = nullptr;
	HANDLE g_deferred_stop_event = nullptr;

	void deferred_destroy_runtime_inner(ThetisPluginRuntime* runtime)
	{
		if (!runtime)
			return;

		__try
		{
			ThetisPluginRuntime_Destroy(runtime);
		}
		__except (EXCEPTION_EXECUTE_HANDLER)
		{
			wchar_t dbg[256];
			swprintf_s(dbg, L"[chain] deferred_destroy_inner CRASH exception=0x%08X\r\n",
				static_cast<unsigned long>(GetExceptionCode()));
			chain_log(dbg);
		}
	}

	void deferred_destroy_runtime(ThetisPluginRuntime* runtime);

	unsigned __stdcall deferred_destroy_thread_proc(void*)
	{
		for (;;)
		{
			HANDLE handles[2] = { g_deferred_stop_event, g_deferred_event };
			DWORD wait = WaitForMultipleObjects(2, handles, FALSE, INFINITE);

			if (wait == WAIT_OBJECT_0 || wait == WAIT_FAILED)
				break;

			for (;;)
			{
				std::vector<DeferredDestroyItem> batch;
				AcquireSRWLockExclusive(&g_deferred_lock);
				if (g_deferred_items.empty())
				{
					ReleaseSRWLockExclusive(&g_deferred_lock);
					break;
				}
				batch.swap(g_deferred_items);
				ReleaseSRWLockExclusive(&g_deferred_lock);

				{
					wchar_t dbg[128];
					swprintf_s(dbg, L"[chain] deferred_destroy batch size=%d\r\n", static_cast<int>(batch.size()));
					chain_log(dbg);
				}

				for (size_t i = 0; i < batch.size(); ++i)
				{
					deferred_destroy_runtime_inner(batch[i].runtime);
				}
			}
		}
		return 0;
	}

	void ensure_deferred_thread()
	{
		if (g_deferred_event)
			return;

		g_deferred_event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
		g_deferred_stop_event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
		if (!g_deferred_event || !g_deferred_stop_event)
			return;

		uintptr_t h = _beginthreadex(nullptr, 0, deferred_destroy_thread_proc, nullptr, 0, nullptr);
		if (h)
		{
			g_deferred_thread = reinterpret_cast<HANDLE>(h);
			chain_log(L"[chain] deferred_destroy thread started\r\n");
		}
	}

	void deferred_destroy_runtime(ThetisPluginRuntime* runtime)
	{
		if (!runtime)
			return;

		ensure_deferred_thread();
		if (!g_deferred_event)
		{
			__try
			{
				ThetisPluginRuntime_Destroy(runtime);
			}
			__except (EXCEPTION_EXECUTE_HANDLER)
			{
				wchar_t dbg[256];
				swprintf_s(dbg, L"[chain] deferred_destroy_runtime fallback CRASH exception=0x%08X\r\n",
					static_cast<unsigned long>(GetExceptionCode()));
				chain_log(dbg);
			}
			return;
		}

		ThetisPluginRuntime_Retain(runtime);
		AcquireSRWLockExclusive(&g_deferred_lock);
		g_deferred_items.push_back({ runtime });
		ReleaseSRWLockExclusive(&g_deferred_lock);
		SetEvent(g_deferred_event);
	}

	void shutdown_deferred_destroy_remaining()
	{
		AcquireSRWLockExclusive(&g_deferred_lock);
		std::vector<DeferredDestroyItem> remaining;
		remaining.swap(g_deferred_items);
		ReleaseSRWLockExclusive(&g_deferred_lock);

		for (size_t i = 0; i < remaining.size(); ++i)
			deferred_destroy_runtime_inner(remaining[i].runtime);
	}

	void shutdown_deferred_thread()
	{
		if (!g_deferred_thread)
			return;

		SetEvent(g_deferred_stop_event);
		WaitForSingleObject(g_deferred_thread, 15000);
		CloseHandle(g_deferred_thread);
		g_deferred_thread = nullptr;
		CloseHandle(g_deferred_event);
		g_deferred_event = nullptr;
		CloseHandle(g_deferred_stop_event);
		g_deferred_stop_event = nullptr;

		shutdown_deferred_destroy_remaining();
	}
}

/*
VstChain owns the in-host chain model for one RX or TX path.

Important split:
- descriptor array on VstChainState: control-plane/source model
- active VstProcessingState: immutable processing snapshot used by audio

Structural edits publish a new processing state rather than mutating the live
audio view in place.
*/

namespace
{
	struct CapturedPluginState
	{
		std::unique_ptr<unsigned char[]> data;
		int size = 0;
	};

	union DoubleBits
	{
		double value;
		LONG64 bits;
	};

	LONG64 double_to_bits(double value)
	{
		DoubleBits converter = {};
		converter.value = value;
		return converter.bits;
	}

	double bits_to_double(LONG64 bits)
	{
		DoubleBits converter = {};
		converter.bits = bits;
		return converter.value;
	}

	VstPluginFormat detect_plugin_format_from_path(const wchar_t* plugin_path)
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

	void clear_descriptor(VstPluginSlot& plugin)
	{
		InterlockedExchange(&plugin.enabled, 0);
		InterlockedExchange(&plugin.bypass, 0);
		InterlockedExchange(&plugin.load_state, VST_PLUGIN_LOAD_NONE);
		InterlockedExchange(&plugin.format, VST_PLUGIN_FORMAT_UNKNOWN);
		plugin.path[0] = L'\0';
		plugin.name[0] = L'\0';
		plugin.cid[0] = L'\0';
	}

	void copy_descriptor(VstPluginSlot& destination, const VstPluginSlot& source)
	{
		InterlockedExchange(&destination.enabled, InterlockedCompareExchange(const_cast<LONG*>(&source.enabled), 0, 0));
		InterlockedExchange(&destination.bypass, InterlockedCompareExchange(const_cast<LONG*>(&source.bypass), 0, 0));
		InterlockedExchange(&destination.load_state, InterlockedCompareExchange(const_cast<LONG*>(&source.load_state), 0, 0));
		InterlockedExchange(&destination.format, InterlockedCompareExchange(const_cast<LONG*>(&source.format), 0, 0));
		wcsncpy_s(destination.path, VST_MAX_PLUGIN_PATH_CHARS, source.path, _TRUNCATE);
		wcsncpy_s(destination.name, VST_MAX_PLUGIN_NAME_CHARS, source.name, _TRUNCATE);
		wcsncpy_s(destination.cid, VST_MAX_PLUGIN_CID_CHARS, source.cid, _TRUNCATE);
	}

	void copy_descriptor_array(VstPluginSlot* destination, const VstPluginSlot* source, int plugin_count)
	{
		int i;

		for (i = 0; i < plugin_count; ++i)
			copy_descriptor(destination[i], source[i]);
		for (; i < VST_MAX_CHAIN_PLUGINS; ++i)
			clear_descriptor(destination[i]);
	}

	void mark_descriptors_pending(VstPluginSlot* descriptors, int plugin_count)
	{
		int i;

		for (i = 0; i < plugin_count; ++i)
		{
			if (descriptors[i].path[0])
				InterlockedExchange(&descriptors[i].load_state, VST_PLUGIN_LOAD_DESCRIPTOR_ONLY);
			else
				InterlockedExchange(&descriptors[i].load_state, VST_PLUGIN_LOAD_NONE);
		}
	}

	void derive_plugin_name(const wchar_t* plugin_path, wchar_t* plugin_name, size_t plugin_name_count)
	{
		const wchar_t* filename;

		if (!plugin_name || plugin_name_count == 0)
			return;

		plugin_name[0] = L'\0';
		if (!plugin_path || !plugin_path[0])
			return;

		filename = wcsrchr(plugin_path, L'\\');
		if (!filename)
			filename = wcsrchr(plugin_path, L'/');
		if (filename)
			filename++;
		else
			filename = plugin_path;

		wcsncpy_s(plugin_name, plugin_name_count, filename, _TRUNCATE);
	}

	bool normalize_plugin_path(const wchar_t* plugin_path, wchar_t* normalized_path, unsigned long normalized_count)
	{
		DWORD required_length;

		if (!plugin_path || !plugin_path[0] || !normalized_path || normalized_count == 0)
			return false;

		required_length = GetFullPathNameW(plugin_path, normalized_count, normalized_path, 0);
		if (required_length == 0 || required_length >= normalized_count)
			return false;

		return GetFileAttributesW(normalized_path) != INVALID_FILE_ATTRIBUTES;
	}

	void retain_processing_state(VstProcessingState* state)
	{
		if (state)
			InterlockedIncrement(&state->ref_count);
	}

	void release_processing_state(VstProcessingState*& state)
	{
		VstProcessingState* local_state = state;
		int i;

		state = 0;
		if (!local_state)
			return;

		if (InterlockedDecrement(&local_state->ref_count) != 0)
		{
			wchar_t dbg[128];
			swprintf_s(dbg, L"[chain] release_state %p ref_count=%ld (kept)\r\n",
				reinterpret_cast<void*>(local_state), InterlockedCompareExchange(&local_state->ref_count, 0, 0));
			chain_log(dbg);
			return;
		}

		{
			wchar_t dbg[256];
			swprintf_s(dbg, L"[chain] release_state %p DESTROYING %d runtimes (deferred)\r\n",
				reinterpret_cast<void*>(local_state), local_state->plugin_count);
			chain_log(dbg);
		}

		for (i = 0; i < local_state->plugin_count; ++i)
		{
			if (local_state->plugins[i].runtime)
			{
				wchar_t dbg[256];
				swprintf_s(dbg, L"[chain] release_state deferring runtime[%d] %p\r\n",
					i, reinterpret_cast<void*>(local_state->plugins[i].runtime));
				chain_log(dbg);
				deferred_destroy_runtime(local_state->plugins[i].runtime);
				local_state->plugins[i].runtime = 0;
			}
		}

		delete local_state;
	}

	void apply_processing_state_dirty_callback(VstProcessingState* state, VstChainStateDirtyCallback callback, void* context)
	{
		int i;

		if (!state)
			return;

		for (i = 0; i < state->plugin_count; ++i)
		{
			if (state->plugins[i].runtime)
				ThetisPluginRuntime_SetStateDirtyCallback(state->plugins[i].runtime, callback, context);
		}
	}

	void close_processing_state_editors(VstProcessingState* state)
	{
		int i;

		if (!state)
			return;

		for (i = 0; i < state->plugin_count; ++i)
		{
			if (state->plugins[i].runtime)
				ThetisPluginRuntime_CloseOpenEditor(state->plugins[i].runtime);
		}
	}

	void close_active_chain_editors(VstChainState& chain)
	{
		VstProcessingState* active_state;

		AcquireSRWLockShared(&chain.state_lock);
		active_state = chain.active_state;
		retain_processing_state(active_state);
		ReleaseSRWLockShared(&chain.state_lock);

		close_processing_state_editors(active_state);
		release_processing_state(active_state);
	}

	void snapshot_descriptors(
		const VstChainState& chain,
		VstPluginSlot* descriptors,
		int& plugin_count,
		int& sample_rate,
		int& max_block_size,
		int& num_channels,
		int& created,
		LONG& generation)
	{
		AcquireSRWLockShared(const_cast<SRWLOCK*>(&chain.state_lock));
		plugin_count = InterlockedCompareExchange(const_cast<LONG*>(&chain.plugin_count), 0, 0);
		sample_rate = chain.sample_rate;
		max_block_size = chain.max_block_size;
		num_channels = chain.num_channels;
		created = InterlockedCompareExchange(const_cast<LONG*>(&chain.created), 0, 0);
		generation = InterlockedCompareExchange(const_cast<LONG*>(&chain.rebuild_generation), 0, 0);
		copy_descriptor_array(descriptors, chain.plugins, plugin_count);
		ReleaseSRWLockShared(const_cast<SRWLOCK*>(&chain.state_lock));
	}

	void clear_captured_states(CapturedPluginState* captured_states, int count)
	{
		int i;

		if (!captured_states)
			return;

		for (i = 0; i < count; ++i)
		{
			captured_states[i].data.reset();
			captured_states[i].size = 0;
		}
	}

	void capture_plugin_state(ThetisPluginRuntime* runtime, CapturedPluginState& captured_state)
	{
		int state_size;
		int bytes_written = 0;
		std::unique_ptr<unsigned char[]> buffer;

		captured_state.data.reset();
		captured_state.size = 0;
		if (!runtime)
			return;

		state_size = ThetisPluginRuntime_GetStateSize(runtime);
		if (state_size <= 0)
			return;

		buffer.reset(new (std::nothrow) unsigned char[state_size]);
		if (!buffer)
			return;

		if (ThetisPluginRuntime_GetState(runtime, buffer.get(), state_size, &bytes_written) != 0 || bytes_written <= 0)
			return;

		captured_state.data = std::move(buffer);
		captured_state.size = bytes_written;
	}

	void capture_processing_states_with_map(
		VstProcessingState* source_state,
		const int* source_indices,
		int destination_count,
		CapturedPluginState* captured_states)
	{
		int destination_index;

		clear_captured_states(captured_states, destination_count);
		if (!source_state || !captured_states)
			return;

		for (destination_index = 0; destination_index < destination_count; ++destination_index)
		{
			int source_index = source_indices ? source_indices[destination_index] : destination_index;
			VstProcessingPlugin* source_plugin;

			if (source_index < 0 || source_index >= source_state->plugin_count)
				continue;

			source_plugin = &source_state->plugins[source_index];
			if (!source_plugin->runtime || InterlockedCompareExchange(&source_plugin->load_state, 0, 0) != VST_PLUGIN_LOAD_ACTIVE)
				continue;

			capture_plugin_state(source_plugin->runtime, captured_states[destination_index]);
		}
	}

	int build_processing_state(
		const VstPluginSlot* descriptors,
		int plugin_count,
		int sample_rate,
		int max_block_size,
		int num_channels,
		VstProcessingState*& processing_state,
		VstPluginSlot* resolved_descriptors,
		const CapturedPluginState* captured_states)
	{
		std::unique_ptr<VstProcessingState> new_state;
		int i;

		processing_state = 0;
		copy_descriptor_array(resolved_descriptors, descriptors, plugin_count);

		if (plugin_count <= 0)
			return 0;
		if (sample_rate <= 0 || max_block_size <= 0 || num_channels <= 0)
			return -1;

		new_state.reset(new (std::nothrow) VstProcessingState());
		if (!new_state)
			return -2;

		memset(new_state.get(), 0, sizeof(VstProcessingState));
		InterlockedExchange(&new_state->ref_count, 1);
		new_state->sample_rate = sample_rate;
		new_state->max_block_size = max_block_size;
		new_state->num_channels = num_channels;
		new_state->plugin_count = plugin_count;

		for (i = 0; i < plugin_count; ++i)
		{
			VstProcessingPlugin& runtime_plugin = new_state->plugins[i];
			VstPluginSlot& descriptor = resolved_descriptors[i];
			int result;

			InterlockedExchange(&runtime_plugin.enabled, InterlockedCompareExchange(&descriptor.enabled, 0, 0));
			InterlockedExchange(&runtime_plugin.bypass, InterlockedCompareExchange(&descriptor.bypass, 0, 0));
			runtime_plugin.runtime = 0;

			if (!descriptor.path[0])
			{
				InterlockedExchange(&descriptor.load_state, VST_PLUGIN_LOAD_NONE);
				InterlockedExchange(&runtime_plugin.load_state, VST_PLUGIN_LOAD_NONE);
				continue;
			}

			if (!descriptor.name[0])
				derive_plugin_name(descriptor.path, descriptor.name, VST_MAX_PLUGIN_NAME_CHARS);

			result = ThetisPluginRuntime_Create(
				runtime_plugin.runtime,
				descriptor.path,
				sample_rate,
				max_block_size,
				num_channels,
				descriptor.name,
				VST_MAX_PLUGIN_NAME_CHARS,
				descriptor.cid[0] ? descriptor.cid : nullptr);

			InterlockedExchange(&descriptor.load_state, result == 0 ? VST_PLUGIN_LOAD_ACTIVE : VST_PLUGIN_LOAD_FAILED);
			InterlockedExchange(&runtime_plugin.load_state, result == 0 ? VST_PLUGIN_LOAD_ACTIVE : VST_PLUGIN_LOAD_FAILED);
			{
				wchar_t dbg[512];
				swprintf_s(dbg, L"[chain] Runtime_Create i=%d result=%d path=\"%s\"\r\n", i, result, descriptor.path);
				chain_log(dbg);
			}
			if (result == 0 && captured_states && captured_states[i].data && captured_states[i].size > 0)
				ThetisPluginRuntime_SetState(runtime_plugin.runtime, captured_states[i].data.get(), captured_states[i].size);
		}

		processing_state = new_state.release();
		return 0;
	}

	int build_reordered_processing_state(
		VstProcessingState* old_state,
		const VstPluginSlot* descriptors,
		int plugin_count,
		int from_index,
		int to_index,
		VstProcessingState*& processing_state,
		VstPluginSlot* resolved_descriptors)
	{
		std::unique_ptr<VstProcessingState> new_state;
		int new_index;

		processing_state = 0;
		copy_descriptor_array(resolved_descriptors, descriptors, plugin_count);

		if (!old_state || plugin_count <= 0 || plugin_count != old_state->plugin_count)
			return -1;

		new_state.reset(new (std::nothrow) VstProcessingState());
		if (!new_state)
			return -2;

		memset(new_state.get(), 0, sizeof(VstProcessingState));
		InterlockedExchange(&new_state->ref_count, 1);
		new_state->sample_rate = old_state->sample_rate;
		new_state->max_block_size = old_state->max_block_size;
		new_state->num_channels = old_state->num_channels;
		new_state->plugin_count = plugin_count;

		for (new_index = 0; new_index < plugin_count; ++new_index)
		{
			int old_index = new_index;
			VstProcessingPlugin& destination = new_state->plugins[new_index];
			VstPluginSlot& descriptor = resolved_descriptors[new_index];

			if (from_index < to_index)
			{
				if (new_index >= from_index && new_index <= to_index)
					old_index = (new_index == to_index) ? from_index : (new_index + 1);
			}
			else if (from_index > to_index)
			{
				if (new_index >= to_index && new_index <= from_index)
					old_index = (new_index == to_index) ? from_index : (new_index - 1);
			}

			InterlockedExchange(&destination.enabled, InterlockedCompareExchange(&descriptor.enabled, 0, 0));
			InterlockedExchange(&destination.bypass, InterlockedCompareExchange(&descriptor.bypass, 0, 0));
			InterlockedExchange(&destination.load_state, InterlockedCompareExchange(&old_state->plugins[old_index].load_state, 0, 0));
			destination.runtime = old_state->plugins[old_index].runtime;
			ThetisPluginRuntime_Retain(destination.runtime);
			InterlockedExchange(&descriptor.load_state, InterlockedCompareExchange(&destination.load_state, 0, 0));
		}

		processing_state = new_state.release();
		return 0;
	}

	int build_mapped_processing_state(
		VstProcessingState* old_state,
		const VstPluginSlot* descriptors,
		int plugin_count,
		const int* source_indices,
		VstProcessingState*& processing_state,
		VstPluginSlot* resolved_descriptors,
		const CapturedPluginState* captured_states)
	{
		std::unique_ptr<VstProcessingState> new_state;
		int new_index;

		processing_state = 0;
		copy_descriptor_array(resolved_descriptors, descriptors, plugin_count);

		if (!old_state)
			return -1;
		if (plugin_count < 0 || plugin_count > VST_MAX_CHAIN_PLUGINS)
			return -1;

		new_state.reset(new (std::nothrow) VstProcessingState());
		if (!new_state)
			return -2;

		memset(new_state.get(), 0, sizeof(VstProcessingState));
		InterlockedExchange(&new_state->ref_count, 1);
		new_state->sample_rate = old_state->sample_rate;
		new_state->max_block_size = old_state->max_block_size;
		new_state->num_channels = old_state->num_channels;
		new_state->plugin_count = plugin_count;

		for (new_index = 0; new_index < plugin_count; ++new_index)
		{
			int source_index = source_indices ? source_indices[new_index] : new_index;
			VstProcessingPlugin& destination = new_state->plugins[new_index];
			VstPluginSlot& descriptor = resolved_descriptors[new_index];

			InterlockedExchange(&destination.enabled, InterlockedCompareExchange(&descriptor.enabled, 0, 0));
			InterlockedExchange(&destination.bypass, InterlockedCompareExchange(&descriptor.bypass, 0, 0));
			destination.runtime = 0;

			if (source_index >= 0 && source_index < old_state->plugin_count)
			{
				InterlockedExchange(&destination.load_state, InterlockedCompareExchange(&old_state->plugins[source_index].load_state, 0, 0));
				destination.runtime = old_state->plugins[source_index].runtime;
				ThetisPluginRuntime_Retain(destination.runtime);
				InterlockedExchange(&descriptor.load_state, InterlockedCompareExchange(&destination.load_state, 0, 0));
				continue;
			}

			if (!descriptor.path[0])
			{
				InterlockedExchange(&descriptor.load_state, VST_PLUGIN_LOAD_NONE);
				InterlockedExchange(&destination.load_state, VST_PLUGIN_LOAD_NONE);
				continue;
			}

			if (!descriptor.name[0])
				derive_plugin_name(descriptor.path, descriptor.name, VST_MAX_PLUGIN_NAME_CHARS);

			{
				int result = ThetisPluginRuntime_Create(
					destination.runtime,
					descriptor.path,
					new_state->sample_rate,
					new_state->max_block_size,
					new_state->num_channels,
					descriptor.name,
					VST_MAX_PLUGIN_NAME_CHARS,
					descriptor.cid[0] ? descriptor.cid : nullptr);

				InterlockedExchange(&descriptor.load_state, result == 0 ? VST_PLUGIN_LOAD_ACTIVE : VST_PLUGIN_LOAD_FAILED);
				InterlockedExchange(&destination.load_state, result == 0 ? VST_PLUGIN_LOAD_ACTIVE : VST_PLUGIN_LOAD_FAILED);
				{
					wchar_t dbg[512];
					swprintf_s(dbg, L"[chain] Runtime_Create mapped i=%d result=%d path=\"%s\"\r\n", new_index, result, descriptor.path);
					chain_log(dbg);
				}
				if (result == 0 && captured_states && captured_states[new_index].data && captured_states[new_index].size > 0)
					ThetisPluginRuntime_SetState(destination.runtime, captured_states[new_index].data.get(), captured_states[new_index].size);
			}
		}

		processing_state = new_state.release();
		return 0;
	}

	bool publish_processing_state(
		VstChainState& chain,
		const VstPluginSlot* descriptors,
		int plugin_count,
		int sample_rate,
		int max_block_size,
		int num_channels,
		int created,
		LONG expected_generation,
		VstProcessingState* new_state)
	{
		VstProcessingState* old_state;
		LONG current_generation = InterlockedCompareExchange(&chain.rebuild_generation, 0, 0);
		BOOL stopping = InterlockedCompareExchange(&chain.stop_worker, 0, 0);

		{
			wchar_t dbg[512];
			swprintf_s(dbg, L"[chain] publish企图 expected_gen=%ld current_gen=%ld stopping=%d pc=%d created=%d\r\n",
				expected_generation, current_generation, stopping, plugin_count, created);
			chain_log(dbg);
		}
		for (int pi = 0; pi < plugin_count && pi < 8; ++pi)
		{
			wchar_t dbg[256];
			swprintf_s(dbg, L"[chain] publish desc[%d] load=%d path=\"%s\"\r\n",
				pi, (int)InterlockedCompareExchange((LONG*)&descriptors[pi].load_state, 0, 0), descriptors[pi].path);
			chain_log(dbg);
		}

		AcquireSRWLockExclusive(&chain.state_lock);
		if (current_generation != expected_generation || stopping)
		{
			{
				wchar_t dbg[256];
				swprintf_s(dbg, L"[chain] publish REJECTED gen_mismatch=%d stopping=%d\r\n",
					current_generation != expected_generation, stopping);
				chain_log(dbg);
			}
			ReleaseSRWLockExclusive(&chain.state_lock);
			return false;
		}

		old_state = chain.active_state;
		chain.active_state = new_state;
		chain.sample_rate = sample_rate;
		chain.max_block_size = max_block_size;
		chain.num_channels = num_channels;
		InterlockedExchange(&chain.created, created ? 1 : 0);
		InterlockedExchange(&chain.ready, created ? 1 : 0);
		InterlockedExchange(&chain.plugin_count, plugin_count);
		copy_descriptor_array(chain.plugins, descriptors, plugin_count);
		ReleaseSRWLockExclusive(&chain.state_lock);

		{
			wchar_t dbg[256];
			swprintf_s(dbg, L"[chain] publish OK gen=%ld pc=%d ready=%d\r\n",
				expected_generation, plugin_count, created);
			chain_log(dbg);
		}
		for (int pi = 0; pi < plugin_count && pi < 8; ++pi)
		{
			wchar_t dbg[256];
			swprintf_s(dbg, L"[chain] publish final[%d] load=%d path=\"%s\"\r\n",
				pi, (int)InterlockedCompareExchange((LONG*)&chain.plugins[pi].load_state, 0, 0), chain.plugins[pi].path);
			chain_log(dbg);
		}

		release_processing_state(old_state);
		return true;
	}

	void clear_active_state(VstChainState& chain)
	{
		VstProcessingState* old_state;
		int i;

		AcquireSRWLockExclusive(&chain.state_lock);
		InterlockedIncrement(&chain.rebuild_generation);
		old_state = chain.active_state;
		chain.active_state = 0;
		chain.sample_rate = 0;
		chain.max_block_size = 0;
		chain.num_channels = 0;
		InterlockedExchange(&chain.created, 0);
		InterlockedExchange(&chain.ready, 0);
		InterlockedExchange(&chain.bypass, 0);
		InterlockedExchange(&chain.plugin_count, 0);
		InterlockedExchange64(&chain.gain_bits, double_to_bits(1.0));
		for (i = 0; i < VST_MAX_CHAIN_PLUGINS; ++i)
			clear_descriptor(chain.plugins[i]);
		ReleaseSRWLockExclusive(&chain.state_lock);

		release_processing_state(old_state);
	}

	VstProcessingState* detach_active_state_for_rebuild(VstChainState& chain)
	{
		VstProcessingState* old_state = chain.active_state;

		chain.active_state = 0;
		InterlockedExchange(&chain.ready, 0);
		return old_state;
	}

	void drain_detached_processing_state(VstProcessingState* state)
	{
		int spin_count;

		if (!state)
			return;

		for (spin_count = 0; spin_count < 4000; ++spin_count)
		{
			if (InterlockedCompareExchange(&state->ref_count, 0, 0) <= 1)
				return;
			Sleep(1);
		}

		// If we get here the audio thread is still referencing the old
		// state after 4 seconds.  Retain the state to avoid a
		// use-after-free; the memory will leak but the host won't crash.
		retain_processing_state(state);
	}

	int build_and_publish_requested_state(VstChainState& chain, LONG target_generation, const CapturedPluginState* captured_states)
	{
		VstPluginSlot descriptors[VST_MAX_CHAIN_PLUGINS] = {};
		VstPluginSlot resolved_descriptors[VST_MAX_CHAIN_PLUGINS] = {};
		VstProcessingState* new_state = 0;
		int plugin_count;
		int sample_rate;
		int max_block_size;
		int num_channels;
		int created;
		LONG snapshot_generation;
		int result;

		snapshot_descriptors(
			chain,
			descriptors,
			plugin_count,
			sample_rate,
			max_block_size,
			num_channels,
			created,
			snapshot_generation);

		if (snapshot_generation != target_generation)
			return 1;
		if (!created)
			return 0;

		result = build_processing_state(
			descriptors,
			plugin_count,
			sample_rate,
			max_block_size,
			num_channels,
			new_state,
			resolved_descriptors,
			captured_states);

		if (result != 0)
		{
			release_processing_state(new_state);
			return result;
		}

		apply_processing_state_dirty_callback(new_state, chain.state_dirty_callback, chain.state_dirty_context);

		if (!publish_processing_state(
			chain,
			resolved_descriptors,
			plugin_count,
			sample_rate,
			max_block_size,
			num_channels,
			created,
			snapshot_generation,
			new_state))
		{
			release_processing_state(new_state);
			return 1;
		}

		return 0;
	}

	int build_and_publish_requested_state(VstChainState& chain, LONG target_generation)
	{
		return build_and_publish_requested_state(chain, target_generation, 0);
	}

	void queue_rebuild(VstChainState& chain)
	{
		LONG generation = InterlockedIncrement(&chain.rebuild_generation);

		if (chain.rebuild_event && chain.worker_thread)
			SetEvent(chain.rebuild_event);
		else
			build_and_publish_requested_state(chain, generation);
	}

	int rebuild_now(VstChainState& chain)
	{
		LONG generation = InterlockedIncrement(&chain.rebuild_generation);
		return build_and_publish_requested_state(chain, generation);
	}

	int rebuild_now(VstChainState& chain, const CapturedPluginState* captured_states)
	{
		LONG generation = InterlockedIncrement(&chain.rebuild_generation);
		return build_and_publish_requested_state(chain, generation, captured_states);
	}

	unsigned __stdcall chain_worker_proc(void* context)
	{
		VstChainState* chain = static_cast<VstChainState*>(context);
		LONG last_completed_generation = 0;

		while (WaitForSingleObject(chain->rebuild_event, INFINITE) == WAIT_OBJECT_0)
		{
			LONG target_generation;

			if (InterlockedCompareExchange(&chain->stop_worker, 0, 0))
				break;

			for (;;)
			{
				int result;

				target_generation = InterlockedCompareExchange(&chain->rebuild_generation, 0, 0);
				if (target_generation == last_completed_generation)
					break;
				if (InterlockedCompareExchange(&chain->stop_worker, 0, 0))
					break;

				result = build_and_publish_requested_state(*chain, target_generation);
				if (result == 0)
					last_completed_generation = target_generation;
				else if (result < 0)
					break;
			}
		}

		return 0;
	}
}

void VstChain_Initialize(VstChainState& chain)
{
	uintptr_t worker_handle;
	int i;

	InitializeSRWLock(&chain.state_lock);
	InitializeCriticalSectionAndSpinCount(&chain.mutation_lock, 2500);
	chain.rebuild_event = CreateEvent(0, FALSE, FALSE, 0);
	chain.worker_thread = 0;
	InterlockedExchange(&chain.stop_worker, 0);
	InterlockedExchange(&chain.rebuild_generation, 0);
	chain.active_state = 0;
	chain.sample_rate = 0;
	chain.max_block_size = 0;
	chain.num_channels = 0;
	chain.state_dirty_callback = nullptr;
	chain.state_dirty_context = nullptr;
	InterlockedExchange(&chain.created, 0);
	InterlockedExchange(&chain.ready, 0);
	InterlockedExchange(&chain.bypass, 0);
	InterlockedExchange(&chain.plugin_count, 0);
	InterlockedExchange64(&chain.gain_bits, double_to_bits(1.0));
	for (i = 0; i < VST_MAX_CHAIN_PLUGINS; ++i)
		clear_descriptor(chain.plugins[i]);

	if (chain.rebuild_event)
	{
		worker_handle = _beginthreadex(0, 0, chain_worker_proc, &chain, 0, 0);
		if (worker_handle)
			chain.worker_thread = reinterpret_cast<HANDLE>(worker_handle);
	}
}

void VstChain_Terminate(VstChainState& chain)
{
	HANDLE worker_thread = chain.worker_thread;
	HANDLE rebuild_event = chain.rebuild_event;

	InterlockedExchange(&chain.stop_worker, 1);
	if (rebuild_event)
		SetEvent(rebuild_event);

	if (worker_thread)
	{
		WaitForSingleObject(worker_thread, INFINITE);
		CloseHandle(worker_thread);
		chain.worker_thread = 0;
	}

	if (rebuild_event)
	{
		CloseHandle(rebuild_event);
		chain.rebuild_event = 0;
	}

	clear_active_state(chain);
	DeleteCriticalSection(&chain.mutation_lock);

	shutdown_deferred_thread();
}

void VstChain_Reset(VstChainState& chain)
{
	EnterCriticalSection(&chain.mutation_lock);
	close_active_chain_editors(chain);
	clear_active_state(chain);
	LeaveCriticalSection(&chain.mutation_lock);
}

int VstChain_Create(VstChainState& chain, int sample_rate, int max_block_size, int num_channels)
{
	CapturedPluginState captured_states[VST_MAX_CHAIN_PLUGINS];
	int state_map[VST_MAX_CHAIN_PLUGINS];
	int plugin_count;
	VstProcessingState* old_state;
	int result;

	{
		wchar_t dbg[256];
		swprintf_s(dbg, L"[chain] VstChain_Create sr=%d bs=%d ch=%d\r\n", sample_rate, max_block_size, num_channels);
		chain_log(dbg);
	}

	if (sample_rate <= 0 || max_block_size <= 0 || num_channels <= 0)
		return -1;

	EnterCriticalSection(&chain.mutation_lock);
	close_active_chain_editors(chain);
	AcquireSRWLockExclusive(&chain.state_lock);
	old_state = detach_active_state_for_rebuild(chain);
	chain.sample_rate = sample_rate;
	chain.max_block_size = max_block_size;
	chain.num_channels = num_channels;
	InterlockedExchange(&chain.created, 1);
	plugin_count = InterlockedCompareExchange(&chain.plugin_count, 0, 0);
	mark_descriptors_pending(chain.plugins, plugin_count);
	ReleaseSRWLockExclusive(&chain.state_lock);
	drain_detached_processing_state(old_state);
	for (int i = 0; i < plugin_count; ++i)
		state_map[i] = i;
	capture_processing_states_with_map(old_state, state_map, plugin_count, captured_states);
	release_processing_state(old_state);
	result = rebuild_now(chain, captured_states);
	clear_captured_states(captured_states, plugin_count);
	LeaveCriticalSection(&chain.mutation_lock);
	return result < 0 ? result : 0;
}

void VstChain_Destroy(VstChainState& chain)
{
	VstChain_Reset(chain);
}

void VstChain_SetBypass(VstChainState& chain, int bypass)
{
	InterlockedExchange(&chain.bypass, bypass ? 1 : 0);
	if (chain.state_dirty_callback)
		chain.state_dirty_callback(chain.state_dirty_context);
}

int VstChain_GetBypass(const VstChainState& chain)
{
	return InterlockedCompareExchange(const_cast<LONG*>(&chain.bypass), 0, 0) ? 1 : 0;
}

int VstChain_GetReady(const VstChainState& chain)
{
	return InterlockedCompareExchange(const_cast<LONG*>(&chain.ready), 0, 0) ? 1 : 0;
}

void VstChain_SetGain(VstChainState& chain, double gain)
{
	if (!std::isfinite(gain))
		gain = 1.0;

	InterlockedExchange64(&chain.gain_bits, double_to_bits(gain));
}

double VstChain_GetGain(const VstChainState& chain)
{
	double gain = bits_to_double(InterlockedCompareExchange64(const_cast<LONG64*>(&chain.gain_bits), 0, 0));
	return std::isfinite(gain) ? gain : 1.0;
}

int VstChain_ClearPlugins(VstChainState& chain)
{
	int i;
	int created;
	VstProcessingState* old_state;
	int result = 0;

	EnterCriticalSection(&chain.mutation_lock);
	close_active_chain_editors(chain);
	AcquireSRWLockExclusive(&chain.state_lock);
	old_state = detach_active_state_for_rebuild(chain);
	created = InterlockedCompareExchange(&chain.created, 0, 0);
	for (i = 0; i < VST_MAX_CHAIN_PLUGINS; ++i)
		clear_descriptor(chain.plugins[i]);
	InterlockedExchange(&chain.plugin_count, 0);
	ReleaseSRWLockExclusive(&chain.state_lock);
	drain_detached_processing_state(old_state);
	release_processing_state(old_state);
	if (created)
		result = rebuild_now(chain);
	LeaveCriticalSection(&chain.mutation_lock);
	return result < 0 ? result : 0;
}

int VstChain_AddPlugin(VstChainState& chain, const wchar_t* plugin_path, const wchar_t* plugin_cid)
{
	int state_map[VST_MAX_CHAIN_PLUGINS];
	wchar_t normalized_path[VST_MAX_PLUGIN_PATH_CHARS];
	wchar_t normalized_cid[VST_MAX_PLUGIN_CID_CHARS] = {};
	VstPluginSlot resolved_descriptors[VST_MAX_CHAIN_PLUGINS] = {};
	int plugin_count;
	int created;
	VstProcessingState* old_state;
	VstProcessingState* new_state = 0;
	int result = 0;

	if (!normalize_plugin_path(plugin_path, normalized_path, VST_MAX_PLUGIN_PATH_CHARS))
		return -3;
	if (plugin_cid && plugin_cid[0])
		wcsncpy_s(normalized_cid, VST_MAX_PLUGIN_CID_CHARS, plugin_cid, _TRUNCATE);

	EnterCriticalSection(&chain.mutation_lock);
	close_active_chain_editors(chain);
	AcquireSRWLockExclusive(&chain.state_lock);
	plugin_count = InterlockedCompareExchange(&chain.plugin_count, 0, 0);
	if (plugin_count >= VST_MAX_CHAIN_PLUGINS)
	{
		ReleaseSRWLockExclusive(&chain.state_lock);
		LeaveCriticalSection(&chain.mutation_lock);
		return -2;
	}

	created = InterlockedCompareExchange(&chain.created, 0, 0);
	old_state = created ? chain.active_state : 0;
	if (old_state && old_state->plugin_count <= 0)
	{
		chain_log(L"[chain] AddPlugin old_state has 0 plugins, forcing rebuild_now path\r\n");
		old_state = 0;
	}
	retain_processing_state(old_state);
	{
		wchar_t dbg[512];
		swprintf_s(dbg, L"[chain] AddPlugin created=%d old_state=%p old_pc=%d plugin_count=%d path=\"%s\"\r\n",
			created, old_state, old_state ? old_state->plugin_count : 0, plugin_count, normalized_path);
		chain_log(dbg);
	}
	clear_descriptor(chain.plugins[plugin_count]);
	InterlockedExchange(&chain.plugins[plugin_count].enabled, 1);
	InterlockedExchange(&chain.plugins[plugin_count].bypass, 0);
	InterlockedExchange(&chain.plugins[plugin_count].load_state, VST_PLUGIN_LOAD_DESCRIPTOR_ONLY);
	wcsncpy_s(chain.plugins[plugin_count].path, VST_MAX_PLUGIN_PATH_CHARS, normalized_path, _TRUNCATE);
	InterlockedExchange(&chain.plugins[plugin_count].format, detect_plugin_format_from_path(chain.plugins[plugin_count].path));
	derive_plugin_name(chain.plugins[plugin_count].path, chain.plugins[plugin_count].name, VST_MAX_PLUGIN_NAME_CHARS);
	if (normalized_cid[0])
		wcsncpy_s(chain.plugins[plugin_count].cid, VST_MAX_PLUGIN_CID_CHARS, normalized_cid, _TRUNCATE);
	InterlockedExchange(&chain.plugin_count, plugin_count + 1);
	if (created)
		mark_descriptors_pending(chain.plugins, plugin_count + 1);
	ReleaseSRWLockExclusive(&chain.state_lock);

	if (created)
	{
		for (int i = 0; i < plugin_count; ++i)
			state_map[i] = i;
		state_map[plugin_count] = -1;

		result = build_mapped_processing_state(
			old_state,
			chain.plugins,
			plugin_count + 1,
			state_map,
			new_state,
			resolved_descriptors,
			0);

		if (result == 0)
		{
			VstProcessingState* published_old_state;

			apply_processing_state_dirty_callback(new_state, chain.state_dirty_callback, chain.state_dirty_context);
			AcquireSRWLockExclusive(&chain.state_lock);
			published_old_state = chain.active_state;
			chain.active_state = new_state;
			InterlockedExchange(&chain.ready, 1);
			copy_descriptor_array(chain.plugins, resolved_descriptors, plugin_count + 1);
			ReleaseSRWLockExclusive(&chain.state_lock);
			release_processing_state(published_old_state);
			new_state = 0;
			{
				wchar_t dbg[256];
				swprintf_s(dbg, L"[chain] AddPlugin build_mapped OK plugin_count=%d\r\n", plugin_count + 1);
				chain_log(dbg);
			}
		}
		else
		{
			wchar_t dbg[256];
			swprintf_s(dbg, L"[chain] AddPlugin build_mapped FAILED result=%d, rebuild_now\r\n", result);
			chain_log(dbg);
			result = rebuild_now(chain);
			swprintf_s(dbg, L"[chain] AddPlugin rebuild_now result=%d\r\n", result);
			chain_log(dbg);
		}
	}
	else
	{
		wchar_t dbg[256];
		swprintf_s(dbg, L"[chain] AddPlugin NOT created, returning descriptor index=%d\r\n", plugin_count);
		chain_log(dbg);
	}
	release_processing_state(new_state);
	release_processing_state(old_state);
	LeaveCriticalSection(&chain.mutation_lock);
	{
		int final_load = -999;
		int final_count = -1;
		if (plugin_count >= 0 && plugin_count < VST_MAX_CHAIN_PLUGINS)
		{
			AcquireSRWLockShared(const_cast<SRWLOCK*>(&chain.state_lock));
			final_count = InterlockedCompareExchange(const_cast<LONG*>(&chain.plugin_count), 0, 0);
			if (plugin_count < final_count)
				final_load = InterlockedCompareExchange(const_cast<LONG*>(&chain.plugins[plugin_count].load_state), 0, 0);
			ReleaseSRWLockShared(const_cast<SRWLOCK*>(&chain.state_lock));
		}
		wchar_t dbg[256];
		swprintf_s(dbg, L"[chain] AddPlugin RETURN idx=%d result=%d final_load=%d final_count=%d\r\n",
			plugin_count, result < 0 ? result : plugin_count, final_load, final_count);
		chain_log(dbg);
	}
	if (result < 0)
		return result;
	return plugin_count;
}

int VstChain_RemovePlugin(VstChainState& chain, int index)
{
	int state_map[VST_MAX_CHAIN_PLUGINS];
	VstPluginSlot resolved_descriptors[VST_MAX_CHAIN_PLUGINS] = {};
	int plugin_count;
	int created;
	int i;
	VstProcessingState* old_state;
	VstProcessingState* new_state = 0;
	int result = 0;
	DWORD t0 = GetTickCount();
	{
		wchar_t dbg[256];
		swprintf_s(dbg, L"[chain] RemovePlugin ENTER index=%d\r\n", index);
		chain_log(dbg);
	}

	EnterCriticalSection(&chain.mutation_lock);
	{
		wchar_t dbg[256];
		swprintf_s(dbg, L"[chain] RemovePlugin got mutation_lock (%lu ms)\r\n", GetTickCount() - t0);
		chain_log(dbg);
	}
	close_active_chain_editors(chain);
	{
		wchar_t dbg[256];
		swprintf_s(dbg, L"[chain] RemovePlugin editors closed (%lu ms)\r\n", GetTickCount() - t0);
		chain_log(dbg);
	}
	AcquireSRWLockExclusive(&chain.state_lock);
	plugin_count = InterlockedCompareExchange(&chain.plugin_count, 0, 0);
	if (index < 0 || index >= plugin_count)
	{
		ReleaseSRWLockExclusive(&chain.state_lock);
		LeaveCriticalSection(&chain.mutation_lock);
		chain_log(L"[chain] RemovePlugin BAD INDEX\r\n");
		return -1;
	}

	created = InterlockedCompareExchange(&chain.created, 0, 0);
	old_state = created ? chain.active_state : 0;
	retain_processing_state(old_state);
	for (i = index; i < plugin_count - 1; ++i)
		copy_descriptor(chain.plugins[i], chain.plugins[i + 1]);
	clear_descriptor(chain.plugins[plugin_count - 1]);
	InterlockedExchange(&chain.plugin_count, plugin_count - 1);
	if (created)
		mark_descriptors_pending(chain.plugins, plugin_count - 1);
	ReleaseSRWLockExclusive(&chain.state_lock);
	{
		wchar_t dbg[256];
		swprintf_s(dbg, L"[chain] RemovePlugin descriptors shifted created=%d old_pc=%d new_pc=%d (%lu ms)\r\n",
			created, plugin_count, plugin_count - 1, GetTickCount() - t0);
		chain_log(dbg);
	}

	if (created)
	{
		for (i = 0; i < plugin_count - 1; ++i)
			state_map[i] = (i < index) ? i : (i + 1);

		result = build_mapped_processing_state(
			old_state,
			chain.plugins,
			plugin_count - 1,
			state_map,
			new_state,
			resolved_descriptors,
			0);

		{
			wchar_t dbg[256];
			swprintf_s(dbg, L"[chain] RemovePlugin build_mapped result=%d new_pc=%d (%lu ms)\r\n",
				result, plugin_count - 1, GetTickCount() - t0);
			chain_log(dbg);
		}

		if (result == 0)
		{
			VstProcessingState* published_old_state;

			apply_processing_state_dirty_callback(new_state, chain.state_dirty_callback, chain.state_dirty_context);
			AcquireSRWLockExclusive(&chain.state_lock);
			published_old_state = chain.active_state;
			chain.active_state = new_state;
			InterlockedExchange(&chain.ready, 1);
			copy_descriptor_array(chain.plugins, resolved_descriptors, plugin_count - 1);
			ReleaseSRWLockExclusive(&chain.state_lock);
			{
				wchar_t dbg[256];
				swprintf_s(dbg, L"[chain] RemovePlugin published new state, releasing published_old %p (%lu ms)\r\n",
					reinterpret_cast<void*>(published_old_state), GetTickCount() - t0);
				chain_log(dbg);
			}
			release_processing_state(published_old_state);
			{
				wchar_t dbg[256];
				swprintf_s(dbg, L"[chain] RemovePlugin published_old released (%lu ms)\r\n", GetTickCount() - t0);
				chain_log(dbg);
			}
			new_state = 0;
		}
		else
		{
			result = rebuild_now(chain);
		}
	}
	release_processing_state(new_state);
	{
		wchar_t dbg[256];
		swprintf_s(dbg, L"[chain] RemovePlugin new_state released, releasing old_state %p (%lu ms)\r\n",
			reinterpret_cast<void*>(old_state), GetTickCount() - t0);
		chain_log(dbg);
	}
	release_processing_state(old_state);
	{
		wchar_t dbg[256];
		swprintf_s(dbg, L"[chain] RemovePlugin old_state released, leaving mutation_lock (%lu ms)\r\n",
			GetTickCount() - t0);
		chain_log(dbg);
	}
	LeaveCriticalSection(&chain.mutation_lock);
	{
		wchar_t dbg[256];
		swprintf_s(dbg, L"[chain] RemovePlugin EXIT result=%d total=%lu ms\r\n",
			result < 0 ? result : 0, GetTickCount() - t0);
		chain_log(dbg);
	}
	return result < 0 ? result : 0;
}

int VstChain_MovePlugin(VstChainState& chain, int from_index, int to_index)
{
	CapturedPluginState captured_states[VST_MAX_CHAIN_PLUGINS];
	int state_map[VST_MAX_CHAIN_PLUGINS];
	VstPluginSlot temp = {};
	VstPluginSlot resolved_descriptors[VST_MAX_CHAIN_PLUGINS] = {};
	int plugin_count;
	int created;
	int i;
	VstProcessingState* old_state;
	VstProcessingState* new_state = 0;
	int result = 0;

	EnterCriticalSection(&chain.mutation_lock);
	close_active_chain_editors(chain);
	AcquireSRWLockExclusive(&chain.state_lock);
	plugin_count = InterlockedCompareExchange(&chain.plugin_count, 0, 0);
	if (from_index < 0 || from_index >= plugin_count || to_index < 0 || to_index >= plugin_count)
	{
		ReleaseSRWLockExclusive(&chain.state_lock);
		LeaveCriticalSection(&chain.mutation_lock);
		return -1;
	}

	if (from_index == to_index)
	{
		ReleaseSRWLockExclusive(&chain.state_lock);
		LeaveCriticalSection(&chain.mutation_lock);
		return 0;
	}

	created = InterlockedCompareExchange(&chain.created, 0, 0);
	old_state = created ? chain.active_state : 0;
	retain_processing_state(old_state);
	copy_descriptor(temp, chain.plugins[from_index]);
	if (from_index < to_index)
	{
		for (i = from_index; i < to_index; ++i)
			copy_descriptor(chain.plugins[i], chain.plugins[i + 1]);
	}
	else
	{
		for (i = from_index; i > to_index; --i)
			copy_descriptor(chain.plugins[i], chain.plugins[i - 1]);
	}
	copy_descriptor(chain.plugins[to_index], temp);
	if (created)
		mark_descriptors_pending(chain.plugins, plugin_count);
	ReleaseSRWLockExclusive(&chain.state_lock);

	if (created)
	{
		result = build_reordered_processing_state(
			old_state,
			chain.plugins,
			plugin_count,
			from_index,
			to_index,
			new_state,
			resolved_descriptors);

		if (result == 0)
		{
			VstProcessingState* published_old_state;

			apply_processing_state_dirty_callback(new_state, chain.state_dirty_callback, chain.state_dirty_context);
			AcquireSRWLockExclusive(&chain.state_lock);
			published_old_state = chain.active_state;
			chain.active_state = new_state;
			InterlockedExchange(&chain.ready, 1);
			copy_descriptor_array(chain.plugins, resolved_descriptors, plugin_count);
			ReleaseSRWLockExclusive(&chain.state_lock);
			release_processing_state(published_old_state);
			new_state = 0;
		}
		else
		{
			for (i = 0; i < plugin_count; ++i)
			{
				state_map[i] = i;
				if (from_index < to_index)
				{
					if (i >= from_index && i <= to_index)
						state_map[i] = (i == to_index) ? from_index : (i + 1);
				}
				else if (from_index > to_index)
				{
					if (i >= to_index && i <= from_index)
						state_map[i] = (i == to_index) ? from_index : (i - 1);
				}
			}
			capture_processing_states_with_map(old_state, state_map, plugin_count, captured_states);
			result = rebuild_now(chain, captured_states);
		}
	}
	clear_captured_states(captured_states, plugin_count);
	release_processing_state(new_state);
	release_processing_state(old_state);
	LeaveCriticalSection(&chain.mutation_lock);
	return result < 0 ? result : 0;
}

void VstChain_GetSnapshotInfo(
	const VstChainState& chain,
	int& created,
	int& sample_rate,
	int& max_block_size,
	int& num_channels,
	int& ready,
	int& bypass,
	double& gain,
	int& plugin_count)
{
	AcquireSRWLockShared(const_cast<SRWLOCK*>(&chain.state_lock));
	created = InterlockedCompareExchange(const_cast<LONG*>(&chain.created), 0, 0);
	sample_rate = chain.sample_rate;
	max_block_size = chain.max_block_size;
	num_channels = chain.num_channels;
	ready = InterlockedCompareExchange(const_cast<LONG*>(&chain.ready), 0, 0);
	bypass = InterlockedCompareExchange(const_cast<LONG*>(&chain.bypass), 0, 0);
	gain = VstChain_GetGain(chain);
	plugin_count = InterlockedCompareExchange(const_cast<LONG*>(&chain.plugin_count), 0, 0);
	ReleaseSRWLockShared(const_cast<SRWLOCK*>(&chain.state_lock));
}

int VstChain_GetPluginCount(const VstChainState& chain)
{
	int plugin_count;

	AcquireSRWLockShared(const_cast<SRWLOCK*>(&chain.state_lock));
	plugin_count = InterlockedCompareExchange(const_cast<LONG*>(&chain.plugin_count), 0, 0);
	ReleaseSRWLockShared(const_cast<SRWLOCK*>(&chain.state_lock));
	return plugin_count;
}

int VstChain_GetPluginInfo(const VstChainState& chain, int index, VstPluginInfo* info)
{
	const VstPluginSlot* descriptor;
	int plugin_count;

	if (!info)
		return -2;

	AcquireSRWLockShared(const_cast<SRWLOCK*>(&chain.state_lock));
	plugin_count = InterlockedCompareExchange(const_cast<LONG*>(&chain.plugin_count), 0, 0);
	if (index < 0 || index >= plugin_count)
	{
		ReleaseSRWLockShared(const_cast<SRWLOCK*>(&chain.state_lock));
		return -1;
	}

	descriptor = &chain.plugins[index];
	info->index = index;
	info->enabled = InterlockedCompareExchange(const_cast<LONG*>(&descriptor->enabled), 0, 0) ? 1 : 0;
	info->bypass = InterlockedCompareExchange(const_cast<LONG*>(&descriptor->bypass), 0, 0) ? 1 : 0;
	info->load_state = InterlockedCompareExchange(const_cast<LONG*>(&descriptor->load_state), 0, 0);
	info->format = InterlockedCompareExchange(const_cast<LONG*>(&descriptor->format), 0, 0);
	wcsncpy_s(info->path, VST_MAX_PLUGIN_PATH_CHARS, descriptor->path, _TRUNCATE);
	wcsncpy_s(info->name, VST_MAX_PLUGIN_NAME_CHARS, descriptor->name, _TRUNCATE);
	if (descriptor->cid[0])
		wcsncpy_s(info->cid, VST_MAX_PLUGIN_CID_CHARS, descriptor->cid, _TRUNCATE);
	ReleaseSRWLockShared(const_cast<SRWLOCK*>(&chain.state_lock));
	return 0;
}

int VstChain_SetPluginBypass(VstChainState& chain, int index, int bypass)
{
	VstProcessingState* active_state;
	int plugin_count;

	EnterCriticalSection(&chain.mutation_lock);
	close_active_chain_editors(chain);
	AcquireSRWLockExclusive(&chain.state_lock);
	plugin_count = InterlockedCompareExchange(&chain.plugin_count, 0, 0);
	if (index < 0 || index >= plugin_count)
	{
		ReleaseSRWLockExclusive(&chain.state_lock);
		LeaveCriticalSection(&chain.mutation_lock);
		return -1;
	}

	InterlockedExchange(&chain.plugins[index].bypass, bypass ? 1 : 0);
	active_state = chain.active_state;
	if (active_state && index < active_state->plugin_count)
		InterlockedExchange(&active_state->plugins[index].bypass, bypass ? 1 : 0);
	ReleaseSRWLockExclusive(&chain.state_lock);
	LeaveCriticalSection(&chain.mutation_lock);
	if (chain.state_dirty_callback)
		chain.state_dirty_callback(chain.state_dirty_context);
	return 0;
}

int VstChain_SetPluginEnabled(VstChainState& chain, int index, int enabled)
{
	VstProcessingState* active_state;
	int plugin_count;

	EnterCriticalSection(&chain.mutation_lock);
	close_active_chain_editors(chain);
	AcquireSRWLockExclusive(&chain.state_lock);
	plugin_count = InterlockedCompareExchange(&chain.plugin_count, 0, 0);
	if (index < 0 || index >= plugin_count)
	{
		ReleaseSRWLockExclusive(&chain.state_lock);
		LeaveCriticalSection(&chain.mutation_lock);
		return -1;
	}

	InterlockedExchange(&chain.plugins[index].enabled, enabled ? 1 : 0);
	active_state = chain.active_state;
	if (active_state && index < active_state->plugin_count)
		InterlockedExchange(&active_state->plugins[index].enabled, enabled ? 1 : 0);
	ReleaseSRWLockExclusive(&chain.state_lock);
	LeaveCriticalSection(&chain.mutation_lock);
	if (chain.state_dirty_callback)
		chain.state_dirty_callback(chain.state_dirty_context);
	return 0;
}

int VstChain_GetPluginStateSize(VstChainState& chain, int index)
{
	VstProcessingState* active_state;
	VstProcessingPlugin* plugin;
	int result;

	AcquireSRWLockShared(&chain.state_lock);
	active_state = chain.active_state;
	retain_processing_state(active_state);
	ReleaseSRWLockShared(&chain.state_lock);

	if (!active_state)
	{
		release_processing_state(active_state);
		return -2;
	}
	if (index < 0 || index >= active_state->plugin_count)
	{
		release_processing_state(active_state);
		return -3;
	}

	plugin = &active_state->plugins[index];
	if (!plugin->runtime || InterlockedCompareExchange(&plugin->load_state, 0, 0) != VST_PLUGIN_LOAD_ACTIVE)
	{
		release_processing_state(active_state);
		return -4;
	}

	result = ThetisPluginRuntime_GetStateSize(plugin->runtime);
	release_processing_state(active_state);
	return result;
}

int VstChain_GetPluginState(VstChainState& chain, int index, void* buffer, int buffer_size, int* bytes_written)
{
	VstProcessingState* active_state;
	VstProcessingPlugin* plugin;
	int result;

	if (bytes_written)
		*bytes_written = 0;

	AcquireSRWLockShared(&chain.state_lock);
	active_state = chain.active_state;
	retain_processing_state(active_state);
	ReleaseSRWLockShared(&chain.state_lock);

	if (!active_state)
	{
		release_processing_state(active_state);
		return -2;
	}
	if (index < 0 || index >= active_state->plugin_count)
	{
		release_processing_state(active_state);
		return -3;
	}

	plugin = &active_state->plugins[index];
	if (!plugin->runtime || InterlockedCompareExchange(&plugin->load_state, 0, 0) != VST_PLUGIN_LOAD_ACTIVE)
	{
		release_processing_state(active_state);
		return -4;
	}

	result = ThetisPluginRuntime_GetState(plugin->runtime, buffer, buffer_size, bytes_written);
	release_processing_state(active_state);
	return result;
}

int VstChain_SetPluginState(VstChainState& chain, int index, const void* buffer, int buffer_size)
{
	VstProcessingState* active_state;
	VstProcessingPlugin* plugin;
	int result;

	if (!buffer || buffer_size <= 0)
		return -1;

	EnterCriticalSection(&chain.mutation_lock);
	AcquireSRWLockShared(&chain.state_lock);
	active_state = chain.active_state;
	retain_processing_state(active_state);
	ReleaseSRWLockShared(&chain.state_lock);

	if (!active_state)
	{
		LeaveCriticalSection(&chain.mutation_lock);
		release_processing_state(active_state);
		return -2;
	}
	if (index < 0 || index >= active_state->plugin_count)
	{
		LeaveCriticalSection(&chain.mutation_lock);
		release_processing_state(active_state);
		return -3;
	}

	plugin = &active_state->plugins[index];
	if (!plugin->runtime || InterlockedCompareExchange(&plugin->load_state, 0, 0) != VST_PLUGIN_LOAD_ACTIVE)
	{
		LeaveCriticalSection(&chain.mutation_lock);
		release_processing_state(active_state);
		return -4;
	}

	result = ThetisPluginRuntime_SetState(plugin->runtime, buffer, buffer_size);
	LeaveCriticalSection(&chain.mutation_lock);
	release_processing_state(active_state);
	return result;
}

void VstChain_SetStateDirtyCallback(VstChainState& chain, VstChainStateDirtyCallback callback, void* context)
{
	VstProcessingState* active_state;

	EnterCriticalSection(&chain.mutation_lock);
	AcquireSRWLockExclusive(&chain.state_lock);
	chain.state_dirty_callback = callback;
	chain.state_dirty_context = context;
	active_state = chain.active_state;
	retain_processing_state(active_state);
	ReleaseSRWLockExclusive(&chain.state_lock);
	LeaveCriticalSection(&chain.mutation_lock);

	apply_processing_state_dirty_callback(active_state, callback, context);
	release_processing_state(active_state);
}

int VstChain_ProcessInterleavedDouble(VstChainState& chain, double* buffer, int frames)
{
	VstProcessingState* active_state;
	int created;
	int ready;
	int bypass;
	double gain;
	int plugin_index;
	int process_result;
	int sample_count;
	int i;

	if (!buffer || frames <= 0)
		return -1;

	AcquireSRWLockShared(&chain.state_lock);
	created = InterlockedCompareExchange(&chain.created, 0, 0);
	ready = InterlockedCompareExchange(&chain.ready, 0, 0);
	bypass = InterlockedCompareExchange(&chain.bypass, 0, 0);
	active_state = chain.active_state;
	retain_processing_state(active_state);
	ReleaseSRWLockShared(&chain.state_lock);

	if (!created || !ready || !active_state)
	{
		release_processing_state(active_state);
		return 0;
	}

	if (bypass)
	{
		release_processing_state(active_state);
		return 0;
	}

	if (frames > active_state->max_block_size)
	{
		release_processing_state(active_state);
		return -2;
	}

	for (plugin_index = 0; plugin_index < active_state->plugin_count; ++plugin_index)
	{
		VstProcessingPlugin& plugin = active_state->plugins[plugin_index];

		if (!plugin.runtime)
			continue;
		if (!InterlockedCompareExchange(&plugin.enabled, 0, 0))
			continue;
		if (InterlockedCompareExchange(&plugin.bypass, 0, 0))
			continue;
		if (InterlockedCompareExchange(&plugin.load_state, 0, 0) != VST_PLUGIN_LOAD_ACTIVE)
			continue;

		process_result = ThetisPluginRuntime_Process(plugin.runtime, buffer, frames, active_state->num_channels);
		if (process_result != 0)
		{
			InterlockedExchange(&plugin.load_state, VST_PLUGIN_LOAD_FAILED);
			if (plugin_index < VST_MAX_CHAIN_PLUGINS)
				InterlockedExchange(&chain.plugins[plugin_index].load_state, VST_PLUGIN_LOAD_FAILED);
			release_processing_state(active_state);
			return process_result;
		}
	}

	gain = VstChain_GetGain(chain);
	if (gain != 1.0)
	{
		sample_count = frames * active_state->num_channels;
		for (i = 0; i < sample_count; ++i)
			buffer[i] *= gain;
	}

	release_processing_state(active_state);
	return 0;
}

int VstChain_OpenPluginEditor(VstChainState& chain, int index, HWND parent_window, int& width, int& height, int& can_resize, VstEditorSession*& session)
{
	VstProcessingState* active_state;
	VstProcessingPlugin* plugin;
	int created;
	int ready;
	int result;

	session = 0;
	width = 0;
	height = 0;
	can_resize = 0;

	AcquireSRWLockShared(&chain.state_lock);
	created = InterlockedCompareExchange(&chain.created, 0, 0);
	ready = InterlockedCompareExchange(&chain.ready, 0, 0);
	active_state = chain.active_state;
	retain_processing_state(active_state);
	ReleaseSRWLockShared(&chain.state_lock);

	if (!created || !ready || !active_state)
	{
		release_processing_state(active_state);
		return -2;
	}

	if (index < 0 || index >= active_state->plugin_count)
	{
		release_processing_state(active_state);
		return -3;
	}

	plugin = &active_state->plugins[index];
	if (!plugin->runtime || InterlockedCompareExchange(&plugin->load_state, 0, 0) != VST_PLUGIN_LOAD_ACTIVE)
	{
		release_processing_state(active_state);
		return -4;
	}

	result = ThetisPluginRuntime_OpenEditor(plugin->runtime, active_state, index, parent_window, width, height, can_resize, session);
	if (result != 0)
	{
		release_processing_state(active_state);
		return result;
	}

	return 0;
}

int VstChain_ClosePluginEditor(VstEditorSession*& session)
{
	if (!session)
		return 0;

	return ThetisPluginRuntime_CloseEditor(session);
}

int VstChain_ResizePluginEditor(VstEditorSession* session, int width, int height)
{
	return ThetisPluginRuntime_ResizeEditor(session, width, height);
}
