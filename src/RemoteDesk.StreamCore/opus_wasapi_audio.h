#pragma once

#include "Limelight.h"

int RdAudioInit(int audioConfiguration, const POPUS_MULTISTREAM_CONFIGURATION opusConfig);
void RdAudioStart();
void RdAudioStop();
void RdAudioCleanup();
void RdAudioDecodeAndPlay(char* sampleData, int sampleLength);
void RdAudioSetMuted(int muted);
