#include "stream_core_api.h"
#include "d3d11_mf_video.h"
#include "opus_wasapi_audio.h"
#include "Limelight.h"

#include <atomic>
#include <chrono>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>

namespace {

std::mutex g_sessionMutex;
RdStreamCallbacks g_callbacks{};
HWND g_targetHwnd = nullptr;
int g_initViewportW = 1280;
int g_initViewportH = 720;
int g_hardwareDecode = 1;
std::atomic<bool> g_running{ false };
std::atomic<bool> g_connected{ false };
std::thread g_startThread;
std::thread g_statsThread;

int VideoSetupCallback(int videoFormat, int width, int height, int redrawRate, void* /*context*/, int /*drFlags*/) {
    return RdVideoSetup(videoFormat, width, height, redrawRate, g_targetHwnd, g_initViewportW, g_initViewportH, g_hardwareDecode);
}

void VideoStartCallback() {
    RdVideoStart();
}

void VideoStopCallback() {
    RdVideoStop();
}

void VideoCleanupCallback() {
    RdVideoCleanup();
}

int VideoSubmitDecodeUnitCallback(PDECODE_UNIT decodeUnit) {
    return RdVideoSubmitDecodeUnit(decodeUnit);
}

int AudioInitCallback(int audioConfiguration, const POPUS_MULTISTREAM_CONFIGURATION opusConfig, void* /*context*/, int /*arFlags*/) {
    return RdAudioInit(audioConfiguration, opusConfig);
}

void AudioStartCallback() {
    RdAudioStart();
}

void AudioStopCallback() {
    RdAudioStop();
}

void AudioCleanupCallback() {
    RdAudioCleanup();
}

void AudioDecodeAndPlayCallback(char* sampleData, int sampleLength) {
    RdAudioDecodeAndPlay(sampleData, sampleLength);
}

void StageStartingCallback(int stage) {
    if (g_callbacks.on_stage) {
        g_callbacks.on_stage(stage, LiGetStageName(stage));
    }
}

void StageCompleteCallback(int /*stage*/) {
}

void StageFailedCallback(int stage, int errorCode) {
    g_connected.store(false);
    g_running.store(false);
    if (g_callbacks.on_disconnected) {
        const char* stageName = LiGetStageName(stage);
        std::string msg = std::string("连接阶段失败 [") + (stageName ? stageName : "未知") + "]";
        g_callbacks.on_disconnected(errorCode != 0 ? errorCode : -1, msg.c_str());
    }
}

void ConnectionStartedCallback() {
    g_connected.store(true);
    if (g_callbacks.on_connected) {
        g_callbacks.on_connected();
    }
}

void ConnectionTerminatedCallback(int errorCode) {
    g_connected.store(false);
    g_running.store(false);
    if (g_callbacks.on_disconnected) {
        const char* msg = (errorCode == 0) ? "远控会话已正常结束" : "远控连接意外断开";
        g_callbacks.on_disconnected(errorCode, msg);
    }
}

void ConnectionStatusUpdateCallback(int connectionStatus) {
    if (g_callbacks.on_connection_status) {
        g_callbacks.on_connection_status(connectionStatus == CONN_STATUS_POOR ? 1 : 0);
    }
}

inline short ToMoonlightKeyCode(short vk) {
    return static_cast<short>(0x8000 | (vk & 0x00FF));
}

void SendKeyDownUp(short vk, char modifiers) {
    const short wireVk = ToMoonlightKeyCode(vk);
    LiSendKeyboardEvent(wireVk, KEY_ACTION_DOWN, modifiers);
    LiSendKeyboardEvent(wireVk, KEY_ACTION_UP, modifiers);
}

} // namespace

uint32_t __cdecl rd_stream_query_supported_codecs(void) {
    return RdQuerySupportedVideoCodecs();
}

