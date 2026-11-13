// PL/SQL Developer plug-in shim for "PL/Pilot" (technical name: PlsqlDevAgent).
//
// This is the DLL that PL/SQL Developer loads. It is deliberately tiny and has no
// dependency on .NET: it answers IdentifyPlugIn / CreateMenuItem natively, records
// all IDE callback addresses, and only loads the .NET Framework 4.8 runtime (CLR v4)
// lazily on the first menu click. If the managed part fails to load, the IDE keeps
// working and the user gets an error message instead of a crash.
//
// The managed implementation lives in <this dll's folder>\PlsqlDevAgent\PlsqlDevAgent.Core.dll
// (a sub-folder, so PL/SQL Developer does not try to load those DLLs as plug-ins).

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <metahost.h>
#include <xmmintrin.h>
#include <stdio.h>
#include <string.h>
#include <wchar.h>
#include <stdint.h>

#pragma comment(lib, "mscoree.lib")
#pragma comment(lib, "user32.lib")

#define EXPORT extern "C" __declspec(dllexport)

// ---- menu indices (must match PlsqlDevAgent.Core/Bootstrap.cs) -------------------
enum MenuIndex
{
    MI_GROUP = 1,
    MI_CHAT = 2,
    MI_ASK_SELECTION = 3,
    MI_SETTINGS = 4,
    MI_POPUP_EDITOR = 20,
    MI_POPUP_OBJECT = 21,
};

// ---- events forwarded to managed code --------------------------------------------
enum AgentEvent
{
    EV_MENU_CLICK = 1,
    EV_ACTIVATE = 2,
    EV_DEACTIVATE = 3,
    EV_DESTROY = 4,
    EV_CONNECTION_CHANGE = 5,
    EV_WINDOW_CHANGE = 6,
    EV_CAN_CLOSE = 7,
    EV_SELF_TEST = 8,
};

typedef intptr_t(__cdecl* DispatchFn)(int evt, intptr_t a, intptr_t b);

static HMODULE g_module = nullptr;
static int g_pluginId = 0;
static void* g_callbacks[1024];
static DispatchFn g_dispatch = nullptr;
static bool g_clrTried = false;
static wchar_t g_dir[MAX_PATH];
static const char* g_description = "PL/Pilot (AI coding agent for PL/SQL Developer)";

// ---- IDE callbacks used natively ----------------------------------------------------
typedef int(__cdecl* SYS_Version_t)();
typedef char*(__cdecl* SYS_RootDir_t)();
typedef int(__cdecl* IDE_GetWindowHandle_t)();
typedef void(__cdecl* IDE_CreatePopupItem_t)(int id, int index, const char* name, const char* objectType);
typedef void(__cdecl* IDE_CreateToolButton_t)(int id, int index, const char* name, const char* bitmapFile, void* bitmapHandle);
typedef BOOL(__cdecl* IDE_PlugInSetting_t)(int id, const char* setting, const char* value);
typedef void(__cdecl* IDE_DebugLog_t)(const char* msg);

template <typename T>
static T Cb(int index) { return (index >= 0 && index < 1024) ? (T)g_callbacks[index] : nullptr; }

static int IdeVersion()
{
    auto f = Cb<SYS_Version_t>(1);
    return f ? f() : 0;
}

static HWND IdeMainWindow()
{
    auto f = Cb<IDE_GetWindowHandle_t>(16);
    return f ? (HWND)(intptr_t)f() : nullptr;
}

static void DebugLog(const char* msg)
{
    auto f = Cb<IDE_DebugLog_t>(173);
    if (f) f(msg);
}

// ---- language of the menus (same rules as the managed part: setting, PL/SQL Developer, Windows) ----
typedef char*(__cdecl* IDE_TranslationLanguage_t)();

