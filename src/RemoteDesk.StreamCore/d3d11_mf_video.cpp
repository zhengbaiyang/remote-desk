#include "d3d11_mf_video.h"

#include <d3d11.h>
#include <d3d11_1.h>
#include <dxgi1_2.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <codecapi.h>
#include <icodecapi.h>
#include <wrl/client.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstring>
#include <mutex>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace {

// Fallback GUID definitions if missing in older SDK headers.
static const GUID kCodecApiAvLowLatencyMode = { 0x9c27891a, 0xed7a, 0x40e1, { 0x88, 0xe8, 0xb2, 0x27, 0x27, 0xa0, 0x24, 0xee } };
static const GUID kMfLowLatency = { 0x9c27891a, 0xed7a, 0x40e1, { 0x88, 0xe8, 0xb2, 0x27, 0x27, 0xa0, 0x24, 0xee } };
static const GUID kMfTransformAsync = { 0xf81a699a, 0x649a, 0x497d, { 0x8c, 0x73, 0x29, 0xf8, 0xfe, 0xd6, 0xad, 0x7a } };
static const GUID kMfTransformAsyncUnlock = { 0xe5666d6b, 0x3422, 0x4eb6, { 0xa4, 0x21, 0xda, 0x7d, 0xb1, 0xf8, 0xe2, 0x07 } };

struct VideoState {
    std::mutex mutex;
    HWND hwnd = nullptr;
    int viewportW = 1280;
    int viewportH = 720;
    int streamW = 1920;
    int streamH = 1080;
    int redrawRate = 60;
    int videoFormat = VIDEO_FORMAT_H264;
    bool preferHw = true;
    bool hwAccelerated = false;
    bool isAsyncMft = false;
    bool mfStarted = false;
    bool comInited = false;
    bool streamStarted = false;
    std::atomic<bool> stopping{ false };

    ComPtr<ID3D11Device> d3dDevice;
    ComPtr<ID3D11DeviceContext> d3dContext;
    ComPtr<IDXGISwapChain1> swapChain;
    ComPtr<ID3D11RenderTargetView> backBufferRtv;
    ComPtr<ID3D11VideoDevice> videoDevice;
    ComPtr<ID3D11VideoContext> videoContext;
    ComPtr<ID3D11VideoProcessorEnumerator> vpEnum;
    ComPtr<ID3D11VideoProcessor> videoProcessor;
    ComPtr<ID3D11Texture2D> cpuUploadNv12;
    UINT cpuUploadW = 0;
    UINT cpuUploadH = 0;

    ComPtr<IMFDXGIDeviceManager> dxgiManager;
    UINT dxgiResetToken = 0;
    ComPtr<IMFTransform> decoder;
    ComPtr<IMFMediaEventGenerator> eventGen;

    std::vector<uint8_t> stagingNal;
    std::vector<uint8_t> cpuNv12Buffer;

    // Telemetry
    std::atomic<uint64_t> rxFramesInWindow{ 0 };
    std::atomic<uint64_t> txFramesInWindow{ 0 };
    std::atomic<uint64_t> bytesInWindow{ 0 };
    std::atomic<uint32_t> droppedFrames{ 0 };
    double lastRxFps = 0.0;
    double lastTxFps = 0.0;
    double lastDecodeMs = 0.0;
    uint32_t lastBitrateKbps = 0;
    std::chrono::steady_clock::time_point windowStart = std::chrono::steady_clock::now();
};

VideoState g_video;

GUID FormatToMfSubtype(int videoFormat) {
    if (videoFormat & VIDEO_FORMAT_MASK_AV1) return MFVideoFormat_AV1;
    if (videoFormat & VIDEO_FORMAT_MASK_H265) return MFVideoFormat_HEVC;
    return MFVideoFormat_H264;
}

int FormatToPrefCodec(int videoFormat) {
    if (videoFormat & VIDEO_FORMAT_MASK_AV1) return RD_PREF_CODEC_AV1;
    if (videoFormat & VIDEO_FORMAT_MASK_H265) return RD_PREF_CODEC_HEVC;
    return RD_PREF_CODEC_H264;
}

