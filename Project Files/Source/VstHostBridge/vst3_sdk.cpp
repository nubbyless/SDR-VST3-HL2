/*  vst3_sdk.cpp

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

#include "vst3_sdk.h"

#include "pluginterfaces/vst/ivsthostapplication.h"
#include "pluginterfaces/vst/vsttypes.h"
#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/module.h"

#define VST3SDK_WIDEN_IMPL(value) L##value
#define VST3SDK_WIDEN(value) VST3SDK_WIDEN_IMPL(value)

namespace
{
	const wchar_t* const kVst3SdkVersion = VST3SDK_WIDEN(kVstVersionString);
}

int Vst3Sdk_IsAvailable(void)
{
	return VST_VERSION > 0 ? 1 : 0;
}

const wchar_t* Vst3Sdk_GetVersionString(void)
{
	return kVst3SdkVersion;
}