static int ConfigLanguage() // 1 = de, 0 = en, -1 = auto / unknown
{
    wchar_t path[MAX_PATH * 2];
    if (GetEnvironmentVariableW(L"PLSQLDEVAGENT_CONFIG", path, MAX_PATH) == 0)
    {
        wchar_t appData[MAX_PATH];
        if (GetEnvironmentVariableW(L"APPDATA", appData, MAX_PATH) == 0) return -1;
        swprintf_s(path, L"%s\\PlsqlDevAgent\\config.json", appData);
    }
    HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
    if (h == INVALID_HANDLE_VALUE) return -1;
    char buf[16384];
    DWORD read = 0;
    BOOL ok = ReadFile(h, buf, sizeof(buf) - 1, &read, nullptr);
    CloseHandle(h);
    if (!ok) return -1;
    buf[read] = 0;
    const char* p = strstr(buf, "\"Language\"");
    if (!p) return -1;
    p = strchr(p + 10, '"');               // opening quote of the value
    if (!p) return -1;
    if (_strnicmp(p + 1, "de\"", 3) == 0) return 1;
    if (_strnicmp(p + 1, "en\"", 3) == 0) return 0;
    return -1;
}

static bool IsGerman()
{
    static int cached = -1;
    if (cached >= 0) return cached == 1;
    int lang = ConfigLanguage();
    if (lang < 0)
    {
        auto f = Cb<IDE_TranslationLanguage_t>(131);
        const char* ide = f ? f() : nullptr;
        if (ide && *ide)
            lang = (strstr(ide, "erman") || strstr(ide, "eutsch")) ? 1 : 0;
        else
            lang = PRIMARYLANGID(GetUserDefaultUILanguage()) == LANG_GERMAN ? 1 : 0;
    }
    cached = lang;
    return lang == 1;
}

// English / German text
#define TX(en, de) (IsGerman() ? (de) : (en))

// Delphi unmasks some floating point exceptions (zero divide, overflow, invalid).
// .NET / WinForms code expects them masked, so every transition into managed code is
// wrapped with a guard that masks them and restores the IDE's state afterwards.
struct FpuGuard
{
    unsigned int old;
    FpuGuard() { old = _mm_getcsr(); _mm_setcsr(old | 0x1F80); }
    ~FpuGuard() { _mm_setcsr(old); }
};

// Exported for the managed side (P/Invoke) so it can guard its own window procedures.
EXPORT unsigned int __cdecl PdaMaskFpu()
{
    unsigned int old = _mm_getcsr();
    _mm_setcsr(old | 0x1F80);
    return old;
}

EXPORT void __cdecl PdaRestoreFpu(unsigned int value)
{
    _mm_setcsr(value);
}

static void ShowError(const wchar_t* text)
{
    MessageBoxW(IdeMainWindow(), text, L"PL/Pilot", MB_OK | MB_ICONERROR);
}

static bool EnsureClr()
{
    if (g_dispatch) return true;
    if (g_clrTried) return false;
    g_clrTried = true;

    FpuGuard fpu;
    wchar_t asmPath[MAX_PATH * 2];
    swprintf_s(asmPath, L"%s\\PlsqlDevAgent\\PlsqlDevAgent.Core.dll", g_dir);
    if (GetFileAttributesW(asmPath) == INVALID_FILE_ATTRIBUTES)
    {
        wchar_t msg[MAX_PATH * 3];
        swprintf_s(msg, L"The managed part of the plug-in was not found:\n\n%s\n\n"
                        L"Copy the complete 'PlsqlDevAgent' folder next to PlsqlDevAgent.dll.", asmPath);
        ShowError(msg);
        return false;
    }

    ICLRMetaHost* meta = nullptr;
    ICLRRuntimeInfo* info = nullptr;
    ICLRRuntimeHost* host = nullptr;
    HRESULT hr = CLRCreateInstance(CLSID_CLRMetaHost, IID_PPV_ARGS(&meta));
    if (SUCCEEDED(hr)) hr = meta->GetRuntime(L"v4.0.30319", IID_PPV_ARGS(&info));
    if (SUCCEEDED(hr)) hr = info->GetInterface(CLSID_CLRRuntimeHost, IID_PPV_ARGS(&host));
    if (SUCCEEDED(hr)) hr = host->Start();  // S_FALSE if already started by someone else

    DWORD ret = 0;
    if (SUCCEEDED(hr))
    {
        wchar_t arg[MAX_PATH + 128];
        swprintf_s(arg, L"%p|%p|%d|%s", (void*)g_callbacks, (void*)&g_dispatch, g_pluginId, g_dir);
        hr = host->ExecuteInDefaultAppDomain(asmPath, L"PlsqlDevAgent.Bootstrap", L"Init", arg, &ret);
    }

    if (host) host->Release();
    if (info) info->Release();
    if (meta) meta->Release();

    if (FAILED(hr) || ret != 0 || !g_dispatch)
    {
        wchar_t msg[1024];
        swprintf_s(msg, L"Could not start the .NET part of the plug-in (hr=0x%08X, ret=%u).\n\n"
                        L"Requires .NET Framework 4.8. See %%LOCALAPPDATA%%\\PlsqlDevAgent\\agent.log for details.",
                   (unsigned)hr, (unsigned)ret);
        ShowError(msg);
        g_dispatch = nullptr;
        return false;
    }
    return true;
}

