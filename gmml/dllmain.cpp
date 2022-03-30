// thanks to Adaf for helping me figure out the proxy dll loading stuff

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <filesystem>

#include "lib/minhook/include/MinHook.h"

#include <fstream>
#include <iterator>
#include <vector>

#include <iostream>
#include <assert.h>

// .NET CLR hosting
// https://github.com/dotnet/runtime/blob/main/docs/design/features/native-hosting.md
#include "lib/nethost/nethost.h"
#include "lib/nethost/coreclr_delegates.h"
#include "lib/nethost/hostfxr.h"

constexpr auto PROXY_DLL = TEXT("version.dll");
constexpr auto PROXY_MAX_PATH = 260;

#define DLL_PROXY_ORIGINAL(name) original_##name

#define DLL_NAME(name)                \
    FARPROC DLL_PROXY_ORIGINAL(name); \
    void _##name() {                  \
        DLL_PROXY_ORIGINAL(name)();   \
    }
#include "include/proxy.h"
#undef DLL_NAME

std::filesystem::path getSystemDirectory() {
    const auto system_directory(std::make_unique<TCHAR[]>(PROXY_MAX_PATH));
    ::GetSystemDirectory(system_directory.get(), PROXY_MAX_PATH);
    return std::filesystem::path(system_directory.get());
}

bool loadProxy() {
    const auto libPath = getSystemDirectory() / PROXY_DLL;
    const auto lib = LoadLibrary(libPath.c_str());
    if(!lib) return false;

    #define DLL_NAME(name) DLL_PROXY_ORIGINAL(name) = GetProcAddress(lib, ###name);
    #include "include/proxy.h"
    #undef DLL_NAME

    return true;
}

#include "include/gmml.h"
auto settings = gmmlSettings();

void loadSettings(const char* path) {
    std::ifstream input(path);
    auto size = input.gcount();
    auto setting = std::string();

    while(input >> setting) {
        if(setting == "debug") settings.debug = true;
        else if(setting == "console") settings.showConsole = true;
    }

    input.close();
}

using string_t = std::basic_string<char_t>;

namespace {
    // Globals to hold hostfxr exports
    hostfxr_initialize_for_runtime_config_fn init_fptr;
    hostfxr_get_runtime_delegate_fn get_delegate_fptr;
    hostfxr_close_fn close_fptr;

    // Forward declarations
    bool load_hostfxr();
    load_assembly_and_get_function_pointer_fn get_dotnet_load_assembly(const char_t* assembly);
}

unsigned char* (__stdcall* modifyDataManaged)(int, unsigned char*, int*);
bool startClrHost() {
    if(!load_hostfxr()) {
        MessageBoxA(NULL, "Error when loading hostfxr", NULL, MB_OK);
        return false;
    }

    const string_t config_path = TEXT("gmml\\GmmlPatcher.runtimeconfig.json");
    load_assembly_and_get_function_pointer_fn load_assembly_and_get_function_pointer = nullptr;
    load_assembly_and_get_function_pointer = get_dotnet_load_assembly(config_path.c_str());
    if(load_assembly_and_get_function_pointer == nullptr) {
        MessageBoxA(NULL, "Error when starting .NET CLR", NULL, MB_OK);
        return false;
    }

    const string_t dotnetlib_path = TEXT("gmml\\GmmlPatcher.dll");

    // the macros from coreclr_delegates.h don't work for some reason
    // please submit a PR if you manage to fix it
    // __stdcall here is CORECLR_DELEGATE_CALLTYPE
    int rc = load_assembly_and_get_function_pointer(
        dotnetlib_path.c_str(),
        TEXT("GmmlPatcher.Patcher, GmmlPatcher"), // type
        TEXT("ModifyData"), // method
        (const char_t*)-1, // UNMANAGEDCALLERSONLY_METHOD
        nullptr,
        (void**)&modifyDataManaged);
    if(rc != 0 || modifyDataManaged == nullptr) {
        MessageBoxA(NULL, "Error when loading managed assembly", NULL, MB_OK);
        return false;
    }

    if(settings.debug)
        MessageBoxA(NULL, "CLR host loaded", "Info", MB_OK);

    return true;
}

unsigned char* modifyGameData(unsigned char* orig, int* size) {
    if(settings.debug)
        MessageBoxA(NULL, "Loading game data", "Info", MB_OK);

    if(modifyDataManaged == nullptr && !startClrHost())
        return orig;

    return modifyDataManaged(-1, orig, size);
}

unsigned char* modifyAudioGroup(unsigned char* orig, int* size, int number) {
    if(settings.debug)
        MessageBoxA(NULL, (std::string("Loading audio group ") + std::to_string(number)).c_str(), "Info", MB_OK);

    if(modifyDataManaged == nullptr && !startClrHost())
        return orig;

    return modifyDataManaged(number, orig, size);
}

