#pragma once

#include <stdint.h>
#include <windows.h>

#include "Limelight.h"
#include "stream_core_api.h"

uint32_t RdQuerySupportedVideoCodecs();

int RdVideoSetup(int videoFormat, int width, int height, int redrawRate, HWND hwnd, int viewportW, int viewportH, int hardwareDecode);
void RdVideoStart();
void RdVideoStop();
void RdVideoCleanup();
int RdVideoSubmitDecodeUnit(PDECODE_UNIT decodeUnit);
void RdVideoResize(int width, int height);
void RdVideoGetSnapshot(RdStreamStats* outStats);