int __cdecl rd_stream_start(const RdStreamConfig* config, const RdStreamCallbacks* callbacks) {
    if (!config || !config->hwnd || !config->host) {
        return -1;
    }

    rd_stream_stop();

    std::lock_guard<std::mutex> lock(g_sessionMutex);
    g_callbacks = callbacks ? *callbacks : RdStreamCallbacks{};
    g_targetHwnd = static_cast<HWND>(config->hwnd);
    g_initViewportW = config->viewport_width > 0 ? config->viewport_width : 1280;
    g_initViewportH = config->viewport_height > 0 ? config->viewport_height : 720;
    g_hardwareDecode = config->hardware_decode;

    std::string hostStr = config->host;
    std::string appVerStr = config->app_version ? config->app_version : "7.1.431.-1";
    std::string gfeVerStr = config->gfe_version ? config->gfe_version : "3.23.0.74";
    std::string rtspStr = config->rtsp_session_url ? config->rtsp_session_url : "";

    STREAM_CONFIGURATION streamCfg;
    LiInitializeStreamConfiguration(&streamCfg);
    streamCfg.width = config->width > 0 ? config->width : 1920;
    streamCfg.height = config->height > 0 ? config->height : 1080;
    streamCfg.fps = config->fps > 0 ? config->fps : 60;
    streamCfg.bitrate = config->bitrate_kbps > 0 ? config->bitrate_kbps : 35000;
    streamCfg.packetSize = 1024;
    streamCfg.streamingRemotely = config->streaming_remotely;
    streamCfg.audioConfiguration = AUDIO_CONFIGURATION_STEREO;
    streamCfg.colorSpace = COLORSPACE_REC_709;
    streamCfg.colorRange = COLOR_RANGE_LIMITED;
    streamCfg.encryptionFlags = ENCFLG_ALL;
    streamCfg.clientRefreshRateX100 = streamCfg.fps * 100;
    std::memcpy(streamCfg.remoteInputAesKey, config->remote_input_aes_key, 16);
    std::memcpy(streamCfg.remoteInputAesIv, config->remote_input_aes_iv, 16);

    switch (config->preferred_codec) {
        case RD_PREF_CODEC_AV1:
            streamCfg.supportedVideoFormats = VIDEO_FORMAT_AV1_MAIN8 | VIDEO_FORMAT_H265 | VIDEO_FORMAT_H264;
            break;
        case RD_PREF_CODEC_HEVC:
            streamCfg.supportedVideoFormats = VIDEO_FORMAT_H265 | VIDEO_FORMAT_H264;
            break;
        case RD_PREF_CODEC_H264:
            streamCfg.supportedVideoFormats = VIDEO_FORMAT_H264;
            break;
        default:
            streamCfg.supportedVideoFormats = VIDEO_FORMAT_H265 | VIDEO_FORMAT_H264;
            break;
    }

    int serverCodecMask = config->server_codec_mode_support != 0 ? config->server_codec_mode_support : SCM_H264;

    g_running.store(true);
    g_connected.store(false);

    g_startThread = std::thread([hostStr, appVerStr, gfeVerStr, rtspStr, streamCfg, serverCodecMask]() mutable {
        SERVER_INFORMATION serverInfo;
        LiInitializeServerInformation(&serverInfo);
        serverInfo.address = hostStr.c_str();
        serverInfo.serverInfoAppVersion = appVerStr.c_str();
        serverInfo.serverInfoGfeVersion = gfeVerStr.c_str();
        serverInfo.rtspSessionUrl = rtspStr.empty() ? nullptr : rtspStr.c_str();
        serverInfo.serverCodecModeSupport = serverCodecMask;

        DECODER_RENDERER_CALLBACKS dr;
        LiInitializeVideoCallbacks(&dr);
        dr.setup = VideoSetupCallback;
        dr.start = VideoStartCallback;
        dr.stop = VideoStopCallback;
        dr.cleanup = VideoCleanupCallback;
        dr.submitDecodeUnit = VideoSubmitDecodeUnitCallback;
        dr.capabilities = CAPABILITY_DIRECT_SUBMIT;

        AUDIO_RENDERER_CALLBACKS ar;
        LiInitializeAudioCallbacks(&ar);
        ar.init = AudioInitCallback;
        ar.start = AudioStartCallback;
        ar.stop = AudioStopCallback;
        ar.cleanup = AudioCleanupCallback;
        ar.decodeAndPlaySample = AudioDecodeAndPlayCallback;
        ar.capabilities = CAPABILITY_DIRECT_SUBMIT;

        CONNECTION_LISTENER_CALLBACKS cl;
        LiInitializeConnectionCallbacks(&cl);
        cl.stageStarting = StageStartingCallback;
        cl.stageComplete = StageCompleteCallback;
        cl.stageFailed = StageFailedCallback;
        cl.connectionStarted = ConnectionStartedCallback;
        cl.connectionTerminated = ConnectionTerminatedCallback;
        cl.connectionStatusUpdate = ConnectionStatusUpdateCallback;

        int err = LiStartConnection(&serverInfo, &streamCfg, &cl, &dr, &ar, nullptr, 0, nullptr, 0);
        if (err != 0 && g_running.load()) {
            g_running.store(false);
            g_connected.store(false);
            if (g_callbacks.on_disconnected) {
                g_callbacks.on_disconnected(err, "无法建立串流通道，请检查网络或目标主机状态。");
            }
        }
    });

    g_statsThread = std::thread([]() {
        while (g_running.load()) {
            std::this_thread::sleep_for(std::chrono::milliseconds(500));
            if (!g_running.load()) {
                break;
            }
            if (!g_connected.load() || !g_callbacks.on_stats) {
                continue;
            }

            RdStreamStats stats{};
            RdVideoGetSnapshot(&stats);
            uint32_t rtt = 0, rttVar = 0;
            if (LiGetEstimatedRttInfo(&rtt, &rttVar)) {
                stats.rtt_ms = rtt;
                stats.rtt_variance_ms = rttVar;
            }
            g_callbacks.on_stats(&stats);
        }
    });

    return 0;
}

