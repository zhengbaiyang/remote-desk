#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <bcrypt.h>
#include <stdlib.h>
#include <string.h>

#include "Limelight-internal.h"

#ifndef NT_SUCCESS
#define NT_SUCCESS(Status) (((NTSTATUS)(Status)) >= 0)
#endif

struct evp_cipher_ctx_st {
    BCRYPT_ALG_HANDLE hAlg;
    BCRYPT_KEY_HANDLE hKey;
    int currentAlgorithm;
    PUCHAR pbKeyObject;
    ULONG cbKeyObject;
    UCHAR iv[16];
    ULONG ivLen;
};

static int addPkcs7PaddingInPlace(unsigned char* plaintext, int plaintextLen) {
    int paddedLength = ROUND_TO_PKCS7_PADDED_LEN(plaintextLen);
    unsigned char paddingByte = (unsigned char)(16 - (plaintextLen % 16));
    memset(&plaintext[plaintextLen], paddingByte, (size_t)(paddedLength - plaintextLen));
    return paddedLength;
}

static bool ensureBcryptAlgorithmAndKey(
    EVP_CIPHER_CTX* c,
    int algorithm,
    const unsigned char* key,
    int keyLength)
{
    if (c->hKey != NULL && c->currentAlgorithm == algorithm) {
        return true;
    }

    if (c->hKey != NULL) {
        BCryptDestroyKey(c->hKey);
        c->hKey = NULL;
    }
    if (c->hAlg != NULL) {
        BCryptCloseAlgorithmProvider(c->hAlg, 0);
        c->hAlg = NULL;
    }
    if (c->pbKeyObject != NULL) {
        free(c->pbKeyObject);
        c->pbKeyObject = NULL;
        c->cbKeyObject = 0;
    }

    if (!NT_SUCCESS(BCryptOpenAlgorithmProvider(&c->hAlg, BCRYPT_AES_ALGORITHM, NULL, 0))) {
        return false;
    }

    LPCWSTR mode = (algorithm == ALGORITHM_AES_GCM) ? BCRYPT_CHAIN_MODE_GCM : BCRYPT_CHAIN_MODE_CBC;
    ULONG modeBytes = (ULONG)((wcslen(mode) + 1) * sizeof(wchar_t));
    if (!NT_SUCCESS(BCryptSetProperty(c->hAlg, BCRYPT_CHAINING_MODE, (PUCHAR)mode, modeBytes, 0))) {
        return false;
    }

    ULONG cbData = 0;
    if (!NT_SUCCESS(BCryptGetProperty(c->hAlg, BCRYPT_OBJECT_LENGTH, (PUCHAR)&c->cbKeyObject, sizeof(ULONG), &cbData, 0))) {
        return false;
    }

    c->pbKeyObject = (PUCHAR)malloc(c->cbKeyObject);
    if (c->pbKeyObject == NULL) {
        return false;
    }

    if (!NT_SUCCESS(BCryptGenerateSymmetricKey(c->hAlg, &c->hKey, c->pbKeyObject, c->cbKeyObject, (PUCHAR)key, (ULONG)keyLength, 0))) {
        free(c->pbKeyObject);
        c->pbKeyObject = NULL;
        c->cbKeyObject = 0;
        return false;
    }

    c->currentAlgorithm = algorithm;
    return true;
}

static bool isReplayedMessage(PPLT_CRYPTO_CONTEXT ctx, int algorithm, int flags, unsigned char replayWindowIndex, unsigned int sequenceNumber) {
    if (algorithm == ALGORITHM_AES_CBC || (flags & CIPHER_FLAG_NO_REPLAY_CHECK)) {
        return false;
    }

    if (replayWindowIndex >= ctx->replayWindowCount) {
        LC_ASSERT(false);
        return true;
    }

    PREPLAY_WINDOW window = &ctx->replayWindows[replayWindowIndex];
    if (!window->decryptedAuthedMessage) {
        return false;
    }

    uint32_t diff = sequenceNumber - window->lastDecryptedAuthedSeqNum;
    if (diff > 0 && diff < 0x80000000) {
        return false;
    }

    if (diff == 0) {
        return true;
    }

    uint32_t offset = window->lastDecryptedAuthedSeqNum - sequenceNumber;
    if (offset >= REPLAY_WINDOW_SIZE) {
        return true;
    }

    int wordIndex = (int)(offset / 64);
    int bitIndex = (int)(offset % 64);
    return (window->replayWindow[wordIndex] & ((uint64_t)1 << bitIndex)) != 0;
}

