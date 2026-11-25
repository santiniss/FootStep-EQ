#include "StrikeEqApoInterfaces.h"
#include "StrikeEqApoDsp.h"

#include <atomic>
#include <cstring>
#include <cwchar>
#include <new>

namespace {

HMODULE g_hModule = nullptr;
std::atomic<LONG> g_dllRefCount{0};

bool IsIeeeFloatFormat(const WAVEFORMATEX* wfx) noexcept {
    if (!wfx) return false;
    if (wfx->nChannels == 0 || wfx->nChannels > 8) return false;
    if (wfx->wBitsPerSample != 32) return false;

    if (wfx->wFormatTag == WAVE_FORMAT_IEEE_FLOAT) return true;
    if (wfx->wFormatTag == WAVE_FORMAT_EXTENSIBLE && wfx->cbSize >= 22) {
        const auto* ext = reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(wfx);
        return IsEqualGUID(ext->SubFormat, STRIKEEQ_KSDATAFORMAT_SUBTYPE_IEEE_FLOAT) != 0;
    }
    return false;
}

class StrikeEqApo final
    : public IAudioProcessingObject
    , public IAudioProcessingObjectRT
    , public IAudioProcessingObjectConfiguration
    , public IAudioSystemEffects2
{
public:
    StrikeEqApo() noexcept {
        g_dllRefCount.fetch_add(1, std::memory_order_relaxed);
        OpenSharedMemory();
    }

    ~StrikeEqApo() noexcept {
        CloseSharedMemory();
        g_dllRefCount.fetch_sub(1, std::memory_order_relaxed);
    }

    // ---- IUnknown ----
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppvObject) override {
        if (!ppvObject) return E_POINTER;
        *ppvObject = nullptr;

        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IAudioProcessingObject)) {
            *ppvObject = static_cast<IAudioProcessingObject*>(this);
        } else if (IsEqualIID(riid, IID_IAudioProcessingObjectRT)) {
            *ppvObject = static_cast<IAudioProcessingObjectRT*>(this);
        } else if (IsEqualIID(riid, IID_IAudioProcessingObjectConfiguration)) {
            *ppvObject = static_cast<IAudioProcessingObjectConfiguration*>(this);
        } else if (IsEqualIID(riid, IID_IAudioSystemEffects)) {
            *ppvObject = static_cast<IAudioSystemEffects*>(this);
        } else if (IsEqualIID(riid, IID_IAudioSystemEffects2)) {
            *ppvObject = static_cast<IAudioSystemEffects2*>(this);
        } else {
            return E_NOINTERFACE;
        }

        AddRef();
        return S_OK;
    }

    ULONG STDMETHODCALLTYPE AddRef() override {
        return static_cast<ULONG>(_refCount.fetch_add(1, std::memory_order_relaxed) + 1);
    }

    ULONG STDMETHODCALLTYPE Release() override {
        LONG count = _refCount.fetch_sub(1, std::memory_order_acq_rel) - 1;
        if (count == 0) {
            delete this;
            return 0;
        }
        return static_cast<ULONG>(count);
    }

    // ---- IAudioProcessingObject ----
    HRESULT STDMETHODCALLTYPE Reset() override {
        _dsp.Reset();
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetLatency(HNSTIME* pTime) override {
        if (!pTime) return E_POINTER;
        *pTime = 0; // Zero lookahead latency!
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE GetRegistrationProperties(APO_REG_PROPERTIES** ppRegProps) override {
        if (!ppRegProps) return E_POINTER;

        // Allocate space for 5 interface IIDs (struct already contains 1 IID)
        constexpr UINT32 numInterfaces = 5;
        SIZE_T allocBytes = sizeof(APO_REG_PROPERTIES) + (numInterfaces - 1) * sizeof(IID);
        auto* props = static_cast<APO_REG_PROPERTIES*>(CoTaskMemAlloc(allocBytes));
        if (!props) return E_OUTOFMEMORY;

        std::memset(props, 0, allocBytes);
        props->clsid = CLSID_StrikeEqApo;
        props->Flags = static_cast<APO_FLAG>(
            APO_FLAG_INPLACE |
            APO_FLAG_SAMPLESPERFRAME_MUST_MATCH |
            APO_FLAG_FRAMESPERSECOND_MUST_MATCH |
            APO_FLAG_BITSPERSAMPLE_MUST_MATCH);

        wcscpy_s(props->szFriendlyName, 256, L"FootStep EQ Audio APO");
        wcscpy_s(props->szCopyrightInfo, 256, L"FootStep EQ Gaming Audio");
        props->u32MajorVersion = 1;
        props->u32MinorVersion = 0;
        props->u32MinInputConnections = 1;
        props->u32MaxInputConnections = 1;
        props->u32MinOutputConnections = 1;
        props->u32MaxOutputConnections = 1;
        props->u32MaxInstances = 0xFFFFFFFF;
        props->u32NumAPOInterfaces = numInterfaces;
        props->iidAPOInterfaceList[0] = IID_IAudioProcessingObject;
        props->iidAPOInterfaceList[1] = IID_IAudioProcessingObjectRT;
        props->iidAPOInterfaceList[2] = IID_IAudioProcessingObjectConfiguration;
        props->iidAPOInterfaceList[3] = IID_IAudioSystemEffects;
        props->iidAPOInterfaceList[4] = IID_IAudioSystemEffects2;

        *ppRegProps = props;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE Initialize(UINT32 /*cbDataSize*/, BYTE* /*pbyData*/) override {
        _initialized = true;
        OpenSharedMemory();
        SyncSharedParameters(true);
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE IsInputFormatSupported(
        IAudioMediaType* pOppositeFormat,
        IAudioMediaType* pRequestedInputFormat,
        IAudioMediaType** ppSupportedInputFormat) override
    {
        if (!pRequestedInputFormat || !ppSupportedInputFormat) return E_POINTER;
        *ppSupportedInputFormat = nullptr;

        const WAVEFORMATEX* reqWfx = pRequestedInputFormat->GetAudioFormat();
        if (!IsIeeeFloatFormat(reqWfx)) {
            return APOERR_FORMAT_NOT_SUPPORTED;
        }

        if (pOppositeFormat) {
            const WAVEFORMATEX* oppWfx = pOppositeFormat->GetAudioFormat();
            if (!oppWfx || oppWfx->nChannels != reqWfx->nChannels ||
                oppWfx->nSamplesPerSec != reqWfx->nSamplesPerSec ||
                oppWfx->wBitsPerSample != reqWfx->wBitsPerSample) {
                return APOERR_FORMAT_NOT_SUPPORTED;
            }
        }

        *ppSupportedInputFormat = pRequestedInputFormat;
        pRequestedInputFormat->AddRef();
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE IsOutputFormatSupported(
        IAudioMediaType* pOppositeFormat,
        IAudioMediaType* pRequestedOutputFormat,
        IAudioMediaType** ppSupportedOutputFormat) override
    {
        return IsInputFormatSupported(pOppositeFormat, pRequestedOutputFormat, ppSupportedOutputFormat);
    }

    HRESULT STDMETHODCALLTYPE GetInputChannelCount(UINT32* pu32ChannelCount) override {
        if (!pu32ChannelCount) return E_POINTER;
        *pu32ChannelCount = _channels;
        return S_OK;
    }

    // ---- IAudioProcessingObjectConfiguration ----
    HRESULT STDMETHODCALLTYPE LockForProcess(
        UINT32 u32NumInputConnections,
        APO_CONNECTION_DESCRIPTOR** ppInputConnections,
        UINT32 u32NumOutputConnections,
        APO_CONNECTION_DESCRIPTOR** ppOutputConnections) override
    {
        if (_locked) return APOERR_APO_LOCKED;
        if (u32NumInputConnections != 1 || u32NumOutputConnections != 1)
            return APOERR_NUM_CONNECTIONS_INVALID;
        if (!ppInputConnections || !ppOutputConnections || !ppInputConnections[0] || !ppOutputConnections[0])
            return E_POINTER;
        if (!ppInputConnections[0]->pFormat || !ppOutputConnections[0]->pFormat)
            return APOERR_INVALID_CONNECTION_FORMAT;

        const WAVEFORMATEX* inWfx = ppInputConnections[0]->pFormat->GetAudioFormat();
        const WAVEFORMATEX* outWfx = ppOutputConnections[0]->pFormat->GetAudioFormat();
        if (!IsIeeeFloatFormat(inWfx) || !IsIeeeFloatFormat(outWfx))
            return APOERR_FORMAT_NOT_SUPPORTED;
        if (inWfx->nChannels != outWfx->nChannels || inWfx->nSamplesPerSec != outWfx->nSamplesPerSec)
            return APOERR_FORMAT_NOT_SUPPORTED;

        _sampleRate = inWfx->nSamplesPerSec;
        _channels = inWfx->nChannels;
        _dsp.SetFormat(_sampleRate, _channels);

        OpenSharedMemory();
        SyncSharedParameters(true);

        _locked = true;
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE UnlockForProcess() override {
        if (!_locked) return APOERR_ALREADY_UNLOCKED;
        _locked = false;
        return S_OK;
    }

    // ---- IAudioProcessingObjectRT (Real-Time Audio Thread: NO allocations/locks!) ----
    void STDMETHODCALLTYPE APOProcess(
        UINT32 u32NumInputConnections,
        APO_CONNECTION_PROPERTY** ppInputConnections,
        UINT32 u32NumOutputConnections,
        APO_CONNECTION_PROPERTY** ppOutputConnections) override
    {
        if (!_locked || u32NumInputConnections < 1 || u32NumOutputConnections < 1) return;
        const auto* inConn = ppInputConnections[0];
        auto* outConn = ppOutputConnections[0];
        if (!inConn || !outConn) return;

        UINT32 frames = inConn->u32ValidFrameCount;
        outConn->u32ValidFrameCount = frames;

        const auto* inBuf = reinterpret_cast<const float*>(inConn->pBuffer);
        auto* outBuf = reinterpret_cast<float*>(outConn->pBuffer);

        if (inConn->u32BufferFlags == BUFFER_SILENT || frames == 0 || !inBuf || !outBuf) {
            outConn->u32BufferFlags = BUFFER_SILENT;
            if (outBuf && frames > 0) {
                std::memset(outBuf, 0, static_cast<size_t>(frames) * _channels * sizeof(float));
            }
            WriteTelemetry(0.0f, 0.0f, 0.0f);
            return;
        }

        SyncSharedParameters(false);

        _dsp.ProcessBlock(inBuf, outBuf, frames, _channels);
        outConn->u32BufferFlags = BUFFER_VALID;

        WriteTelemetry(_dsp.LastGainReductionDb(), _dsp.LastPeakL(), _dsp.LastPeakR());
    }

    UINT32 STDMETHODCALLTYPE CalcInputFrames(UINT32 u32OutputFrameCount) override {
        return u32OutputFrameCount;
    }

    UINT32 STDMETHODCALLTYPE CalcOutputFrames(UINT32 u32InputFrameCount) override {
        return u32InputFrameCount;
    }

    // ---- IAudioSystemEffects2 ----
    HRESULT STDMETHODCALLTYPE GetEffectsList(LPGUID* ppEffectsIds, UINT* pcEffects, HANDLE /*Event*/) override {
        if (!ppEffectsIds || !pcEffects) return E_POINTER;
        auto* guids = static_cast<GUID*>(CoTaskMemAlloc(sizeof(GUID)));
        if (!guids) return E_OUTOFMEMORY;
        guids[0] = STRIKEEQ_AUDIO_EFFECT_TYPE_EQUALIZER;
        *ppEffectsIds = guids;
        *pcEffects = 1;
        return S_OK;
    }

private:
    void OpenSharedMemory() noexcept {
        if (_sharedState) return;

        HANDLE hFile = CreateFileW(
            STRIKEEQ_SHM_FILE_PATH,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);

        if (hFile == INVALID_HANDLE_VALUE) return;

        HANDLE hMap = CreateFileMappingW(
            hFile,
            nullptr,
            PAGE_READWRITE,
            0,
            sizeof(StrikeEqSharedState),
            nullptr);

        if (!hMap) {
            CloseHandle(hFile);
            return;
        }

        void* view = MapViewOfFile(hMap, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(StrikeEqSharedState));
        if (!view) {
            CloseHandle(hMap);
            CloseHandle(hFile);
            return;
        }

        _hShmFile = hFile;
        _hShmMap = hMap;
        _sharedState = static_cast<StrikeEqSharedState*>(view);
        _lastSequence = 0xFFFFFFFF;
    }

    void CloseSharedMemory() noexcept {
        if (_sharedState) {
            UnmapViewOfFile(_sharedState);
            _sharedState = nullptr;
        }
        if (_hShmMap) {
            CloseHandle(_hShmMap);
            _hShmMap = nullptr;
        }
        if (_hShmFile != INVALID_HANDLE_VALUE) {
            CloseHandle(_hShmFile);
            _hShmFile = INVALID_HANDLE_VALUE;
        }
    }

    void SyncSharedParameters(bool force) noexcept {
        if (!_sharedState) return;
        if (_sharedState->magic != STRIKEEQ_SHM_MAGIC || _sharedState->version != STRIKEEQ_SHM_VERSION)
            return;

        uint32_t seq = __atomic_load_n(&_sharedState->sequence, __ATOMIC_ACQUIRE);
        if (force || seq != _lastSequence) {
            StrikeEqSharedState snapshot;
            std::memcpy(&snapshot, _sharedState, sizeof(StrikeEqSharedState));
            _dsp.ApplyState(snapshot);
            _lastSequence = seq;
        }
    }

    void WriteTelemetry(float grDb, float peakL, float peakR) noexcept {
        if (!_sharedState || _sharedState->magic != STRIKEEQ_SHM_MAGIC) return;
        _sharedState->apoSampleRate = _sampleRate;
        _sharedState->apoChannelCount = _channels;
        _sharedState->apoGainReductionDb = grDb;
        _sharedState->apoOutputPeakL = peakL;
        _sharedState->apoOutputPeakR = peakR;
        __atomic_add_fetch(&_sharedState->apoActiveHeartbeat, 1u, __ATOMIC_RELEASE);
    }

    std::atomic<LONG> _refCount{1};
    bool _initialized = false;
    bool _locked = false;
    uint32_t _sampleRate = 48000;
    uint32_t _channels = 2;
    uint32_t _lastSequence = 0xFFFFFFFF;

    HANDLE _hShmFile = INVALID_HANDLE_VALUE;
    HANDLE _hShmMap = nullptr;
    StrikeEqSharedState* _sharedState = nullptr;

    strikeeq::ApoDspEngine _dsp;
};

class StrikeEqClassFactory final : public IClassFactory {
public:
    StrikeEqClassFactory() noexcept {
        g_dllRefCount.fetch_add(1, std::memory_order_relaxed);
    }

    ~StrikeEqClassFactory() noexcept {
        g_dllRefCount.fetch_sub(1, std::memory_order_relaxed);
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppvObject) override {
        if (!ppvObject) return E_POINTER;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IClassFactory)) {
            *ppvObject = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppvObject = nullptr;
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() override {
        return static_cast<ULONG>(_refCount.fetch_add(1, std::memory_order_relaxed) + 1);
    }

    ULONG STDMETHODCALLTYPE Release() override {
        LONG count = _refCount.fetch_sub(1, std::memory_order_acq_rel) - 1;
        if (count == 0) {
            delete this;
            return 0;
        }
        return static_cast<ULONG>(count);
    }

    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppvObject) override {
        if (!ppvObject) return E_POINTER;
        *ppvObject = nullptr;
        if (pUnkOuter != nullptr) return CLASS_E_NOAGGREGATION;

        auto* apo = new (std::nothrow) StrikeEqApo();
        if (!apo) return E_OUTOFMEMORY;

        HRESULT hr = apo->QueryInterface(riid, ppvObject);
        apo->Release();
        return hr;
    }

    HRESULT STDMETHODCALLTYPE LockServer(BOOL fLock) override {
        if (fLock) g_dllRefCount.fetch_add(1, std::memory_order_relaxed);
        else g_dllRefCount.fetch_sub(1, std::memory_order_relaxed);
        return S_OK;
    }

private:
    std::atomic<LONG> _refCount{1};
};

HRESULT WriteRegString(HKEY root, const wchar_t* subKey, const wchar_t* valueName, const wchar_t* data) {
    HKEY hKey = nullptr;
    LONG res = RegCreateKeyExW(root, subKey, 0, nullptr, REG_OPTION_NON_VOLATILE, KEY_WRITE, nullptr, &hKey, nullptr);
    if (res != ERROR_SUCCESS) return HRESULT_FROM_WIN32(res);
    res = RegSetValueExW(hKey, valueName, 0, REG_SZ,
        reinterpret_cast<const BYTE*>(data), static_cast<DWORD>((wcslen(data) + 1) * sizeof(wchar_t)));
    RegCloseKey(hKey);
    return HRESULT_FROM_WIN32(res);
}

HRESULT WriteRegDword(HKEY root, const wchar_t* subKey, const wchar_t* valueName, DWORD data) {
    HKEY hKey = nullptr;
    LONG res = RegCreateKeyExW(root, subKey, 0, nullptr, REG_OPTION_NON_VOLATILE, KEY_WRITE, nullptr, &hKey, nullptr);
    if (res != ERROR_SUCCESS) return HRESULT_FROM_WIN32(res);
    res = RegSetValueExW(hKey, valueName, 0, REG_DWORD, reinterpret_cast<const BYTE*>(&data), sizeof(DWORD));
    RegCloseKey(hKey);
    return HRESULT_FROM_WIN32(res);
}

} // namespace

// ============================================================================
// Standard COM & APO DLL Exports
// ============================================================================
BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID /*lpReserved*/) {
    if (ul_reason_for_call == DLL_PROCESS_ATTACH) {
        g_hModule = hModule;
        DisableThreadLibraryCalls(hModule);
    }
    return TRUE;
}

extern "C" HRESULT __stdcall DllCanUnloadNow() {
    return g_dllRefCount.load(std::memory_order_acquire) == 0 ? S_OK : S_FALSE;
}

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv) {
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (!IsEqualCLSID(rclsid, CLSID_StrikeEqApo)) return CLASS_E_CLASSNOTAVAILABLE;

    auto* factory = new (std::nothrow) StrikeEqClassFactory();
    if (!factory) return E_OUTOFMEMORY;

    HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

extern "C" HRESULT __stdcall DllRegisterServer() {
    wchar_t dllPath[MAX_PATH]{};
    if (GetModuleFileNameW(g_hModule, dllPath, MAX_PATH) == 0)
        return HRESULT_FROM_WIN32(GetLastError());

    // 1. Register COM CLSID InprocServer32
    wchar_t clsidKey[160];
    swprintf_s(clsidKey, 160, L"CLSID\\%s", STRIKEEQ_APO_CLSID_STR);
    HRESULT hr = WriteRegString(HKEY_CLASSES_ROOT, clsidKey, nullptr, L"FootStep EQ Audio APO");
    if (FAILED(hr)) return hr;

    wchar_t inprocKey[192];
    swprintf_s(inprocKey, 192, L"CLSID\\%s\\InprocServer32", STRIKEEQ_APO_CLSID_STR);
    hr = WriteRegString(HKEY_CLASSES_ROOT, inprocKey, nullptr, dllPath);
    if (FAILED(hr)) return hr;
    hr = WriteRegString(HKEY_CLASSES_ROOT, inprocKey, L"ThreadingModel", L"Both");
    if (FAILED(hr)) return hr;

    // 2. Register in Windows AudioEngine AudioProcessingObjects registry
    wchar_t apoKey[256];
    swprintf_s(apoKey, 256, L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Audio\\AudioEngine\\AudioProcessingObjects\\%s", STRIKEEQ_APO_CLSID_STR);
    WriteRegString(HKEY_LOCAL_MACHINE, apoKey, L"FriendlyName", L"FootStep EQ Audio APO");
    WriteRegString(HKEY_LOCAL_MACHINE, apoKey, L"Copyright", L"FootStep EQ Gaming Audio");
    WriteRegDword(HKEY_LOCAL_MACHINE, apoKey, L"MajorVersion", 1);
    WriteRegDword(HKEY_LOCAL_MACHINE, apoKey, L"MinorVersion", 0);
    WriteRegDword(HKEY_LOCAL_MACHINE, apoKey, L"Flags",
        APO_FLAG_INPLACE | APO_FLAG_SAMPLESPERFRAME_MUST_MATCH | APO_FLAG_FRAMESPERSECOND_MUST_MATCH | APO_FLAG_BITSPERSAMPLE_MUST_MATCH);
    WriteRegDword(HKEY_LOCAL_MACHINE, apoKey, L"MinInputConnections", 1);
    WriteRegDword(HKEY_LOCAL_MACHINE, apoKey, L"MaxInputConnections", 1);
    WriteRegDword(HKEY_LOCAL_MACHINE, apoKey, L"MinOutputConnections", 1);
    WriteRegDword(HKEY_LOCAL_MACHINE, apoKey, L"MaxOutputConnections", 1);
    WriteRegDword(HKEY_LOCAL_MACHINE, apoKey, L"MaxInstances", 0xFFFFFFFF);
    WriteRegDword(HKEY_LOCAL_MACHINE, apoKey, L"NumAPOInterfaces", 5);
    WriteRegString(HKEY_LOCAL_MACHINE, apoKey, L"APOInterface0", L"{FD7F2B29-24D0-4B5C-B177-592C39F9CA10}");
    WriteRegString(HKEY_LOCAL_MACHINE, apoKey, L"APOInterface1", L"{9E1D6A6D-DDBC-4E95-A4C7-AD64BA37846C}");
    WriteRegString(HKEY_LOCAL_MACHINE, apoKey, L"APOInterface2", L"{0E5ED805-ABA6-49C3-8F9A-2B8C889C4FA8}");
    WriteRegString(HKEY_LOCAL_MACHINE, apoKey, L"APOInterface3", L"{5FA00F27-ADD6-499A-8A9D-6B98521FA75B}");
    WriteRegString(HKEY_LOCAL_MACHINE, apoKey, L"APOInterface4", L"{BAFE99D2-7436-44CE-9E0E-4D89AFBFFF56}");

    return S_OK;
}

extern "C" HRESULT __stdcall DllUnregisterServer() {
    wchar_t clsidKey[160];
    swprintf_s(clsidKey, 160, L"CLSID\\%s", STRIKEEQ_APO_CLSID_STR);
    RegDeleteTreeW(HKEY_CLASSES_ROOT, clsidKey);

    wchar_t apoKey[256];
    swprintf_s(apoKey, 256, L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Audio\\AudioEngine\\AudioProcessingObjects\\%s", STRIKEEQ_APO_CLSID_STR);
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, apoKey);
    return S_OK;
}

// ============================================================================
// Direct C Self-Test Export (for automated C# DSP parity verification)
// ============================================================================
extern "C" int __stdcall StrikeEqApo_SelfTestProcess(
    const StrikeEqSharedState* state,
    float* interleavedStereo,
    uint32_t frameCount,
    uint32_t sampleRate,
    float* outGainReductionDb)
{
    if (!state || !interleavedStereo || frameCount == 0) return -1;
    strikeeq::ApoDspEngine dsp;
    dsp.SetFormat(sampleRate, 2);
    dsp.ApplyState(*state);
    dsp.ProcessBlock(interleavedStereo, interleavedStereo, frameCount, 2);
    if (outGainReductionDb) *outGainReductionDb = dsp.LastGainReductionDb();
    return 0;
}