static intptr_t Dispatch(int evt, intptr_t a = 0, intptr_t b = 0, intptr_t fallback = 0)
{
    if (!g_dispatch) return fallback;
    FpuGuard fpu;
    return g_dispatch(evt, a, b);
}

// ---- DLL entry ------------------------------------------------------------------------
BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
        GetModuleFileNameW(module, g_dir, MAX_PATH);
        wchar_t* slash = wcsrchr(g_dir, L'\\');
        if (slash) *slash = 0;
    }
    return TRUE;
}

// ---- Plug-in primary functions ----------------------------------------------------------
EXPORT char* __cdecl IdentifyPlugIn(int id)
{
    g_pluginId = id;
    return (char*)g_description;
}

EXPORT void __cdecl RegisterCallback(int index, void* addr)
{
    if (index >= 0 && index < 1024) g_callbacks[index] = addr;
}

// All strings of this plug-in use the Windows ANSI code page. PL/SQL Developer applies CHARMODE=UTF8
// only while it is itself calling into the plug-in, but the agent calls the IDE asynchronously, and
// those calls are always decoded as ANSI (verified with version 17). ANSI everywhere is consistent.
static void SetCharModeAnsi()
{
    auto setting = Cb<IDE_PlugInSetting_t>(219);
    if (setting) setting(g_pluginId, "CHARMODE", "ANSI");
}

EXPORT char* __cdecl CreateMenuItem(int index)
{
    // ASCII only: menu texts are read as ANSI. And no "/" in names: it is the menu path separator
    // (even in the ribbon syntax, "GROUP=PL/Pilot" splits the group), hence "PL-Pilot" here.
    if (IdeVersion() >= 1200)
    {
        switch (index)
        {
        case MI_GROUP: return (char*)"GROUP=PL-Pilot";
        case MI_CHAT: return (char*)"LARGEITEM=Chat";
        case MI_ASK_SELECTION: return (char*)TX("ITEM=Ask about Selection", "ITEM=Zur Auswahl fragen");
        case MI_SETTINGS: return (char*)TX("ITEM=Settings", "ITEM=Einstellungen");
        }
    }
    else
    {
        switch (index)
        {
        case MI_GROUP: return (char*)"Tools / -";
        case MI_CHAT: return (char*)"Tools / PL-Pilot &Chat";
        case MI_ASK_SELECTION: return (char*)TX("Tools / Ask PL-Pilot about Selection", "Tools / PL-Pilot zur Auswahl fragen");
        case MI_SETTINGS: return (char*)TX("Tools / PL-Pilot Settings...", "Tools / PL-Pilot-Einstellungen...");
        }
    }
    return (char*)"";
}

// Tells PL/SQL Developer (15+) that we handle themes ourselves; without it IDE_GetCurrentStyle
// reports the light default to this plug-in.
EXPORT BOOL __cdecl SupportsStyles() { return TRUE; }

EXPORT char* __cdecl PlugInName() { return (char*)"PlsqlDevAgent"; }
EXPORT char* __cdecl PlugInShortName() { return (char*)"agent"; }

EXPORT char* __cdecl About()
{
    return (char*)TX("PL/Pilot\r\nThe AI copilot for PL/SQL Developer.\r\n"
                     "Works with any OpenAI-compatible chat completions endpoint.\r\n\r\nCreated by Luca Hascher",
                     "PL/Pilot\r\nDer KI-Copilot fuer PL/SQL Developer.\r\n"
                     "Funktioniert mit jedem OpenAI-kompatiblen Chat-Completions-Endpunkt.\r\n\r\nErstellt von Luca Hascher");
}

