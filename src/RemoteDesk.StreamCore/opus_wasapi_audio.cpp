#include "opus_wasapi_audio.h"

#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <wrl/client.h>

#include <opus_multistream.h>

#include <algorithm>
#include <atomic>
#include <cstring>
#include <mutex>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace {

struct AudioState {
    std::mutex mutex;
    OpusMSDecoder* decoder = nullptr;
    int channels = 2;
    int sampleRate = 48000;
    int samplesPerFrame = 240;
    bool comInited = false;
    bool started = false;
    std::atomic<bool> muted{ false };

    ComPtr<IMMDeviceEnumerator> enumerator;
    ComPtr<IMMDevice> device;
    ComPtr<IAudioClient> audioClient;
    ComPtr<IAudioRenderClient> renderClient;
    UINT32 bufferFrameCount = 0;
    std::vector<float> pcmBuffer;
};

AudioState g_audio;

} // namespace

int RdAudioInit(int /*audioConfiguration*/, const POPUS_MULTISTREAM_CONFIGURATION opusConfig) {
    if (!opusConfig || opusConfig->channelCount <= 0 || opusConfig->channelCount > AUDIO_CONFIGURATION_MAX_CHANNEL_COUNT) {
        return -1;
    }

    std::lock_guard<std::mutex> lock(g_audio.mutex);

    int opusErr = 0;
    g_audio.decoder = opus_multistream_decoder_create(
        opusConfig->sampleRate,
        opusConfig->channelCount,
        opusConfig->streams,
        opusConfig->coupledStreams,
        opusConfig->mapping,
        &opusErr);
    if (!g_audio.decoder || opusErr != OPUS_OK) {
        return -2;
    }

    g_audio.channels = opusConfig->channelCount;
    g_audio.sampleRate = opusConfig->sampleRate;
    g_audio.samplesPerFrame = opusConfig->samplesPerFrame > 0 ? opusConfig->samplesPerFrame : 240;
    g_audio.pcmBuffer.resize(static_cast<size_t>(g_audio.samplesPerFrame) * g_audio.channels * 2);

    HRESULT comHr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    g_audio.comInited = SUCCEEDED(comHr);

    if (FAILED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&g_audio.enumerator)))) {
        return 0; // Allow headless audio systems without audio output device to keep video streaming.
    }

    if (FAILED(g_audio.enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &g_audio.device))) {
        return 0;
    }

    if (FAILED(g_audio.device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(g_audio.audioClient.GetAddressOf())))) {
        return 0;
    }

    WAVEFORMATEXTENSIBLE wfx{};
    wfx.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
    wfx.Format.nChannels = static_cast<WORD>(g_audio.channels);
    wfx.Format.nSamplesPerSec = static_cast<DWORD>(g_audio.sampleRate);
    wfx.Format.wBitsPerSample = 32;
    wfx.Format.nBlockAlign = static_cast<WORD>(wfx.Format.nChannels * (wfx.Format.wBitsPerSample / 8));
    wfx.Format.nAvgBytesPerSec = wfx.Format.nSamplesPerSec * wfx.Format.nBlockAlign;
    wfx.Format.cbSize = sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX);
    wfx.Samples.wValidBitsPerSample = 32;
    wfx.dwChannelMask = (g_audio.channels == 2) ? (SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT) : 0;
    wfx.SubFormat = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;

    REFERENCE_TIME hnsRequestedDuration = 400000; // 40ms low-latency shared buffer
    DWORD streamFlags = AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
    HRESULT hr = g_audio.audioClient->Initialize(
        AUDCLNT_SHAREMODE_SHARED,
        streamFlags,
        hnsRequestedDuration,
        0,
        &wfx.Format,
        nullptr);

    if (FAILED(hr)) {
        g_audio.audioClient.Reset();
        return 0;
    }

    if (FAILED(g_audio.audioClient->GetBufferSize(&g_audio.bufferFrameCount)) ||
        FAILED(g_audio.audioClient->GetService(IID_PPV_ARGS(&g_audio.renderClient)))) {
        g_audio.audioClient.Reset();
        g_audio.renderClient.Reset();
        return 0;
    }

    return 0;
}

void RdAudioStart() {
    std::lock_guard<std::mutex> lock(g_audio.mutex);
    if (g_audio.audioClient && !g_audio.started) {
        if (SUCCEEDED(g_audio.audioClient->Start())) {
            g_audio.started = true;
        }
    }
}

void RdAudioStop() {
    std::lock_guard<std::mutex> lock(g_audio.mutex);
    if (g_audio.audioClient && g_audio.started) {
        g_audio.audioClient->Stop();
        g_audio.started = false;
    }
}

void RdAudioCleanup() {
    std::lock_guard<std::mutex> lock(g_audio.mutex);
    if (g_audio.audioClient && g_audio.started) {
        g_audio.audioClient->Stop();
        g_audio.started = false;
    }
    g_audio.renderClient.Reset();
    g_audio.audioClient.Reset();
    g_audio.device.Reset();
    g_audio.enumerator.Reset();
    if (g_audio.decoder) {
        opus_multistream_decoder_destroy(g_audio.decoder);
        g_audio.decoder = nullptr;
    }
    g_audio.pcmBuffer.clear();
    if (g_audio.comInited) {
        CoUninitialize();
        g_audio.comInited = false;
    }
}

void RdAudioDecodeAndPlay(char* sampleData, int sampleLength) {
    if (!sampleData || sampleLength <= 0) {
        return;
    }

    std::lock_guard<std::mutex> lock(g_audio.mutex);
    if (!g_audio.decoder || !g_audio.audioClient || !g_audio.renderClient) {
        return;
    }

    int maxSamples = static_cast<int>(g_audio.pcmBuffer.size() / (std::max)(1, g_audio.channels));
    int decodedSamples = opus_multistream_decode_float(
        g_audio.decoder,
        reinterpret_cast<const unsigned char*>(sampleData),
        sampleLength,
        g_audio.pcmBuffer.data(),
        maxSamples,
        0);

    if (decodedSamples <= 0) {
        return;
    }

    if (g_audio.muted.load(std::memory_order_relaxed)) {
        return;
    }

    UINT32 padding = 0;
    if (FAILED(g_audio.audioClient->GetCurrentPadding(&padding))) {
        return;
    }

    UINT32 available = (g_audio.bufferFrameCount > padding) ? (g_audio.bufferFrameCount - padding) : 0;
    UINT32 framesToWrite = static_cast<UINT32>(decodedSamples);
    if (framesToWrite > available) {
        // Drop old buffered excess by trimming to available space so latency stays minimal.
        framesToWrite = available;
    }
    if (framesToWrite == 0) {
        return;
    }

    BYTE* pData = nullptr;
    if (SUCCEEDED(g_audio.renderClient->GetBuffer(framesToWrite, &pData)) && pData) {
        size_t copyBytes = static_cast<size_t>(framesToWrite) * g_audio.channels * sizeof(float);
        std::memcpy(pData, g_audio.pcmBuffer.data(), copyBytes);
        g_audio.renderClient->ReleaseBuffer(framesToWrite, 0);
    }
}

void RdAudioSetMuted(int muted) {
    g_audio.muted.store(muted != 0, std::memory_order_relaxed);
}