bool ProbeMftForSubtype(const GUID& subtype) {
    MFT_REGISTER_TYPE_INFO inInfo{ MFMediaType_Video, subtype };
    MFT_REGISTER_TYPE_INFO outInfo{ MFMediaType_Video, MFVideoFormat_NV12 };
    IMFActivate** activates = nullptr;
    UINT32 count = 0;
    UINT32 flags = MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_ASYNCMFT | MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_LOCALMFT | MFT_ENUM_FLAG_SORTANDFILTER;
    HRESULT hr = MFTEnumEx(MFT_CATEGORY_VIDEO_DECODER, flags, &inInfo, &outInfo, &activates, &count);
    if (FAILED(hr) || count == 0) {
        return false;
    }
    for (UINT32 i = 0; i < count; ++i) {
        if (activates[i]) activates[i]->Release();
    }
    CoTaskMemFree(activates);
    return true;
}

bool CreateBackBufferAndVp(VideoState& s) {
    s.backBufferRtv.Reset();
    ComPtr<ID3D11Texture2D> backBuffer;
    if (FAILED(s.swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer)))) {
        return false;
    }
    if (FAILED(s.d3dDevice->CreateRenderTargetView(backBuffer.Get(), nullptr, &s.backBufferRtv))) {
        return false;
    }

    s.vpEnum.Reset();
    s.videoProcessor.Reset();

    D3D11_VIDEO_PROCESSOR_CONTENT_DESC desc{};
    desc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
    desc.InputWidth = static_cast<UINT>((std::max)(16, s.streamW));
    desc.InputHeight = static_cast<UINT>((std::max)(16, s.streamH));
    desc.OutputWidth = static_cast<UINT>((std::max)(16, s.viewportW));
    desc.OutputHeight = static_cast<UINT>((std::max)(16, s.viewportH));
    desc.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;

    if (FAILED(s.videoDevice->CreateVideoProcessorEnumerator(&desc, &s.vpEnum))) {
        return false;
    }
    if (FAILED(s.videoDevice->CreateVideoProcessor(s.vpEnum.Get(), 0, &s.videoProcessor))) {
        return false;
    }

    return true;
}

bool InitD3D11(VideoState& s, HWND hwnd, int viewportW, int viewportH) {
    s.hwnd = hwnd;
    s.viewportW = (std::max)(16, viewportW);
    s.viewportH = (std::max)(16, viewportH);

    UINT createFlags = D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
    D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    D3D_FEATURE_LEVEL obtainedLevel = D3D_FEATURE_LEVEL_11_0;

    HRESULT hr = D3D11CreateDevice(
        nullptr,
        D3D_DRIVER_TYPE_HARDWARE,
        nullptr,
        createFlags,
        levels,
        ARRAYSIZE(levels),
        D3D11_SDK_VERSION,
        &s.d3dDevice,
        &obtainedLevel,
        &s.d3dContext);

    if (FAILED(hr)) {
        hr = D3D11CreateDevice(
            nullptr,
            D3D_DRIVER_TYPE_WARP,
            nullptr,
            createFlags,
            levels,
            ARRAYSIZE(levels),
            D3D11_SDK_VERSION,
            &s.d3dDevice,
            &obtainedLevel,
            &s.d3dContext);
        if (FAILED(hr)) {
            return false;
        }
    }

    ComPtr<ID3D10Multithread> mt;
    if (SUCCEEDED(s.d3dDevice.As(&mt)) && mt) {
        mt->SetMultithreadProtected(TRUE);
    }

    if (FAILED(s.d3dDevice.As(&s.videoDevice)) || FAILED(s.d3dContext.As(&s.videoContext))) {
        return false;
    }

    ComPtr<IDXGIDevice> dxgiDevice;
    ComPtr<IDXGIAdapter> adapter;
    ComPtr<IDXGIFactory2> factory;
    if (FAILED(s.d3dDevice.As(&dxgiDevice)) ||
        FAILED(dxgiDevice->GetAdapter(&adapter)) ||
        FAILED(adapter->GetParent(IID_PPV_ARGS(&factory)))) {
        return false;
    }

    DXGI_SWAP_CHAIN_DESC1 scDesc{};
    scDesc.Width = static_cast<UINT>(s.viewportW);
    scDesc.Height = static_cast<UINT>(s.viewportH);
    scDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    scDesc.SampleDesc.Count = 1;
    scDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    scDesc.BufferCount = 2;
    scDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    scDesc.Scaling = DXGI_SCALING_STRETCH;
    scDesc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;

    if (FAILED(factory->CreateSwapChainForHwnd(s.d3dDevice.Get(), hwnd, &scDesc, nullptr, nullptr, &s.swapChain))) {
        return false;
    }
    factory->MakeWindowAssociation(hwnd, DXGI_MWA_NO_ALT_ENTER);

    if (FAILED(MFCreateDXGIDeviceManager(&s.dxgiResetToken, &s.dxgiManager))) {
        return false;
    }
    if (FAILED(s.dxgiManager->ResetDevice(s.d3dDevice.Get(), s.dxgiResetToken))) {
        return false;
    }

    return CreateBackBufferAndVp(s);
}