unsigned char* (__cdecl* LoadSave_ReadBundleFile_orig)(char*, int*);
unsigned char* __cdecl LoadSave_ReadBundleFile_hook(char* path, int* size) {
    auto base = reinterpret_cast<uintptr_t>(GetModuleHandle(0));
    auto g_pGameFileName = (char**)(base + 0x5e1dc8);

    auto fsPath = std::filesystem::path(path);
    auto fsPathStem = fsPath.stem().string();
    auto audioGroupName = "audiogroup";

    int* modifySize = size;
    if(size == nullptr) {
        struct stat stat_buf;
        int rc = stat(path, &stat_buf);
        modifySize = (int*)&stat_buf.st_size;
    }

    if(strcmp(path, *g_pGameFileName) == 0) {
        return modifyGameData(LoadSave_ReadBundleFile_orig(path, size), modifySize);
    }
    else if(fsPath.extension() == ".dat" && fsPathStem.starts_with(audioGroupName)) {
        try {
            auto number = std::stoi(fsPathStem.substr(strlen(audioGroupName)));
            return modifyAudioGroup(LoadSave_ReadBundleFile_orig(path, size), modifySize, number);
        }
        catch(std::invalid_argument) { }
    }

    return LoadSave_ReadBundleFile_orig(path, size);
}

#pragma warning(push)
#pragma warning(disable : 26812)
bool loadModLoader() {
    loadSettings("gmml.cfg");
    if(settings.showConsole) AllocConsole();

    auto base = reinterpret_cast<uintptr_t>(GetModuleHandle(0));

    if(MH_Initialize() != MH_OK) return false;
    if(MH_CreateHook(reinterpret_cast<void*>(base + 0x8d460), LoadSave_ReadBundleFile_hook,
        reinterpret_cast<void**>(&LoadSave_ReadBundleFile_orig)) != MH_OK)
        return false;
    if(MH_EnableHook(MH_ALL_HOOKS) != MH_OK) return false;

    return true;
}
#pragma warning(pop)

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved) {
    switch(ul_reason_for_call) {
        case DLL_PROCESS_ATTACH:
            if(!loadProxy()) {
                MessageBoxA(NULL, "Failed to load original dll", NULL, MB_OK);
                return FALSE;
            }
            if(!loadModLoader()) {
                MessageBoxA(NULL, "Failed to load mod loader", NULL, MB_OK);
                return FALSE;
            }
            //MessageBoxA(NULL, "ml loaded", "succ", MB_OK);
            break;
        case DLL_THREAD_ATTACH:
        case DLL_THREAD_DETACH:
        case DLL_PROCESS_DETACH:
            break;
    }

    return TRUE;
}

/********************************************************************************************
 * Function used to load and activate .NET Core
 ********************************************************************************************/

namespace {
    // Forward declarations
    void* load_library(const char_t*);
    void* get_export(void*, const char*);

    void* load_library(const char_t* path) {
        HMODULE h = ::LoadLibraryW(path);
        assert(h != nullptr);
        return (void*)h;
    }
    void* get_export(void* h, const char* name) {
        void* f = ::GetProcAddress((HMODULE)h, name);
        assert(f != nullptr);
        return f;
    }

    // <SnippetLoadHostFxr>
    // Using the nethost library, discover the location of hostfxr and get exports
    bool load_hostfxr() {
        // Pre-allocate a large buffer for the path to hostfxr
        char_t buffer[MAX_PATH];
        size_t buffer_size = sizeof(buffer) / sizeof(char_t);
        int rc = get_hostfxr_path(buffer, &buffer_size, nullptr);
        if(rc != 0)
            return false;

        // Load hostfxr and get desired exports
        void* lib = load_library(buffer);
        init_fptr = (hostfxr_initialize_for_runtime_config_fn)get_export(lib, "hostfxr_initialize_for_runtime_config");
        get_delegate_fptr = (hostfxr_get_runtime_delegate_fn)get_export(lib, "hostfxr_get_runtime_delegate");
        close_fptr = (hostfxr_close_fn)get_export(lib, "hostfxr_close");

        return (init_fptr && get_delegate_fptr && close_fptr);
    }
    // </SnippetLoadHostFxr>

    // <SnippetInitialize>
    // Load and initialize .NET Core and get desired function pointer for scenario
    load_assembly_and_get_function_pointer_fn get_dotnet_load_assembly(const char_t* config_path) {
        // Load .NET Core
        void* load_assembly_and_get_function_pointer = nullptr;
        hostfxr_handle cxt = nullptr;
        int rc = init_fptr(config_path, nullptr, &cxt);
        if(rc != 0 || cxt == nullptr) {
            std::cerr << "Init failed: " << std::hex << std::showbase << rc << std::endl;
            close_fptr(cxt);
            return nullptr;
        }

        // Get the load assembly function pointer
        rc = get_delegate_fptr(
            cxt,
            hdt_load_assembly_and_get_function_pointer,
            &load_assembly_and_get_function_pointer);
        if(rc != 0 || load_assembly_and_get_function_pointer == nullptr)
            std::cerr << "Get delegate failed: " << std::hex << std::showbase << rc << std::endl;

        close_fptr(cxt);
        return (load_assembly_and_get_function_pointer_fn)load_assembly_and_get_function_pointer;
    }
    // </SnippetInitialize>
}