void __cdecl rd_stream_stop(void) {
    bool wasRunning = g_running.exchange(false);
    g_connected.store(false);

    if (wasRunning) {
        LiInterruptConnection();
    }

    if (g_statsThread.joinable()) {
        g_statsThread.join();
    }
    if (g_startThread.joinable()) {
        g_startThread.join();
    }

    if (wasRunning) {
        LiStopConnection();
    }
}

void __cdecl rd_stream_resize(int width, int height) {
    RdVideoResize(width, height);
}

void __cdecl rd_stream_set_audio_muted(int muted) {
    RdAudioSetMuted(muted);
}

int __cdecl rd_input_mouse_position(short x, short y, short ref_width, short ref_height) {
    if (!g_connected.load()) return 0;
    return LiSendMousePositionEvent(x, y, ref_width, ref_height);
}

int __cdecl rd_input_mouse_move(short dx, short dy) {
    if (!g_connected.load()) return 0;
    return LiSendMouseMoveEvent(dx, dy);
}

int __cdecl rd_input_mouse_button(unsigned char action, int button) {
    if (!g_connected.load()) return 0;
    return LiSendMouseButtonEvent(static_cast<char>(action), button);
}

int __cdecl rd_input_scroll_v(short high_res_delta) {
    if (!g_connected.load()) return 0;
    return LiSendHighResScrollEvent(high_res_delta);
}

int __cdecl rd_input_scroll_h(short high_res_delta) {
    if (!g_connected.load()) return 0;
    return LiSendHighResHScrollEvent(high_res_delta);
}

int __cdecl rd_input_key_event(short vk_code, char key_action, char modifiers) {
    if (!g_connected.load()) return 0;
    return LiSendKeyboardEvent(ToMoonlightKeyCode(vk_code), key_action, modifiers);
}