static void shiftReplayWindow(PREPLAY_WINDOW window, uint32_t shift) {
    if (shift >= REPLAY_WINDOW_SIZE) {
        memset(window->replayWindow, 0, sizeof(window->replayWindow));
        window->replayWindow[0] = 1;
        return;
    }

    int wordShift = (int)(shift / 64);
    int bitShift = (int)(shift % 64);

    for (int i = REPLAY_WINDOW_WORDS - 1; i >= 0; i--) {
        uint64_t val = 0;
        if (i >= wordShift) {
            val = window->replayWindow[i - wordShift] << bitShift;
            if (bitShift > 0 && i > wordShift) {
                val |= window->replayWindow[i - wordShift - 1] >> (64 - bitShift);
            }
        }
        window->replayWindow[i] = val;
    }

    window->replayWindow[0] |= 1;
}

static void recordSuccessfulAuthenticatedDecryption(PPLT_CRYPTO_CONTEXT ctx, int flags, unsigned char replayWindowIndex, unsigned int sequenceNumber) {
    if (flags & CIPHER_FLAG_NO_REPLAY_CHECK) {
        return;
    }

    if (replayWindowIndex >= ctx->replayWindowCount) {
        LC_ASSERT(false);
        return;
    }

    PREPLAY_WINDOW window = &ctx->replayWindows[replayWindowIndex];
    if (!window->decryptedAuthedMessage) {
        window->decryptedAuthedMessage = true;
        window->lastDecryptedAuthedSeqNum = sequenceNumber;
        memset(window->replayWindow, 0, sizeof(window->replayWindow));
        window->replayWindow[0] = 1;
        return;
    }

    uint32_t diff = sequenceNumber - window->lastDecryptedAuthedSeqNum;
    if (diff > 0 && diff < 0x80000000) {
        shiftReplayWindow(window, diff);
        window->lastDecryptedAuthedSeqNum = sequenceNumber;
    }
    else if (diff >= 0x80000000) {
        uint32_t offset = window->lastDecryptedAuthedSeqNum - sequenceNumber;
        LC_ASSERT(offset < REPLAY_WINDOW_SIZE);
        int wordIndex = (int)(offset / 64);
        int bitIndex = (int)(offset % 64);
        window->replayWindow[wordIndex] |= ((uint64_t)1 << bitIndex);
    }
}

bool PltEncryptMessage(PPLT_CRYPTO_CONTEXT ctx, int algorithm, int flags,
                       unsigned char* key, int keyLength,
                       unsigned char* iv, int ivLength,
                       unsigned char* tag, int tagLength,
                       unsigned char* inputData, int inputDataLength,
                       unsigned char* outputData, int* outputDataLength)
{
    if (!ctx || !ctx->ctx || !key || !inputData || !outputData || !outputDataLength) {
        return false;
    }

    EVP_CIPHER_CTX* c = ctx->ctx;
    if (!ensureBcryptAlgorithmAndKey(c, algorithm, key, keyLength)) {
        return false;
    }

    if (algorithm == ALGORITHM_AES_GCM) {
        if (!tag || tagLength <= 0 || !iv || ivLength <= 0) {
            return false;
        }

        BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO authInfo;
        BCRYPT_INIT_AUTH_MODE_INFO(authInfo);
        authInfo.pbNonce = iv;
        authInfo.cbNonce = (ULONG)ivLength;
        authInfo.pbTag = tag;
        authInfo.cbTag = (ULONG)tagLength;

        ULONG cbResult = 0;
        NTSTATUS status = BCryptEncrypt(
            c->hKey,
            inputData,
            (ULONG)inputDataLength,
            &authInfo,
            NULL,
            0,
            outputData,
            (ULONG)inputDataLength,
            &cbResult,
            0);
        if (!NT_SUCCESS(status)) {
            return false;
        }

        ctx->initialized = true;
        *outputDataLength = (int)cbResult;
        return true;
    }
    else if (algorithm == ALGORITHM_AES_CBC) {
        if (!iv || ivLength != 16) {
            return false;
        }

        if (!ctx->initialized || (flags & CIPHER_FLAG_RESET_IV)) {
            memcpy(c->iv, iv, 16);
            c->ivLen = 16;
            ctx->initialized = true;
        }

        ULONG bcryptFlags = 0;
        ULONG maxOutLen = (ULONG)inputDataLength;
        if (flags & CIPHER_FLAG_PAD_TO_BLOCK_SIZE) {
            inputDataLength = addPkcs7PaddingInPlace(inputData, inputDataLength);
            maxOutLen = (ULONG)inputDataLength;
        }
        else if (flags & CIPHER_FLAG_FINISH) {
            bcryptFlags = BCRYPT_BLOCK_PADDING;
            maxOutLen = (ULONG)ROUND_TO_PKCS7_PADDED_LEN(inputDataLength);
        }

        ULONG cbResult = 0;
        NTSTATUS status = BCryptEncrypt(
            c->hKey,
            inputData,
            (ULONG)inputDataLength,
            NULL,
            c->iv,
            c->ivLen,
            outputData,
            maxOutLen,
            &cbResult,
            bcryptFlags);
        if (!NT_SUCCESS(status)) {
            return false;
        }

        *outputDataLength = (int)cbResult;
        return true;
    }

    return false;
}