bool SelectNv12OutputType(IMFTransform* mft) {
    for (DWORD i = 0; ; ++i) {
        ComPtr<IMFMediaType> outType;
        HRESULT hr = mft->GetOutputAvailableType(0, i, &outType);
        if (hr == MF_E_NO_MORE_TYPES || FAILED(hr)) {
            break;
        }
        GUID subtype{};
        if (SUCCEEDED(outType->GetGUID(MF_MT_SUBTYPE, &subtype)) && IsEqualGUID(subtype, MFVideoFormat_NV12)) {
            if (SUCCEEDED(mft->SetOutputType(0, outType.Get(), 0))) {
                return true;
            }
        }
    }
    return false;
}

bool TryCreateDecoderMft(VideoState& s, const GUID& subtype, UINT32 flags, bool attachDxgi) {
    MFT_REGISTER_TYPE_INFO inInfo{ MFMediaType_Video, subtype };
    MFT_REGISTER_TYPE_INFO outInfo{ MFMediaType_Video, MFVideoFormat_NV12 };
    IMFActivate** activates = nullptr;
    UINT32 count = 0;
    if (FAILED(MFTEnumEx(MFT_CATEGORY_VIDEO_DECODER, flags, &inInfo, &outInfo, &activates, &count)) || count == 0) {
        return false;
    }

    bool ok = false;
    for (UINT32 i = 0; i < count && !ok; ++i) {
        ComPtr<IMFTransform> candidate;
        if (!activates[i] || FAILED(activates[i]->ActivateObject(IID_PPV_ARGS(&candidate)))) {
            continue;
        }

        ComPtr<IMFAttributes> attrs;
        UINT32 isAsync = 0;
        if (SUCCEEDED(candidate->GetAttributes(&attrs)) && attrs) {
            attrs->GetUINT32(kMfTransformAsync, &isAsync);
            if (isAsync) {
                attrs->SetUINT32(kMfTransformAsyncUnlock, TRUE);
            }
            attrs->SetUINT32(kMfLowLatency, TRUE);
        }

        ComPtr<ICodecAPI> codecApi;
        if (SUCCEEDED(candidate.As(&codecApi)) && codecApi) {
            VARIANT var;
            VariantInit(&var);
            var.vt = VT_BOOL;
            var.boolVal = VARIANT_TRUE;
            codecApi->SetValue(&kCodecApiAvLowLatencyMode, &var);
        }

        bool d3dBound = false;
        if (attachDxgi && s.dxgiManager) {
            if (SUCCEEDED(candidate->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, reinterpret_cast<ULONG_PTR>(s.dxgiManager.Get())))) {
                d3dBound = true;
            }
        }

        ComPtr<IMFMediaType> inputType;
        if (SUCCEEDED(MFCreateMediaType(&inputType))) {
            inputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            inputType->SetGUID(MF_MT_SUBTYPE, subtype);
            MFSetAttributeSize(inputType.Get(), MF_MT_FRAME_SIZE, static_cast<UINT32>(s.streamW), static_cast<UINT32>(s.streamH));
            MFSetAttributeRatio(inputType.Get(), MF_MT_FRAME_RATE, static_cast<UINT32>(s.redrawRate), 1);

            if (SUCCEEDED(candidate->SetInputType(0, inputType.Get(), 0)) && SelectNv12OutputType(candidate.Get())) {
                candidate->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
                candidate->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);

                s.decoder = candidate;
                s.isAsyncMft = (isAsync != 0);
                if (s.isAsyncMft) {
                    s.decoder.As(&s.eventGen);
                }
                s.hwAccelerated = d3dBound;
                ok = true;
            }
        }

        if (!ok) {
            activates[i]->ShutdownObject();
        }
    }

    for (UINT32 i = 0; i < count; ++i) {
        if (activates[i]) activates[i]->Release();
    }
    CoTaskMemFree(activates);
    return ok;
}

