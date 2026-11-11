// Optional, read-only metadata ABI for the local control bridge. SPDX-License-Identifier: MIT
#pragma once
#include <stdint.h>
struct VDInputMetadataV1 {
    uint32_t version, fps_num, fps_den, width, height;
    int64_t source_frames;
    char codec[32];
    wchar_t resolved_path[32768];
};
typedef int (__cdecl *VDReadInputMetadataV1)(const wchar_t*, VDInputMetadataV1*, uint32_t);
