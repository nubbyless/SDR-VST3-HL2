/*  vst_scoped_lock.h

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

#pragma once

#include <windows.h>

// Scoped RAII lock for a runtime's api_lock CRITICAL_SECTION.
// Works with any runtime type that has a public api_lock member.
template<typename RuntimeT>
struct ScopedRuntimeApiLock
{
	explicit ScopedRuntimeApiLock(RuntimeT* target_runtime)
		: runtime(target_runtime)
	{
		if (runtime)
			EnterCriticalSection(&runtime->api_lock);
	}

	~ScopedRuntimeApiLock()
	{
		if (runtime)
			LeaveCriticalSection(&runtime->api_lock);
	}

	RuntimeT* runtime;
};

// Non-blocking try-lock variant for the audio thread.
template<typename RuntimeT>
struct ScopedTryRuntimeApiLock
{
	explicit ScopedTryRuntimeApiLock(RuntimeT* target_runtime)
		: runtime(target_runtime)
		, locked(FALSE)
	{
		if (runtime)
			locked = TryEnterCriticalSection(&runtime->api_lock);
	}

	~ScopedTryRuntimeApiLock()
	{
		if (locked && runtime)
			LeaveCriticalSection(&runtime->api_lock);
	}

	bool is_locked() const
	{
		return locked != FALSE;
	}

	RuntimeT* runtime;
	BOOL locked;
};