bool InitDecoder(VideoState& s) {
    GUID subtype = FormatToMfSubtype(s.videoFormat);

    // Prefer synchronous MFT with D3D11VA hardware acceleration first, then async HW, then software fallback.
    if (s.preferHw) {
        if (TryCreateDecoderMft(s, subtype, MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_LOCALMFT | MFT_ENUM_FLAG_SORTANDFILTER, true)) {
            return true;
        }
        if (TryCreateDecoderMft(s, subtype, MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_ASYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER, true)) {
            return true;
        }
    }

    return TryCreateDecoderMft(s, subtype, MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_LOCALMFT | MFT_ENUM_FLAG_SORTANDFILTER, false);
}

void ComputeLetterboxRect(int hostW, int hostH, int videoW, int videoH, RECT& outRect) {
    if (hostW <= 0 || hostH <= 0 || videoW <= 0 || videoH <= 0) {
        outRect = { 0, 0, (std::max)(1, hostW), (std::max)(1, hostH) };
        return;
    }

    double hostAspect = static_cast<double>(hostW) / hostH;
    double videoAspect = static_cast<double>(videoW) / videoH;
    int drawW = hostW;
    int drawH = hostH;
    int offX = 0;
    int offY = 0;

    if (hostAspect > videoAspect) {
        drawH = hostH;
        drawW = static_cast<int>(hostH * videoAspect + 0.5);
        offX = (hostW - drawW) / 2;
    } else if (hostAspect < videoAspect) {
        drawW = hostW;
        drawH = static_cast<int>(hostW / videoAspect + 0.5);
        offY = (hostH - drawH) / 2;
    }

    outRect.left = offX;
    outRect.top = offY;
    outRect.right = offX + drawW;
    outRect.bottom = offY + drawH;
}

bool RenderTextureToSwapChain(VideoState& s, ID3D11Texture2D* srcTex, UINT subresourceIdx) {
    if (!s.swapChain || !s.backBufferRtv || !s.videoDevice || !s.videoContext || !s.videoProcessor) {
        return false;
    }

    D3D11_TEXTURE2D_DESC srcDesc{};
    srcTex->GetDesc(&srcDesc);

    ComPtr<ID3D11Texture2D> backBuffer;
    if (FAILED(s.swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer)))) {
        return false;
    }

    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inViewDesc{};
    inViewDesc.FourCC = 0;
    inViewDesc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
    inViewDesc.Texture2D.MipSlice = 0;
    inViewDesc.Texture2D.ArraySlice = subresourceIdx;

    ComPtr<ID3D11VideoProcessorInputView> inputView;
    if (FAILED(s.videoDevice->CreateVideoProcessorInputView(srcTex, s.vpEnum.Get(), &inViewDesc, &inputView))) {
        return false;
    }

    D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outViewDesc{};
    outViewDesc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
    outViewDesc.Texture2D.MipSlice = 0;

    ComPtr<ID3D11VideoProcessorOutputView> outputView;
    if (FAILED(s.videoDevice->CreateVideoProcessorOutputView(backBuffer.Get(), s.vpEnum.Get(), &outViewDesc, &outputView))) {
        return false;
    }

    const float clearColor[4] = { 0.0f, 0.0f, 0.0f, 1.0f };
    s.d3dContext->ClearRenderTargetView(s.backBufferRtv.Get(), clearColor);

    RECT srcRect{ 0, 0, static_cast<LONG>((std::min)(static_cast<UINT>(s.streamW), srcDesc.Width)), static_cast<LONG>((std::min)(static_cast<UINT>(s.streamH), srcDesc.Height)) };
    RECT dstRect{};
    ComputeLetterboxRect(s.viewportW, s.viewportH, s.streamW, s.streamH, dstRect);

    s.videoContext->VideoProcessorSetStreamSourceRect(s.videoProcessor.Get(), 0, TRUE, &srcRect);
    s.videoContext->VideoProcessorSetStreamDestRect(s.videoProcessor.Get(), 0, TRUE, &dstRect);
    s.videoContext->VideoProcessorSetOutputTargetRect(s.videoProcessor.Get(), TRUE, &dstRect);

    D3D11_VIDEO_PROCESSOR_STREAM streamData{};
    streamData.Enable = TRUE;
    streamData.pInputSurface = inputView.Get();

    if (FAILED(s.videoContext->VideoProcessorBlt(s.videoProcessor.Get(), outputView.Get(), 0, 1, &streamData))) {
        return false;
    }

    s.swapChain->Present(0, 0);
    return true;
}