bool PltDecryptMessage(PPLT_CRYPTO_CONTEXT ctx, int algorithm, int flags,
                       unsigned char replayWindowIndex,
                       unsigned int sequenceNumber,
                       unsigned char* key, int keyLength,
                       unsigned char* iv, int ivLength,
                       unsigned char* tag, int tagLength,
                       unsigned char* inputData, int inputDataLength,
                       unsigned char* outputData, int* outputDataLength)
{
    if (!ctx || !ctx->ctx || !key || !inputData || !outputData || !outputDataLength) {
        return false;
    }

    if (isReplayedMessage(ctx, algorithm, flags, replayWindowIndex, sequenceNumber)) {
        return false;
    }

    EVP_CIPHER_CTX* c = ctx->ctx;
    if (!ensureBcryptAlgorithmAndKey(c, algorithm, key, keyLength)) {
        return false;
    }

    if (algorithm == ALGORITHM_AES_GCM) {
        if (!tag || tagLength <= 0 || !iv || ivLength <= 0) {
            return false;
        }

        BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO authInfo;
        BCRYPT_INIT_AUTH_MODE_INFO(authInfo);
        authInfo.pbNonce = iv;
        authInfo.cbNonce = (ULONG)ivLength;
        authInfo.pbTag = tag;
        authInfo.cbTag = (ULONG)tagLength;

        ULONG cbResult = 0;
        NTSTATUS status = BCryptDecrypt(
            c->hKey,
            inputData,
            (ULONG)inputDataLength,
            &authInfo,
            NULL,
            0,
            outputData,
            (ULONG)inputDataLength,
            &cbResult,
            0);
        if (!NT_SUCCESS(status)) {
            return false;
        }

        ctx->initialized = true;
        recordSuccessfulAuthenticatedDecryption(ctx, flags, replayWindowIndex, sequenceNumber);
        *outputDataLength = (int)cbResult;
        return true;
    }
    else if (algorithm == ALGORITHM_AES_CBC) {
        if (!iv || ivLength != 16) {
            return false;
        }

        if (!ctx->initialized || (flags & CIPHER_FLAG_RESET_IV)) {
            memcpy(c->iv, iv, 16);
            c->ivLen = 16;
            ctx->initialized = true;
        }

        ULONG bcryptFlags = (flags & CIPHER_FLAG_FINISH) ? BCRYPT_BLOCK_PADDING : 0;
        ULONG maxOutLen = (flags & CIPHER_FLAG_FINISH)
            ? (ULONG)ROUND_TO_PKCS7_PADDED_LEN(inputDataLength)
            : (ULONG)inputDataLength;

        ULONG cbResult = 0;
        NTSTATUS status = BCryptDecrypt(
            c->hKey,
            inputData,
            (ULONG)inputDataLength,
            NULL,
            c->iv,
            c->ivLen,
            outputData,
            maxOutLen,
            &cbResult,
            bcryptFlags);
        if (!NT_SUCCESS(status)) {
            return false;
        }

        *outputDataLength = (int)cbResult;
        return true;
    }

    return false;
}

PPLT_CRYPTO_CONTEXT PltCreateCryptoContext(unsigned char replayWindowCount) {
    PPLT_CRYPTO_CONTEXT ctx = (PPLT_CRYPTO_CONTEXT)calloc(
        1,
        sizeof(*ctx) + ((size_t)replayWindowCount * sizeof(REPLAY_WINDOW)));
    if (!ctx) {
        return NULL;
    }

    ctx->replayWindowCount = replayWindowCount;
    ctx->ctx = (EVP_CIPHER_CTX*)calloc(1, sizeof(EVP_CIPHER_CTX));
    if (!ctx->ctx) {
        free(ctx);
        return NULL;
    }

    return ctx;
}

void PltDestroyCryptoContext(PPLT_CRYPTO_CONTEXT ctx) {
    if (!ctx) {
        return;
    }

    if (ctx->ctx) {
        if (ctx->ctx->hKey != NULL) {
            BCryptDestroyKey(ctx->ctx->hKey);
        }
        if (ctx->ctx->hAlg != NULL) {
            BCryptCloseAlgorithmProvider(ctx->ctx->hAlg, 0);
        }
        if (ctx->ctx->pbKeyObject != NULL) {
            free(ctx->ctx->pbKeyObject);
        }
        free(ctx->ctx);
    }

    free(ctx);
}

void PltGenerateRandomData(unsigned char* data, int length) {
    if (data && length > 0) {
        BCryptGenRandom(NULL, data, (ULONG)length, BCRYPT_USE_SYSTEM_PREFERRED_RNG);
    }
}
