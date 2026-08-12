#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <Xinput.h>
#include <cstdint>
#include <cstring>

namespace
{
    constexpr DWORD DeviceNotConnected = ERROR_DEVICE_NOT_CONNECTED;
    constexpr std::uint16_t OrdinalGetState = 2;
    constexpr std::uint16_t OrdinalSetState = 3;
    constexpr std::uint16_t OrdinalGetCapabilities = 4;
    constexpr int MaxPatches = 16;

    using GetStateFn = DWORD(WINAPI*)(DWORD, XINPUT_STATE*);
    using SetStateFn = DWORD(WINAPI*)(DWORD, XINPUT_VIBRATION*);
    using GetCapabilitiesFn = DWORD(WINAPI*)(DWORD, DWORD, XINPUT_CAPABILITIES*);

    struct PatchRecord
    {
        void** slot{};
        void* original{};
        void* replacement{};
    };

    SRWLOCK g_lock = SRWLOCK_INIT;
    PatchRecord g_patches[MaxPatches]{};
    int g_patchCount = 0;
    int g_playerOneMode = 1; // 0 = keyboard/mouse, 1 = XInput
    DWORD g_playerOneSlot = 0;
    DWORD g_playerTwoSlot = 1;
    GetStateFn g_originalGetState = nullptr;
    SetStateFn g_originalSetState = nullptr;
    GetCapabilitiesFn g_originalGetCapabilities = nullptr;
    volatile LONG64 g_blockedCalls = 0;

    bool ShouldHideSlot(DWORD slot)
    {
        if (slot > 3)
            return false;

        if (g_playerOneMode == 0)
            return true;

        return slot != g_playerOneSlot;
    }

    DWORD WINAPI FilterGetState(DWORD slot, XINPUT_STATE* state)
    {
        if (ShouldHideSlot(slot))
        {
            InterlockedIncrement64(&g_blockedCalls);
            if (state != nullptr)
                std::memset(state, 0, sizeof(*state));
            return DeviceNotConnected;
        }

        return g_originalGetState != nullptr
            ? g_originalGetState(slot, state)
            : DeviceNotConnected;
    }

    DWORD WINAPI FilterSetState(DWORD slot, XINPUT_VIBRATION* vibration)
    {
        if (ShouldHideSlot(slot))
        {
            InterlockedIncrement64(&g_blockedCalls);
            return DeviceNotConnected;
        }

        return g_originalSetState != nullptr
            ? g_originalSetState(slot, vibration)
            : DeviceNotConnected;
    }

    DWORD WINAPI FilterGetCapabilities(DWORD slot, DWORD flags, XINPUT_CAPABILITIES* capabilities)
    {
        if (ShouldHideSlot(slot))
        {
            InterlockedIncrement64(&g_blockedCalls);
            if (capabilities != nullptr)
                std::memset(capabilities, 0, sizeof(*capabilities));
            return DeviceNotConnected;
        }

        return g_originalGetCapabilities != nullptr
            ? g_originalGetCapabilities(slot, flags, capabilities)
            : DeviceNotConnected;
    }