bool RenderOutputSample(VideoState& s, IMFSample* sample) {
    ComPtr<IMFMediaBuffer> mediaBuffer;
    if (FAILED(sample->GetBufferByIndex(0, &mediaBuffer))) {
        return false;
    }

    ComPtr<IMFDXGIBuffer> dxgiBuffer;
    if (SUCCEEDED(mediaBuffer.As(&dxgiBuffer)) && dxgiBuffer) {
        ComPtr<ID3D11Texture2D> tex;
        UINT subIdx = 0;
        if (SUCCEEDED(dxgiBuffer->GetResource(IID_PPV_ARGS(&tex))) &&
            SUCCEEDED(dxgiBuffer->GetSubresourceIndex(&subIdx))) {
            return RenderTextureToSwapChain(s, tex.Get(), subIdx);
        }
    }

    // CPU buffer upload fallback (software MFT)
    UINT texW = static_cast<UINT>((std::max)(16, (s.streamW + 1) & ~1));
    UINT texH = static_cast<UINT>((std::max)(16, (s.streamH + 1) & ~1));
    if (!s.cpuUploadNv12 || s.cpuUploadW != texW || s.cpuUploadH != texH) {
        D3D11_TEXTURE2D_DESC td{};
        td.Width = texW;
        td.Height = texH;
        td.MipLevels = 1;
        td.ArraySize = 1;
        td.Format = DXGI_FORMAT_NV12;
        td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_DEFAULT;
        td.BindFlags = D3D11_BIND_DECODER;
        if (FAILED(s.d3dDevice->CreateTexture2D(&td, nullptr, &s.cpuUploadNv12))) {
            return false;
        }
        s.cpuUploadW = texW;
        s.cpuUploadH = texH;
    }

    size_t tightRow = texW;
    size_t tightTotal = tightRow * texH * 3 / 2;
    s.cpuNv12Buffer.resize(tightTotal);

    ComPtr<IMF2DBuffer> buf2d;
    if (SUCCEEDED(mediaBuffer.As(&buf2d)) && buf2d) {
        BYTE* scan0 = nullptr;
        LONG pitch = 0;
        if (SUCCEEDED(buf2d->Lock2D(&scan0, &pitch))) {
            // Y plane + interleaved UV plane (texH * 3 / 2 rows)
            UINT totalRows = texH + texH / 2;
            for (UINT r = 0; r < totalRows; ++r) {
                std::memcpy(s.cpuNv12Buffer.data() + r * tightRow, scan0 + static_cast<size_t>(r) * pitch, tightRow);
            }
            buf2d->Unlock2D();
        } else {
            return false;
        }
    } else {
        BYTE* rawPtr = nullptr;
        DWORD maxLen = 0, curLen = 0;
        if (SUCCEEDED(mediaBuffer->Lock(&rawPtr, &maxLen, &curLen))) {
            size_t toCopy = (std::min)(tightTotal, static_cast<size_t>(curLen));
            std::memcpy(s.cpuNv12Buffer.data(), rawPtr, toCopy);
            mediaBuffer->Unlock();
        } else {
            return false;
        }
    }

    s.d3dContext->UpdateSubresource(s.cpuUploadNv12.Get(), 0, nullptr, s.cpuNv12Buffer.data(), static_cast<UINT>(tightRow), 0);
    return RenderTextureToSwapChain(s, s.cpuUploadNv12.Get(), 0);
}

