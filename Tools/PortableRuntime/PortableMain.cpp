#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shlobj.h>
#include <string>
#include <cstring>

using KnownFolderPath = HRESULT (WINAPI *)(REFKNOWNFOLDERID, DWORD, HANDLE, PWSTR*);
static KnownFolderPath originalKnownFolderPath;
static std::wstring portableRoot;

static HRESULT WINAPI PortableKnownFolderPath(REFKNOWNFOLDERID id, DWORD flags, HANDLE token, PWSTR* path)
{
    // Patch only UnityPlayer's import, and only the current user's LocalLow.
    // Other known folders and other modules retain normal Windows behavior.
    if (id != FOLDERID_LocalAppDataLow || token != nullptr)
        return originalKnownFolderPath(id, flags, token, path);
    if (!path) return E_INVALIDARG;
    *path = nullptr;
    const size_t bytes = (portableRoot.size() + 1) * sizeof(wchar_t);
    auto copy = static_cast<wchar_t*>(CoTaskMemAlloc(bytes));
    if (!copy) return E_OUTOFMEMORY;
    memcpy(copy, portableRoot.c_str(), bytes);
    *path = copy;
    return S_OK;
}

static bool RedirectUnityLocalLow(HMODULE player)
{
    // Use the PE import name instead of Unity-version-specific code offsets.
    // The module is loaded but UnityMain has not started any engine threads.
    auto base = reinterpret_cast<unsigned char*>(player);
    auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    auto nt = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
    const DWORD imports = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress;
    if (!imports) return false;
    for (auto library = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + imports); library->Name; ++library)
    {
        if (!library->OriginalFirstThunk) continue;
        auto names = reinterpret_cast<IMAGE_THUNK_DATA*>(base + library->OriginalFirstThunk);
        auto slots = reinterpret_cast<IMAGE_THUNK_DATA*>(base + library->FirstThunk);
        for (; names->u1.AddressOfData; ++names, ++slots)
        {
            if (IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal)) continue;
            auto name = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base + names->u1.AddressOfData);
            if (strcmp(reinterpret_cast<const char*>(name->Name), "SHGetKnownFolderPath") != 0) continue;
            DWORD previousProtection;
            if (!VirtualProtect(&slots->u1.Function, sizeof(void*), PAGE_READWRITE, &previousProtection)) return false;
            originalKnownFolderPath = reinterpret_cast<KnownFolderPath>(slots->u1.Function);
            InterlockedExchangePointer(reinterpret_cast<void* volatile*>(&slots->u1.Function),
                reinterpret_cast<void*>(&PortableKnownFolderPath));
            DWORD ignored;
            return VirtualProtect(&slots->u1.Function, sizeof(void*), previousProtection, &ignored) != FALSE;
        }
    }
    return false;
}

static bool EnsureDirectory(const std::wstring& path)
{
    if (CreateDirectoryW(path.c_str(), nullptr)) return true;
    const DWORD attributes = GetFileAttributesW(path.c_str());
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
}

static int StartupError()
{
    MessageBoxW(nullptr,
        L"MateEngine could not initialize portable storage.\n"
        L"Check that the program files are complete and the program directory is writable.",
        L"MateEngine", MB_OK | MB_ICONERROR);
    return 1;
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR commandLine, int show)
{
    wchar_t executable[32768];
    const DWORD count = GetModuleFileNameW(nullptr, executable, _countof(executable));
    if (!count || count == _countof(executable)) return StartupError();
    std::wstring folder(executable, count);
    folder.resize(folder.find_last_of(L"\\/") + 1);
    const std::wstring userData = folder + L"UserData";
    const std::wstring cache = userData + L"\\Cache";
    const std::wstring temporary = cache + L"\\Temp";
    portableRoot = userData + L"\\Unity";
    if (!EnsureDirectory(userData) || !EnsureDirectory(portableRoot) ||
        !EnsureDirectory(cache) || !EnsureDirectory(temporary)) return StartupError();

    // Process-local only. Unity and its crash-handler child inherit this before
    // they compute native temporary paths; no user/system environment is edited.
    if (!SetEnvironmentVariableW(L"TEMP", temporary.c_str()) ||
        !SetEnvironmentVariableW(L"TMP", temporary.c_str())) return StartupError();

    auto player = LoadLibraryExW((folder + L"UnityPlayer.dll").c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!player || !RedirectUnityLocalLow(player)) return StartupError();
    using UnityMain = int (WINAPI *)(HINSTANCE, HINSTANCE, PWSTR, int);
    auto main = reinterpret_cast<UnityMain>(GetProcAddress(player, "UnityMain"));
    if (!main) return StartupError();
    // Keep the same process, executable name, command line and Windows resources.
    // Unity owns shutdown; this module stays loaded for the entire process.
    return main(instance, previous, commandLine, show);
}