    bool WritePointer(void** slot, void* value)
    {
        DWORD oldProtection = 0;
        if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &oldProtection))
            return false;

        InterlockedExchangePointer(slot, value);

        DWORD ignored = 0;
        VirtualProtect(slot, sizeof(void*), oldProtection, &ignored);
        FlushInstructionCache(GetCurrentProcess(), slot, sizeof(void*));
        return true;
    }

    bool AddPatch(void** slot, void* replacement, std::uint16_t ordinal)
    {
        if (g_patchCount >= MaxPatches || slot == nullptr || replacement == nullptr)
            return false;

        void* original = *slot;
        if (original == nullptr || original == replacement)
            return false;

        if (ordinal == OrdinalGetState && g_originalGetState == nullptr)
            g_originalGetState = reinterpret_cast<GetStateFn>(original);
        else if (ordinal == OrdinalSetState && g_originalSetState == nullptr)
            g_originalSetState = reinterpret_cast<SetStateFn>(original);
        else if (ordinal == OrdinalGetCapabilities && g_originalGetCapabilities == nullptr)
            g_originalGetCapabilities = reinterpret_cast<GetCapabilitiesFn>(original);

        if (!WritePointer(slot, replacement))
            return false;

        g_patches[g_patchCount++] = {slot, original, replacement};
        return true;
    }

    int PatchMainExecutable()
    {
        auto* module = reinterpret_cast<std::uint8_t*>(GetModuleHandleW(nullptr));
        if (module == nullptr)
            return 0;

        auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(module);
        if (dos->e_magic != IMAGE_DOS_SIGNATURE)
            return 0;

        auto* nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(module + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC)
            return 0;

        const auto& importDirectory = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
        if (importDirectory.VirtualAddress == 0)
            return 0;

        auto* descriptor = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(module + importDirectory.VirtualAddress);
        for (; descriptor->Name != 0; ++descriptor)
        {
            const char* dllName = reinterpret_cast<const char*>(module + descriptor->Name);
            if (_stricmp(dllName, "XINPUT1_4.dll") != 0)
                continue;

            auto* originalThunk = descriptor->OriginalFirstThunk != 0
                ? reinterpret_cast<IMAGE_THUNK_DATA64*>(module + descriptor->OriginalFirstThunk)
                : reinterpret_cast<IMAGE_THUNK_DATA64*>(module + descriptor->FirstThunk);
            auto* firstThunk = reinterpret_cast<IMAGE_THUNK_DATA64*>(module + descriptor->FirstThunk);

            for (; originalThunk->u1.AddressOfData != 0; ++originalThunk, ++firstThunk)
            {
                if (!IMAGE_SNAP_BY_ORDINAL64(originalThunk->u1.Ordinal))
                    continue;

                std::uint16_t ordinal = static_cast<std::uint16_t>(IMAGE_ORDINAL64(originalThunk->u1.Ordinal));
                void* replacement = nullptr;
                if (ordinal == OrdinalGetState)
                    replacement = reinterpret_cast<void*>(&FilterGetState);
                else if (ordinal == OrdinalSetState)
                    replacement = reinterpret_cast<void*>(&FilterSetState);
                else if (ordinal == OrdinalGetCapabilities)
                    replacement = reinterpret_cast<void*>(&FilterGetCapabilities);

                if (replacement != nullptr)
                    AddPatch(reinterpret_cast<void**>(&firstThunk->u1.Function), replacement, ordinal);
            }
        }

        return g_patchCount;
    }

    void RestorePatches()
    {
        for (int index = g_patchCount - 1; index >= 0; --index)
        {
            PatchRecord& patch = g_patches[index];
            if (patch.slot != nullptr && *patch.slot == patch.replacement)
                WritePointer(patch.slot, patch.original);
            patch = {};
        }

        g_patchCount = 0;
        g_originalGetState = nullptr;
        g_originalSetState = nullptr;
        g_originalGetCapabilities = nullptr;
    }
}

extern "C" __declspec(dllexport) int PragmataInputFilter_Initialize(int playerOneMode, int playerOneSlot, int playerTwoSlot)
{
    AcquireSRWLockExclusive(&g_lock);
    RestorePatches();
    g_playerOneMode = playerOneMode == 0 ? 0 : 1;
    g_playerOneSlot = static_cast<DWORD>(playerOneSlot < 0 ? 0 : playerOneSlot);
    g_playerTwoSlot = static_cast<DWORD>(playerTwoSlot < 0 ? 0 : playerTwoSlot);
    InterlockedExchange64(&g_blockedCalls, 0);
    int result = PatchMainExecutable();
    ReleaseSRWLockExclusive(&g_lock);
    return result;
}

extern "C" __declspec(dllexport) int PragmataInputFilter_EnsureInstalled()
{
    AcquireSRWLockExclusive(&g_lock);
    for (int index = 0; index < g_patchCount; ++index)
    {
        PatchRecord& patch = g_patches[index];
        if (patch.slot != nullptr && *patch.slot != patch.replacement)
        {
            patch.original = *patch.slot;
            WritePointer(patch.slot, patch.replacement);
        }
    }
    int result = g_patchCount;
    ReleaseSRWLockExclusive(&g_lock);
    return result;
}

extern "C" __declspec(dllexport) void PragmataInputFilter_Shutdown()
{
    AcquireSRWLockExclusive(&g_lock);
    RestorePatches();
    ReleaseSRWLockExclusive(&g_lock);
}

extern "C" __declspec(dllexport) long long PragmataInputFilter_GetBlockedCalls()
{
    return InterlockedCompareExchange64(&g_blockedCalls, 0, 0);
}

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID)
{
    return TRUE;
}