bool DrainDecoderOutput(VideoState& s) {
    bool renderedAny = false;
    while (!s.stopping.load()) {
        MFT_OUTPUT_STREAM_INFO info{};
        s.decoder->GetOutputStreamInfo(0, &info);

        MFT_OUTPUT_DATA_BUFFER outBuf{};
        ComPtr<IMFSample> callerSample;
        if (!(info.dwFlags & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES))) {
            if (FAILED(MFCreateSample(&callerSample))) {
                break;
            }
            ComPtr<IMFMediaBuffer> mb;
            if (FAILED(MFCreateMemoryBuffer(info.cbSize > 0 ? info.cbSize : static_cast<DWORD>(s.streamW * s.streamH * 3 / 2), &mb))) {
                break;
            }
            callerSample->AddBuffer(mb.Get());
            outBuf.pSample = callerSample.Get();
        }

        DWORD status = 0;
        HRESULT hr = s.decoder->ProcessOutput(0, 1, &outBuf, &status);
        if (outBuf.pEvents) {
            outBuf.pEvents->Release();
            outBuf.pEvents = nullptr;
        }

        if (hr == MF_E_TRANSFORM_STREAM_CHANGE) {
            SelectNv12OutputType(s.decoder.Get());
            ComPtr<IMFMediaType> curOut;
            if (SUCCEEDED(s.decoder->GetOutputCurrentType(0, &curOut))) {
                UINT32 w = 0, h = 0;
                if (SUCCEEDED(MFGetAttributeSize(curOut.Get(), MF_MT_FRAME_SIZE, &w, &h)) && w > 0 && h > 0) {
                    s.streamW = static_cast<int>(w);
                    s.streamH = static_cast<int>(h);
                    CreateBackBufferAndVp(s);
                }
            }
            if (outBuf.pSample && !callerSample) outBuf.pSample->Release();
            continue;
        }

        if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT || FAILED(hr)) {
            if (outBuf.pSample && !callerSample) outBuf.pSample->Release();
            break;
        }

        if (outBuf.pSample) {
            if (RenderOutputSample(s, outBuf.pSample)) {
                s.txFramesInWindow.fetch_add(1, std::memory_order_relaxed);
                renderedAny = true;
            }
            if (!callerSample) {
                outBuf.pSample->Release();
            }
        }
    }
    return renderedAny;
}

void UpdateTelemetryWindow(VideoState& s, double decodeMs) {
    s.lastDecodeMs = (s.lastDecodeMs == 0.0) ? decodeMs : (s.lastDecodeMs * 0.8 + decodeMs * 0.2);

    auto now = std::chrono::steady_clock::now();
    auto elapsedMs = std::chrono::duration_cast<std::chrono::milliseconds>(now - s.windowStart).count();
    if (elapsedMs >= 500) {
        double secs = elapsedMs / 1000.0;
        uint64_t rx = s.rxFramesInWindow.exchange(0, std::memory_order_relaxed);
        uint64_t tx = s.txFramesInWindow.exchange(0, std::memory_order_relaxed);
        uint64_t bytes = s.bytesInWindow.exchange(0, std::memory_order_relaxed);
        s.lastRxFps = rx / secs;
        s.lastTxFps = tx / secs;
        s.lastBitrateKbps = static_cast<uint32_t>((bytes * 8.0) / (secs * 1000.0));
        s.windowStart = now;
    }
}

} // namespace