// ---- Event functions ----------------------------------------------------------------------
EXPORT void __cdecl OnCreate() {}

EXPORT void __cdecl OnActivate()
{
    SetCharModeAnsi();

    // Popup and button names are plain display names (no path parsing), so "PL/Pilot" works there.
    auto popup = Cb<IDE_CreatePopupItem_t>(69);
    if (popup)
    {
        const char* editorWindows[] = { "SQLWINDOW", "PROGRAMWINDOW", "TESTWINDOW", "COMMANDWINDOW" };
        for (const char* w : editorWindows) popup(g_pluginId, MI_POPUP_EDITOR, TX("Ask PL/Pilot", "PL/Pilot fragen"), w);

        const char* objectTypes[] = { "TABLE", "VIEW", "PACKAGE", "PACKAGE BODY", "PROCEDURE", "FUNCTION",
                                      "TRIGGER", "TYPE", "TYPE BODY", "SEQUENCE", "MATERIALIZED VIEW", "SYNONYM" };
        for (const char* t : objectTypes) popup(g_pluginId, MI_POPUP_OBJECT, TX("Ask PL/Pilot about this object", "PL/Pilot zu diesem Objekt fragen"), t);
    }

    auto button = Cb<IDE_CreateToolButton_t>(150);
    auto rootDir = Cb<SYS_RootDir_t>(3);
    if (button && rootDir && IdeVersion() >= 1200)
    {
        // SVG icons exist since version 15; older versions get the classic PNG icons.
        bool svg = IdeVersion() >= 1500;
        char path[MAX_PATH * 2];
        const char* root = rootDir();
        sprintf_s(path, "%s\\Icons\\%s", root, svg ? "compass.svg" : "Bulb.png");
        button(g_pluginId, MI_CHAT, "PL/Pilot Chat", path, nullptr);
        sprintf_s(path, "%s\\Icons\\%s", root, svg ? "magic-wand.svg" : "Magic.png");
        button(g_pluginId, MI_ASK_SELECTION, TX("Ask about Selection", "Zur Auswahl fragen"), path, nullptr);
        button(g_pluginId, MI_POPUP_EDITOR, TX("Ask PL/Pilot", "PL/Pilot fragen"), path, nullptr);
        button(g_pluginId, MI_POPUP_OBJECT, TX("Ask PL/Pilot about this object", "PL/Pilot zu diesem Objekt fragen"), path, nullptr);
        sprintf_s(path, "%s\\Icons\\%s", root, svg ? "gear.svg" : "Preferences.png");
        button(g_pluginId, MI_SETTINGS, TX("PL/Pilot Settings", "PL/Pilot-Einstellungen"), path, nullptr);
    }

    DebugLog("PlsqlDevAgent: OnActivate");
    Dispatch(EV_ACTIVATE);
}

// Called once PL/SQL Developer has finished starting. Only used for the automated in-IDE self test
// (environment variable PLSQLDEVAGENT_AUTOTEST=<output folder>); otherwise the CLR stays unloaded.
EXPORT void __cdecl AfterStart()
{
    wchar_t dir[MAX_PATH];
    if (GetEnvironmentVariableW(L"PLSQLDEVAGENT_AUTOTEST", dir, MAX_PATH) == 0) return;
    if (!EnsureClr()) return;
    Dispatch(EV_SELF_TEST);
}

EXPORT void __cdecl OnDeactivate() { Dispatch(EV_DEACTIVATE); }
EXPORT void __cdecl OnDestroy() { Dispatch(EV_DESTROY); }
EXPORT void __cdecl OnConnectionChange() { Dispatch(EV_CONNECTION_CHANGE); }
EXPORT void __cdecl OnWindowChange() { Dispatch(EV_WINDOW_CHANGE); }
EXPORT BOOL __cdecl CanClose() { return (BOOL)Dispatch(EV_CAN_CLOSE, 0, 0, TRUE); }

EXPORT void __cdecl OnMenuClick(int index)
{
    if (!EnsureClr()) return;
    Dispatch(EV_MENU_CLICK, index);
}

EXPORT void __cdecl Configure()
{
    if (!EnsureClr()) return;
    Dispatch(EV_MENU_CLICK, MI_SETTINGS);
}
