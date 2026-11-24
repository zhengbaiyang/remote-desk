#pragma once

#include <stdint.h>

#ifdef RD_STREAMCORE_EXPORTS
#define RD_API __declspec(dllexport)
#else
#define RD_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

// Codec capability mask bits (matches Moonlight VIDEO_FORMAT_MASK_*)
#define RD_CODEC_H264 0x01u
#define RD_CODEC_HEVC 0x02u
#define RD_CODEC_AV1  0x04u

// Requested preferred codec
#define RD_PREF_CODEC_AUTO 0
#define RD_PREF_CODEC_H264 1
#define RD_PREF_CODEC_HEVC 2
#define RD_PREF_CODEC_AV1  3

// System hotkey IDs
#define RD_HOTKEY_CTRL_ALT_DEL   1
#define RD_HOTKEY_WIN            2
#define RD_HOTKEY_WIN_D          3
#define RD_HOTKEY_ALT_TAB        4
#define RD_HOTKEY_CTRL_SHIFT_ESC 5
#define RD_HOTKEY_WIN_L          6

// Mouse buttons (matches Limelight.h)
#define RD_BTN_ACTION_PRESS   0x07
#define RD_BTN_ACTION_RELEASE 0x08
#define RD_BTN_LEFT   0x01
#define RD_BTN_MIDDLE 0x02
#define RD_BTN_RIGHT  0x03
#define RD_BTN_X1     0x04
#define RD_BTN_X2     0x05

// Key actions (matches Limelight.h)
#define RD_KEY_DOWN 0x03
#define RD_KEY_UP   0x04

typedef struct _RdStreamConfig {
    void* hwnd;
    int viewport_width;
    int viewport_height;

    const char* host;
    const char* app_version;
    const char* gfe_version;
    const char* rtsp_session_url;

    int width;
    int height;
    int fps;
    int bitrate_kbps;
    int server_codec_mode_support;
    int preferred_codec;   // RD_PREF_CODEC_*
    int hardware_decode;   // 1 = prefer GPU MFT, 0 = software MFT
    int streaming_remotely; // 0 = local, 1 = remote, 2 = auto

    unsigned char remote_input_aes_key[16];
    unsigned char remote_input_aes_iv[16];
} RdStreamConfig;

typedef struct _RdStreamStats {
    double received_fps;
    double rendered_fps;
    double decode_time_ms;
    uint32_t rtt_ms;
    uint32_t rtt_variance_ms;
    uint32_t bitrate_kbps;
    uint32_t dropped_frames;
    int video_width;
    int video_height;
    int active_codec;     // RD_PREF_CODEC_H264 / HEVC / AV1
    int hw_accelerated;   // 1 if D3D11 DXVA hardware MFT active
} RdStreamStats;

typedef void(__cdecl* RdStageCallback)(int stage, const char* stage_name);
typedef void(__cdecl* RdConnectedCallback)(void);
typedef void(__cdecl* RdDisconnectedCallback)(int error_code, const char* message);
typedef void(__cdecl* RdConnectionStatusCallback)(int is_poor);
typedef void(__cdecl* RdStatsCallback)(const RdStreamStats* stats);

typedef struct _RdStreamCallbacks {
    RdStageCallback on_stage;
    RdConnectedCallback on_connected;
    RdDisconnectedCallback on_disconnected;
    RdConnectionStatusCallback on_connection_status;
    RdStatsCallback on_stats;
} RdStreamCallbacks;

RD_API uint32_t __cdecl rd_stream_query_supported_codecs(void);
RD_API int __cdecl rd_stream_start(const RdStreamConfig* config, const RdStreamCallbacks* callbacks);
RD_API void __cdecl rd_stream_stop(void);
RD_API void __cdecl rd_stream_resize(int width, int height);
RD_API void __cdecl rd_stream_set_audio_muted(int muted);

RD_API int __cdecl rd_input_mouse_position(short x, short y, short ref_width, short ref_height);
RD_API int __cdecl rd_input_mouse_move(short dx, short dy);
RD_API int __cdecl rd_input_mouse_button(unsigned char action, int button);
RD_API int __cdecl rd_input_scroll_v(short high_res_delta);
RD_API int __cdecl rd_input_scroll_h(short high_res_delta);
RD_API int __cdecl rd_input_key_event(short vk_code, char key_action, char modifiers);
RD_API int __cdecl rd_input_utf8_text(const char* text, unsigned int length);
RD_API int __cdecl rd_input_send_hotkey(int hotkey_id);

#ifdef __cplusplus
}
#endif