uint32_t RdQuerySupportedVideoCodecs() {
    HRESULT comHr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    HRESULT mfHr = MFStartup(MF_VERSION);

    uint32_t mask = RD_CODEC_H264; // Always claim H.264 (inbox MS decoder available on Win10/11)
    if (SUCCEEDED(mfHr)) {
        if (ProbeMftForSubtype(MFVideoFormat_H264)) mask |= RD_CODEC_H264;
        if (ProbeMftForSubtype(MFVideoFormat_HEVC)) mask |= RD_CODEC_HEVC;
        if (ProbeMftForSubtype(MFVideoFormat_AV1))  mask |= RD_CODEC_AV1;
        MFShutdown();
    }
    if (SUCCEEDED(comHr)) {
        CoUninitialize();
    }
    return mask;
}

int RdVideoSetup(int videoFormat, int width, int height, int redrawRate, HWND hwnd, int viewportW, int viewportH, int hardwareDecode) {
    std::lock_guard<std::mutex> lock(g_video.mutex);
    g_video.stopping.store(false);
    g_video.videoFormat = videoFormat;
    g_video.streamW = width;
    g_video.streamH = height;
    g_video.redrawRate = redrawRate > 0 ? redrawRate : 60;
    g_video.preferHw = (hardwareDecode != 0);
    g_video.rxFramesInWindow.store(0);
    g_video.txFramesInWindow.store(0);
    g_video.bytesInWindow.store(0);
    g_video.droppedFrames.store(0);
    g_video.lastRxFps = 0.0;
    g_video.lastTxFps = 0.0;
    g_video.lastDecodeMs = 0.0;
    g_video.lastBitrateKbps = 0;
    g_video.windowStart = std::chrono::steady_clock::now();

    HRESULT comHr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    g_video.comInited = SUCCEEDED(comHr);

    if (SUCCEEDED(MFStartup(MF_VERSION))) {
        g_video.mfStarted = true;
    } else {
        return -1;
    }

    if (!InitD3D11(g_video, hwnd, viewportW, viewportH)) {
        RdVideoCleanup();
        return -2;
    }

    if (!InitDecoder(g_video)) {
        RdVideoCleanup();
        return -3;
    }

    return 0;
}

void RdVideoStart() {
    std::lock_guard<std::mutex> lock(g_video.mutex);
    g_video.streamStarted = true;
}

void RdVideoStop() {
    g_video.stopping.store(true);
    std::lock_guard<std::mutex> lock(g_video.mutex);
    g_video.streamStarted = false;
    if (g_video.decoder) {
        g_video.decoder->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
    }
}

void RdVideoCleanup() {
    g_video.stopping.store(true);
    std::lock_guard<std::mutex> lock(g_video.mutex);

    if (g_video.decoder) {
        g_video.decoder->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
        g_video.decoder->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
    }
    g_video.eventGen.Reset();
    g_video.decoder.Reset();
    g_video.dxgiManager.Reset();
    g_video.cpuUploadNv12.Reset();
    g_video.videoProcessor.Reset();
    g_video.vpEnum.Reset();
    g_video.videoContext.Reset();
    g_video.videoDevice.Reset();
    g_video.backBufferRtv.Reset();
    g_video.swapChain.Reset();
    g_video.d3dContext.Reset();
    g_video.d3dDevice.Reset();
    g_video.stagingNal.clear();
    g_video.cpuNv12Buffer.clear();

    if (g_video.mfStarted) {
        MFShutdown();
        g_video.mfStarted = false;
    }
    if (g_video.comInited) {
        CoUninitialize();
        g_video.comInited = false;
    }
}