int __cdecl rd_input_utf8_text(const char* text, unsigned int length) {
    if (!g_connected.load() || !text || length == 0) return 0;
    return LiSendUtf8TextEvent(text, length);
}

int __cdecl rd_input_send_hotkey(int hotkey_id) {
    if (!g_connected.load()) return -1;

    switch (hotkey_id) {
        case RD_HOTKEY_CTRL_ALT_DEL:
            LiSendKeyboardEvent(ToMoonlightKeyCode(VK_LCONTROL), KEY_ACTION_DOWN, MODIFIER_CTRL);
            LiSendKeyboardEvent(ToMoonlightKeyCode(VK_LMENU), KEY_ACTION_DOWN, MODIFIER_CTRL | MODIFIER_ALT);
            LiSendKeyboardEvent(ToMoonlightKeyCode(VK_DELETE), KEY_ACTION_DOWN, MODIFIER_CTRL | MODIFIER_ALT | MODIFIER_EXTENDED);
            LiSendKeyboardEvent(ToMoonlightKeyCode(VK_DELETE), KEY_ACTION_UP, MODIFIER_CTRL | MODIFIER_ALT | MODIFIER_EXTENDED);
            LiSendKeyboardEvent(ToMoonlightKeyCode(VK_LMENU), KEY_ACTION_UP, MODIFIER_CTRL);
            LiSendKeyboardEvent(ToMoonlightKeyCode(VK_LCONTROL), KEY_ACTION_UP, 0);
            return 0;

        case RD_HOTKEY_WIN:
            SendKeyDownUp(VK_LWIN, MODIFIER_META | MODIFIER_EXTENDED);
            return 0;

        case RD_HOTKEY_WIN_D:
            LiSendKeyboardEvent(ToMoonlightKeyCode(VK_LWIN), KEY_ACTION_DOWN, MODIFIER_META | MODIFIER_EXTENDED);
            SendKeyDownUp('D', MODIFIER_META);
            LiSendKeyboardEvent(ToMoonlightKeyCode(VK_LWIN), KEY_ACTION_UP, 0);
            return 0;

        case RD_HOTKEY_ALT_TAB:
            LiSendKeyboardEvent(ToMoonlightKeyCode(VK_LMENU), KEY_ACTION_DOWN, MODIFIER_ALT);
            SendKeyDownUp(VK_TAB, MODIFIER_ALT);
            LiSendKeyboardEvent(ToMoonlightKeyCode(VK_LMENU), KEY_ACTION_UP, 0);
            return 0;

        case RD_HOTKEY_CTRL_SHIFT_ESC:
            LiSendKeyboardEvent(ToMoonlightKeyCode(VK_LCONTROL), KEY_ACTION_DOWN, MODIFIER_CTRL);
            LiSendKeyboardEvent(ToMoonlightKeyCode(VK_LSHIFT), KEY_ACTION_DOWN, MODIFIER_CTRL | MODIFIER_SHIFT);
            SendKeyDownUp(VK_ESCAPE, MODIFIER_CTRL | MODIFIER_SHIFT);
            LiSendKeyboardEvent(ToMoonlightKeyCode(VK_LSHIFT), KEY_ACTION_UP, MODIFIER_CTRL);
            LiSendKeyboardEvent(ToMoonlightKeyCode(VK_LCONTROL), KEY_ACTION_UP, 0);
            return 0;

        case RD_HOTKEY_WIN_L:
            LiSendKeyboardEvent(ToMoonlightKeyCode(VK_LWIN), KEY_ACTION_DOWN, MODIFIER_META | MODIFIER_EXTENDED);
            SendKeyDownUp('L', MODIFIER_META);
            LiSendKeyboardEvent(ToMoonlightKeyCode(VK_LWIN), KEY_ACTION_UP, 0);
            return 0;

        default:
            return -1;
    }
}