int RdVideoSubmitDecodeUnit(PDECODE_UNIT decodeUnit) {
    if (!decodeUnit || g_video.stopping.load()) {
        return DR_OK;
    }

    auto t0 = std::chrono::steady_clock::now();
    std::lock_guard<std::mutex> lock(g_video.mutex);
    if (!g_video.decoder || g_video.stopping.load()) {
        return DR_OK;
    }

    g_video.rxFramesInWindow.fetch_add(1, std::memory_order_relaxed);
    g_video.bytesInWindow.fetch_add(static_cast<uint64_t>(decodeUnit->fullLength), std::memory_order_relaxed);

    g_video.stagingNal.resize(static_cast<size_t>(decodeUnit->fullLength));
    size_t offset = 0;
    for (PLENTRY entry = decodeUnit->bufferList; entry != nullptr; entry = entry->next) {
        if (entry->data && entry->length > 0) {
            std::memcpy(g_video.stagingNal.data() + offset, entry->data, static_cast<size_t>(entry->length));
            offset += static_cast<size_t>(entry->length);
        }
    }

    ComPtr<IMFMediaBuffer> inBuf;
    ComPtr<IMFSample> inSample;
    if (FAILED(MFCreateMemoryBuffer(static_cast<DWORD>(offset), &inBuf)) ||
        FAILED(MFCreateSample(&inSample))) {
        return DR_NEED_IDR;
    }

    BYTE* dst = nullptr;
    if (FAILED(inBuf->Lock(&dst, nullptr, nullptr))) {
        return DR_NEED_IDR;
    }
    std::memcpy(dst, g_video.stagingNal.data(), offset);
    inBuf->Unlock();
    inBuf->SetCurrentLength(static_cast<DWORD>(offset));

    inSample->AddBuffer(inBuf.Get());
    inSample->SetSampleTime(static_cast<LONGLONG>(decodeUnit->presentationTimeUs * 10));
    inSample->SetSampleDuration(10000000LL / (std::max)(1, g_video.redrawRate));
    if (decodeUnit->frameType == FRAME_TYPE_IDR) {
        inSample->SetUINT32(MFSampleExtension_CleanPoint, TRUE);
    }

    if (g_video.isAsyncMft && g_video.eventGen) {
        // Pump pending async events briefly so HW async MFT accepts input and produces output.
        for (int iter = 0; iter < 8; ++iter) {
            ComPtr<IMFMediaEvent> ev;
            HRESULT evHr = g_video.eventGen->GetEvent(MF_EVENT_FLAG_NO_WAIT, &ev);
            if (evHr == MF_E_NO_EVENTS_AVAILABLE || FAILED(evHr) || !ev) {
                break;
            }
            MediaEventType met = MEUnknown;
            ev->GetType(&met);
            if (met == METransformHaveOutput) {
                DrainDecoderOutput(g_video);
            }
        }
    }

    HRESULT hr = g_video.decoder->ProcessInput(0, inSample.Get(), 0);
    if (hr == MF_E_NOTACCEPTING) {
        DrainDecoderOutput(g_video);
        hr = g_video.decoder->ProcessInput(0, inSample.Get(), 0);
    }
    if (FAILED(hr)) {
        g_video.droppedFrames.fetch_add(1, std::memory_order_relaxed);
        return DR_NEED_IDR;
    }

    DrainDecoderOutput(g_video);

    auto t1 = std::chrono::steady_clock::now();
    double decodeMs = std::chrono::duration_cast<std::chrono::microseconds>(t1 - t0).count() / 1000.0;
    UpdateTelemetryWindow(g_video, decodeMs);
    return DR_OK;
}

void RdVideoResize(int width, int height) {
    std::lock_guard<std::mutex> lock(g_video.mutex);
    if (!g_video.swapChain || width < 16 || height < 16) {
        return;
    }

    g_video.viewportW = width;
    g_video.viewportH = height;
    g_video.backBufferRtv.Reset();
    if (SUCCEEDED(g_video.swapChain->ResizeBuffers(0, static_cast<UINT>(width), static_cast<UINT>(height), DXGI_FORMAT_UNKNOWN, 0))) {
        CreateBackBufferAndVp(g_video);
    }
}

void RdVideoGetSnapshot(RdStreamStats* outStats) {
    if (!outStats) {
        return;
    }

    std::lock_guard<std::mutex> lock(g_video.mutex);
    outStats->received_fps = g_video.lastRxFps;
    outStats->rendered_fps = g_video.lastTxFps;
    outStats->decode_time_ms = g_video.lastDecodeMs;
    outStats->bitrate_kbps = g_video.lastBitrateKbps;
    outStats->dropped_frames = g_video.droppedFrames.load(std::memory_order_relaxed);
    outStats->video_width = g_video.streamW;
    outStats->video_height = g_video.streamH;
    outStats->active_codec = FormatToPrefCodec(g_video.videoFormat);
    outStats->hw_accelerated = g_video.hwAccelerated ? 1 : 0;
}
