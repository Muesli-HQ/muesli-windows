// Muesli Cotypist text service: the thin half of Cotypist (experimental, dev builds only).
// Port of macOS native/CotypistInputMethod/CotypistController.m (2f10b23d) to a TSF text input
// processor.
//
// Every key passes through to the app untouched. After a word boundary (space, punctuation) or a
// pause inside a word, the service asks Muesli for a suggestion over a named pipe and shows it as
// a grey composition after the caret ("ghost"). Tab inserts it, Ctrl+Right inserts its next word;
// any other key clears it and passes through. Muesli gathers context, runs the model and records
// outcomes; this DLL never waits on Muesli while handling a key: a slow or absent Muesli means no
// ghost, never a stalled keyboard. Ghost text is never committed: a composition the app ends
// itself (focus change) is emptied in OnCompositionTerminated.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <msctf.h>
#include <olectl.h>
#include <sddl.h>
#include <appmodel.h>
#include <atomic>
#include <cmath>
#include <cwctype>
#include <memory>
#include <string>
#include <thread>

#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")
#pragma comment(lib, "user32.lib")

// {847EA321-420D-4289-8CA1-6C2623C50551}
static const CLSID CLSID_Cotypist = {0x847ea321, 0x420d, 0x4289, {0x8c, 0xa1, 0x6c, 0x26, 0x23, 0xc5, 0x05, 0x51}};
// {39722C55-F0B2-47FB-81F5-D6BCBCAB2B1A}
static const GUID GUID_CotypistProfile = {0x39722c55, 0xf0b2, 0x47fb, {0x81, 0xf5, 0xd6, 0xbc, 0xbc, 0xab, 0x2b, 0x1a}};
// {A14DE6D8-2BF2-40CD-B292-CF686CB270D5}
static const GUID GUID_GhostAttribute = {0xa14de6d8, 0x2bf2, 0x40cd, {0xb2, 0x92, 0xcf, 0x68, 0x6c, 0xb2, 0x70, 0xd5}};

static const wchar_t kDescription[] = L"Muesli Cotypist";
static const wchar_t kPackageName[] = L"Muesli.Windows";
static const UINT kBoundaryDelayMs = 60;
static const size_t kContextChars = 1500;
static const UINT WM_COTYPIST_REPLY = WM_APP + 1;
static const UINT_PTR kDebounceTimer = 1;

static HINSTANCE g_module;
static std::atomic<long> g_locks{0};
// The pause before completing a half-typed word; 0 turns that off. Muesli sends the user's setting
// with every reply.
static std::atomic<UINT> g_midWordDelayMs{500};

static void ModuleAddRef() { ++g_locks; }
static void ModuleRelease() { --g_locks; }

#pragma region JSON

static std::string Utf8(const std::wstring &text) {
    if (text.empty()) return {};
    int size = WideCharToMultiByte(CP_UTF8, 0, text.data(), (int)text.size(), nullptr, 0, nullptr, nullptr);
    std::string out(size, '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.data(), (int)text.size(), out.data(), size, nullptr, nullptr);
    return out;
}

static std::wstring Wide(const std::string &text) {
    if (text.empty()) return {};
    int size = MultiByteToWideChar(CP_UTF8, 0, text.data(), (int)text.size(), nullptr, 0);
    std::wstring out(size, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, text.data(), (int)text.size(), out.data(), size);
    return out;
}

static std::string Quote(const std::wstring &value) {
    std::string out = "\"";
    for (unsigned char c : Utf8(value)) {
        switch (c) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (c < 0x20) { char buffer[8]; sprintf_s(buffer, "\\u%04x", c); out += buffer; }
                else out += (char)c;
        }
    }
    return out + "\"";
}

// A flat JSON object writer: what the helper sends.
struct JsonWriter {
    std::string body;
    JsonWriter &Add(const char *key, const std::string &raw) {
        body += body.empty() ? "{" : ",";
        body += "\"" + std::string(key) + "\":" + raw;
        return *this;
    }
    JsonWriter &Str(const char *key, const std::wstring &value) { return Add(key, Quote(value)); }
    JsonWriter &Bool(const char *key, bool value) { return Add(key, value ? "true" : "false"); }
    JsonWriter &Num(const char *key, double value) { char b[64]; sprintf_s(b, "%.6g", value); return Add(key, b); }
    std::string Line() const { return (body.empty() ? "{" : body) + "}\n"; }
};

// Reads one top-level member of a flat JSON reply: a string, number or boolean. Muesli's replies
// are flat; nested values are skipped by the absence of a match.
static bool FindValue(const std::string &json, const char *key, std::string &raw) {
    std::string needle = "\"" + std::string(key) + "\":";
    size_t at = json.find(needle);
    if (at == std::string::npos) return false;
    at += needle.size();
    while (at < json.size() && json[at] == ' ') at++;
    if (at >= json.size()) return false;
    if (json[at] == '"') {
        std::string out;
        for (size_t i = at + 1; i < json.size(); i++) {
            char c = json[i];
            if (c == '"') { raw = out; return true; }
            if (c != '\\' || i + 1 >= json.size()) { out += c; continue; }
            char e = json[++i];
            switch (e) {
                case 'n': out += '\n'; break;
                case 'r': out += '\r'; break;
                case 't': out += '\t'; break;
                case 'u': {
                    if (i + 4 >= json.size()) return false;
                    unsigned code = std::stoul(json.substr(i + 1, 4), nullptr, 16);
                    i += 4;
                    // 😀 pairs: combine, then encode as UTF-8.
                    if (code >= 0xD800 && code <= 0xDBFF && i + 6 < json.size() && json[i + 1] == '\\' && json[i + 2] == 'u') {
                        unsigned low = std::stoul(json.substr(i + 3, 4), nullptr, 16);
                        code = 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00);
                        i += 6;
                    }
                    if (code < 0x80) out += (char)code;
                    else if (code < 0x800) { out += (char)(0xC0 | (code >> 6)); out += (char)(0x80 | (code & 0x3F)); }
                    else if (code < 0x10000) { out += (char)(0xE0 | (code >> 12)); out += (char)(0x80 | ((code >> 6) & 0x3F)); out += (char)(0x80 | (code & 0x3F)); }
                    else { out += (char)(0xF0 | (code >> 18)); out += (char)(0x80 | ((code >> 12) & 0x3F)); out += (char)(0x80 | ((code >> 6) & 0x3F)); out += (char)(0x80 | (code & 0x3F)); }
                    break;
                }
                default: out += e;
            }
        }
        return false;
    }
    size_t end = json.find_first_of(",}", at);
    raw = json.substr(at, end == std::string::npos ? std::string::npos : end - at);
    return true;
}

static std::wstring JsonString(const std::string &json, const char *key) {
    std::string raw;
    return FindValue(json, key, raw) ? Wide(raw) : L"";
}

static bool JsonBool(const std::string &json, const char *key) {
    std::string raw;
    return FindValue(json, key, raw) && raw.rfind("true", 0) == 0;
}

static bool JsonNumber(const std::string &json, const char *key, double &value) {
    std::string raw;
    if (!FindValue(json, key, raw)) return false;
    char *end = nullptr;
    value = strtod(raw.c_str(), &end);
    return end != raw.c_str();
}

#pragma endregion

#pragma region Muesli pipe

static std::wstring PipeName() {
    std::wstring name = L"\\\\.\\pipe\\muesli-cotypist-";
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return {};
    BYTE buffer[256];
    DWORD length = 0;
    std::wstring sid;
    if (GetTokenInformation(token, TokenUser, buffer, sizeof(buffer), &length)) {
        LPWSTR text = nullptr;
        if (ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER *>(buffer)->User.Sid, &text)) { sid = text; LocalFree(text); }
    }
    CloseHandle(token);
    return sid.empty() ? std::wstring() : name + sid;
}

// The process holding the pipe's server end is Muesli: its package identity (granted by Windows on
// install, not claimable by an ordinary process) has Muesli's package name. Another process of the
// same user could otherwise create the pipe while Muesli is closed and read what the user types, so
// nothing is written before this passes.
static bool IsMuesli(HANDLE pipe) {
    ULONG processId = 0;
    if (!GetNamedPipeServerProcessId(pipe, &processId)) return false;
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, processId);
    if (!process) return false;
    wchar_t family[PACKAGE_FAMILY_NAME_MAX_LENGTH + 1] = {};
    UINT32 length = ARRAYSIZE(family);
    bool packaged = GetPackageFamilyName(process, &length, family) == ERROR_SUCCESS;
    CloseHandle(process);
    if (!packaged) return false;
    std::wstring name = family;
    size_t underscore = name.find(L'_');
    return underscore != std::wstring::npos && name.substr(0, underscore) == kPackageName;
}

// One request per connection: a JSON line out, a JSON line back. Runs off the UI thread.
static bool SendToMuesli(const std::string &line, DWORD timeoutMs, std::string *reply) {
    std::wstring name = PipeName();
    if (name.empty() || !WaitNamedPipeW(name.c_str(), 200)) return false;
    HANDLE pipe = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
    if (pipe == INVALID_HANDLE_VALUE) return false;
    bool ok = false;
    OVERLAPPED overlapped = {};
    overlapped.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    auto io = [&](BOOL started, DWORD *count) {
        if (!started && GetLastError() != ERROR_IO_PENDING) return false;
        if (WaitForSingleObject(overlapped.hEvent, timeoutMs) != WAIT_OBJECT_0) { CancelIo(pipe); return false; }
        return GetOverlappedResult(pipe, &overlapped, count, FALSE) != FALSE;
    };
    if (overlapped.hEvent && IsMuesli(pipe)) {
        DWORD written = 0;
        ResetEvent(overlapped.hEvent);
        if (io(WriteFile(pipe, line.data(), (DWORD)line.size(), nullptr, &overlapped), &written) && written == line.size()) {
            ok = true;
            if (reply) {
                char buffer[16384];
                while (reply->size() < (1 << 20)) {
                    DWORD count = 0;
                    ResetEvent(overlapped.hEvent);
                    if (!io(ReadFile(pipe, buffer, sizeof(buffer), nullptr, &overlapped), &count) || count == 0) break;
                    reply->append(buffer, count);
                    if (memchr(buffer, '\n', count)) break;
                }
                ok = !reply->empty();
            }
        }
    }
    if (overlapped.hEvent) CloseHandle(overlapped.hEvent);
    CloseHandle(pipe);
    return ok;
}

// Runs `work` on a short-lived thread that keeps this DLL loaded until it finishes.
template <typename Work> static void RunDetached(Work work) {
    HMODULE self = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, reinterpret_cast<LPCWSTR>(&RunDetached<Work>), &self);
    ModuleAddRef();
    auto job = new Work(std::move(work));
    HANDLE thread = CreateThread(nullptr, 0, [](void *argument) -> DWORD {
        std::unique_ptr<Work> owned(static_cast<Work *>(argument));
        (*owned)();
        owned.reset();
        ModuleRelease();
        FreeLibraryAndExitThread(g_module, 0);
    }, job, 0, nullptr);
    if (thread) CloseHandle(thread);
    else { delete job; ModuleRelease(); FreeLibrary(self); }
}

#pragma endregion

// A suggestion stage's reply, posted back to the UI thread.
struct Reply {
    UINT generation = 0;
    int stage = 1;
    std::wstring text, id, source;
    double firstWordLogProb = 0, elapsedMs = 0;
    bool hasLogProb = false;
};

// Leading spaces, then up to the next space: " of" from " of a word.", "dle" from "dle of".
static size_t NextWordLength(const std::wstring &text) {
    size_t index = 0;
    while (index < text.size() && text[index] == L' ') index++;
    while (index < text.size() && text[index] != L' ') index++;
    return index;
}

class TextService;

// An edit session that runs a callback with its edit cookie.
template <typename Body> class EditSession final : public ITfEditSession {
public:
    explicit EditSession(Body body) : _body(std::move(body)) {}
    STDMETHODIMP QueryInterface(REFIID riid, void **out) override {
        if (riid == IID_IUnknown || riid == IID_ITfEditSession) { *out = this; AddRef(); return S_OK; }
        *out = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++_refs; }
    STDMETHODIMP_(ULONG) Release() override { ULONG refs = --_refs; if (!refs) delete this; return refs; }
    STDMETHODIMP DoEditSession(TfEditCookie ec) override { _body(ec); return S_OK; }
private:
    Body _body;
    std::atomic<ULONG> _refs{1};
};

template <typename Body> static HRESULT RunEdit(ITfContext *context, TfClientId client, DWORD flags, Body body) {
    if (!context) return E_FAIL;
    auto session = new EditSession<Body>(std::move(body));
    HRESULT result = E_FAIL;
    context->RequestEditSession(client, session, flags, &result);
    session->Release();
    return result;
}

class DisplayAttributeInfo final : public ITfDisplayAttributeInfo {
public:
    STDMETHODIMP QueryInterface(REFIID riid, void **out) override {
        if (riid == IID_IUnknown || riid == IID_ITfDisplayAttributeInfo) { *out = this; AddRef(); return S_OK; }
        *out = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++_refs; }
    STDMETHODIMP_(ULONG) Release() override { ULONG refs = --_refs; if (!refs) delete this; return refs; }
    STDMETHODIMP GetGUID(GUID *guid) override { *guid = GUID_GhostAttribute; return S_OK; }
    STDMETHODIMP GetDescription(BSTR *description) override { *description = SysAllocString(L"Cotypist suggestion"); return S_OK; }
    // Grey, no underline: the ghost reads as ahead of the caret, not as text being composed.
    STDMETHODIMP GetAttributeInfo(TF_DISPLAYATTRIBUTE *attribute) override {
        *attribute = {};
        attribute->crText.type = TF_CT_COLORREF;
        attribute->crText.cr = RGB(128, 128, 128);
        attribute->lsStyle = TF_LS_NONE;
        attribute->bAttr = TF_ATTR_INPUT;
        return S_OK;
    }
    STDMETHODIMP SetAttributeInfo(const TF_DISPLAYATTRIBUTE *) override { return E_NOTIMPL; }
    STDMETHODIMP Reset() override { return S_OK; }
private:
    std::atomic<ULONG> _refs{1};
};

class AttributeEnum final : public IEnumTfDisplayAttributeInfo {
public:
    STDMETHODIMP QueryInterface(REFIID riid, void **out) override {
        if (riid == IID_IUnknown || riid == IID_IEnumTfDisplayAttributeInfo) { *out = this; AddRef(); return S_OK; }
        *out = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++_refs; }
    STDMETHODIMP_(ULONG) Release() override { ULONG refs = --_refs; if (!refs) delete this; return refs; }
    STDMETHODIMP Clone(IEnumTfDisplayAttributeInfo **out) override { auto copy = new AttributeEnum(); copy->_done = _done; *out = copy; return S_OK; }
    STDMETHODIMP Next(ULONG count, ITfDisplayAttributeInfo **items, ULONG *fetched) override {
        ULONG n = 0;
        if (count > 0 && !_done) { items[0] = new DisplayAttributeInfo(); _done = true; n = 1; }
        if (fetched) *fetched = n;
        return n == count ? S_OK : S_FALSE;
    }
    STDMETHODIMP Reset() override { _done = false; return S_OK; }
    STDMETHODIMP Skip(ULONG count) override { if (count > 0) _done = true; return S_OK; }
private:
    bool _done = false;
    std::atomic<ULONG> _refs{1};
};

class TextService final : public ITfTextInputProcessorEx, public ITfThreadMgrEventSink, public ITfKeyEventSink,
                          public ITfCompositionSink, public ITfTextEditSink, public ITfDisplayAttributeProvider {
public:
    TextService() { ModuleAddRef(); }
    ~TextService() { ModuleRelease(); }

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void **out) override {
        if (riid == IID_IUnknown || riid == IID_ITfTextInputProcessor || riid == IID_ITfTextInputProcessorEx)
            *out = static_cast<ITfTextInputProcessorEx *>(this);
        else if (riid == IID_ITfThreadMgrEventSink) *out = static_cast<ITfThreadMgrEventSink *>(this);
        else if (riid == IID_ITfKeyEventSink) *out = static_cast<ITfKeyEventSink *>(this);
        else if (riid == IID_ITfCompositionSink) *out = static_cast<ITfCompositionSink *>(this);
        else if (riid == IID_ITfTextEditSink) *out = static_cast<ITfTextEditSink *>(this);
        else if (riid == IID_ITfDisplayAttributeProvider) *out = static_cast<ITfDisplayAttributeProvider *>(this);
        else { *out = nullptr; return E_NOINTERFACE; }
        AddRef();
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++_refs; }
    STDMETHODIMP_(ULONG) Release() override { ULONG refs = --_refs; if (!refs) delete this; return refs; }

    // ITfTextInputProcessor
    STDMETHODIMP Activate(ITfThreadMgr *threadMgr, TfClientId clientId) override { return ActivateEx(threadMgr, clientId, 0); }
    STDMETHODIMP ActivateEx(ITfThreadMgr *threadMgr, TfClientId clientId, DWORD) override {
        _threadMgr = threadMgr;
        _threadMgr->AddRef();
        _clientId = clientId;
        GUID client;
        CoCreateGuid(&client);
        wchar_t text[64];
        StringFromGUID2(client, text, ARRAYSIZE(text));
        _clientName = text;
        wchar_t path[MAX_PATH];
        GetModuleFileNameW(nullptr, path, MAX_PATH);
        _app = wcsrchr(path, L'\\') ? wcsrchr(path, L'\\') + 1 : path;
        for (auto &c : _app) c = (wchar_t)towlower(c);

        ITfSource *source = nullptr;
        if (SUCCEEDED(_threadMgr->QueryInterface(IID_ITfSource, (void **)&source))) {
            source->AdviseSink(IID_ITfThreadMgrEventSink, static_cast<ITfThreadMgrEventSink *>(this), &_threadMgrCookie);
            source->Release();
        }
        ITfKeystrokeMgr *keystrokes = nullptr;
        if (SUCCEEDED(_threadMgr->QueryInterface(IID_ITfKeystrokeMgr, (void **)&keystrokes))) {
            keystrokes->AdviseKeyEventSink(_clientId, static_cast<ITfKeyEventSink *>(this), TRUE);
            keystrokes->Release();
        }
        ITfCategoryMgr *categories = nullptr;
        if (SUCCEEDED(CoCreateInstance(CLSID_TF_CategoryMgr, nullptr, CLSCTX_INPROC_SERVER, IID_ITfCategoryMgr, (void **)&categories))) {
            categories->RegisterGUID(GUID_GhostAttribute, &_ghostAtom);
            categories->Release();
        }
        CreateWindowHost();
        ITfDocumentMgr *focus = nullptr;
        if (SUCCEEDED(_threadMgr->GetFocus(&focus)) && focus) { OnSetFocus(focus, nullptr); focus->Release(); }
        return S_OK;
    }
    STDMETHODIMP Deactivate() override {
        _generation++;
        Dismiss();
        AdviseEdit(nullptr);
        if (_threadMgr) {
            ITfKeystrokeMgr *keystrokes = nullptr;
            if (SUCCEEDED(_threadMgr->QueryInterface(IID_ITfKeystrokeMgr, (void **)&keystrokes))) {
                keystrokes->UnadviseKeyEventSink(_clientId);
                keystrokes->Release();
            }
            ITfSource *source = nullptr;
            if (SUCCEEDED(_threadMgr->QueryInterface(IID_ITfSource, (void **)&source))) {
                source->UnadviseSink(_threadMgrCookie);
                source->Release();
            }
            _threadMgr->Release();
            _threadMgr = nullptr;
        }
        if (_window) { DestroyWindow(_window); _window = nullptr; }
        return S_OK;
    }

    // ITfThreadMgrEventSink: focus changes end the ghost; it must vanish, never be committed.
    STDMETHODIMP OnInitDocumentMgr(ITfDocumentMgr *) override { return S_OK; }
    STDMETHODIMP OnUninitDocumentMgr(ITfDocumentMgr *) override { return S_OK; }
    STDMETHODIMP OnSetFocus(ITfDocumentMgr *focus, ITfDocumentMgr *) override {
        _generation++;
        _typed.clear();
        Dismiss();
        ITfContext *context = nullptr;
        if (focus) focus->GetTop(&context);
        AdviseEdit(context);
        if (context) context->Release();
        return S_OK;
    }
    STDMETHODIMP OnPushContext(ITfContext *) override { return S_OK; }
    STDMETHODIMP OnPopContext(ITfContext *) override { return S_OK; }

    // ITfKeyEventSink
    STDMETHODIMP OnSetFocus(BOOL) override { return S_OK; }
    STDMETHODIMP OnTestKeyDown(ITfContext *, WPARAM key, LPARAM, BOOL *eaten) override {
        *eaten = IsAcceptKey(key);
        return S_OK;
    }
    STDMETHODIMP OnTestKeyUp(ITfContext *, WPARAM, LPARAM, BOOL *eaten) override { *eaten = FALSE; return S_OK; }
    STDMETHODIMP OnKeyUp(ITfContext *, WPARAM, LPARAM, BOOL *eaten) override { *eaten = FALSE; return S_OK; }
    STDMETHODIMP OnPreservedKey(ITfContext *, REFGUID, BOOL *eaten) override { *eaten = FALSE; return S_OK; }
    STDMETHODIMP OnKeyDown(ITfContext *context, WPARAM key, LPARAM flags, BOOL *eaten) override {
        *eaten = FALSE;
        if (key == VK_SHIFT || key == VK_CONTROL || key == VK_MENU || key == VK_LWIN || key == VK_RWIN || key == VK_CAPITAL) return S_OK;
        _generation++;
        _lastKeyTick = GetTickCount64();
        if (IsAcceptKey(key)) {
            *eaten = TRUE;  // consumed: the app never sees this Tab or Ctrl+Right
            if (key == VK_TAB) Accept(context, _ghost, L"");
            else {
                size_t length = NextWordLength(_ghost);
                std::wstring rest = _ghost.substr(length);
                bool more = rest.find_first_not_of(L' ') != std::wstring::npos;
                Accept(context, _ghost.substr(0, length), more ? rest : L"");
            }
            return S_OK;
        }
        if (_shown) FinishShown(L"ignored");
        ClearGhost(context);
        wchar_t typed = TrackKey(key, flags);
        if (ModifierDown(VK_CONTROL) || ModifierDown(VK_MENU) || !typed) return S_OK;
        // Word boundary: suggest the next words almost at once. Inside a word: wait for a pause,
        // then complete the word and continue.
        UINT delay;
        if (wcschr(L" .,;:!?)", typed)) { _midWord = false; delay = kBoundaryDelayMs; }
        else if ((iswalnum(typed) || typed == L'\'' || typed == L'-') && g_midWordDelayMs > 0) { _midWord = true; delay = g_midWordDelayMs; }
        else return S_OK;
        _afterSpace = typed == L' ';
        _triggeredAt = GetTickCount64();
        // The app inserts the character after this returns; read the context once it has.
        if (_window) SetTimer(_window, kDebounceTimer, delay, nullptr);
        return S_OK;
    }

    // ITfCompositionSink: the app ended the composition itself (focus change, its own commit). The
    // ghost was never the user's text: empty it while this cookie still allows writing.
    STDMETHODIMP OnCompositionTerminated(TfEditCookie ec, ITfComposition *composition) override {
        ITfRange *range = nullptr;
        if (!_accepting && composition && SUCCEEDED(composition->GetRange(&range)) && range) {
            range->SetText(ec, 0, L"", 0);
            range->Release();
        }
        if (_composition) { _composition->Release(); _composition = nullptr; }
        if (!_accepting) {
            _ghost.clear();
            if (_shown) FinishShown(L"dismissed");
        }
        return S_OK;
    }

    // ITfTextEditSink: a caret move nobody typed (a click) means the keystroke record no longer
    // ends where typing resumes.
    STDMETHODIMP OnEndEdit(ITfContext *, TfEditCookie, ITfEditRecord *record) override {
        BOOL changed = FALSE;
        ULONGLONG now = GetTickCount64();
        if (record && SUCCEEDED(record->GetSelectionStatus(&changed)) && changed && now - _lastKeyTick > 150 && now - _lastOwnEditTick > 150) {
            _typed.clear();
            if (_shown) FinishShown(L"dismissed");
            _generation++;
        }
        return S_OK;
    }

    // ITfDisplayAttributeProvider
    STDMETHODIMP EnumDisplayAttributeInfo(IEnumTfDisplayAttributeInfo **out) override { *out = new AttributeEnum(); return S_OK; }
    STDMETHODIMP GetDisplayAttributeInfo(REFGUID guid, ITfDisplayAttributeInfo **out) override {
        if (!IsEqualGUID(guid, GUID_GhostAttribute)) { *out = nullptr; return E_INVALIDARG; }
        *out = new DisplayAttributeInfo();
        return S_OK;
    }

private:
    static bool ModifierDown(int key) { return (GetKeyState(key) & 0x8000) != 0; }

    bool IsAcceptKey(WPARAM key) const {
        if (_ghost.empty()) return false;
        bool shift = ModifierDown(VK_SHIFT), control = ModifierDown(VK_CONTROL), alt = ModifierDown(VK_MENU);
        bool win = ModifierDown(VK_LWIN) || ModifierDown(VK_RWIN);
        if (key == VK_TAB) return !shift && !control && !alt && !win;
        // Ctrl+Right: the Windows counterpart of macOS Option-Right ("next word").
        if (key == VK_RIGHT) return control && !shift && !alt && !win;
        return false;
    }

    // Updates the keystroke record; returns the character typed, or 0.
    wchar_t TrackKey(WPARAM key, LPARAM flags) {
        switch (key) {
            case VK_BACK:
                if (ModifierDown(VK_CONTROL)) _typed.clear();
                else if (!_typed.empty()) {
                    _typed.pop_back();
                    if (!_typed.empty() && IS_HIGH_SURROGATE(_typed.back())) _typed.pop_back();
                }
                return 0;
            case VK_RETURN: case VK_ESCAPE:                    // sent or abandoned
            case VK_LEFT: case VK_RIGHT: case VK_UP: case VK_DOWN:  // the record no longer matches the caret
            case VK_TAB:                                       // a Tab that didn't accept: completion rewrites the line
            case VK_DELETE: case VK_HOME: case VK_END: case VK_PRIOR: case VK_NEXT:
                _typed.clear();
                return 0;
        }
        // Shortcuts change the text without typing it (Ctrl+V, Ctrl+X, Ctrl+W...).
        if (ModifierDown(VK_CONTROL) || ModifierDown(VK_MENU)) {
            if (!(ModifierDown(VK_CONTROL) && ModifierDown(VK_MENU))) { _typed.clear(); return 0; }  // AltGr types
        }
        if (key >= VK_F1 && key <= VK_F24) { _typed.clear(); return 0; }
        BYTE state[256];
        if (!GetKeyboardState(state)) return 0;
        wchar_t chars[8] = {};
        // 0x4: don't change the keyboard's dead-key state; the app still has to process this key.
        int count = ToUnicodeEx((UINT)key, (UINT)((flags >> 16) & 0xFF), state, chars, ARRAYSIZE(chars), 0x4, GetKeyboardLayout(0));
        if (count <= 0 || chars[0] < 0x20) return 0;
        _typed.append(chars, count);
        // The keystroke record keeps at most the context a request sends, whole characters only.
        if (_typed.size() > kContextChars) {
            size_t cut = _typed.size() - kContextChars;
            if (IS_LOW_SURROGATE(_typed[cut])) cut++;
            _typed.erase(0, cut);
        }
        return count == 1 ? chars[0] : 0;
    }

    void AdviseEdit(ITfContext *context) {
        if (_editContext) {
            ITfSource *source = nullptr;
            if (SUCCEEDED(_editContext->QueryInterface(IID_ITfSource, (void **)&source))) {
                source->UnadviseSink(_editCookie);
                source->Release();
            }
            _editContext->Release();
            _editContext = nullptr;
        }
        if (!context) return;
        ITfSource *source = nullptr;
        if (SUCCEEDED(context->QueryInterface(IID_ITfSource, (void **)&source))) {
            if (SUCCEEDED(source->AdviseSink(IID_ITfTextEditSink, static_cast<ITfTextEditSink *>(this), &_editCookie))) {
                _editContext = context;
                _editContext->AddRef();
            }
            source->Release();
        }
    }

    ITfContext *FocusedContext() {
        ITfDocumentMgr *focus = nullptr;
        ITfContext *context = nullptr;
        if (_threadMgr && SUCCEEDED(_threadMgr->GetFocus(&focus)) && focus) { focus->GetTop(&context); focus->Release(); }
        return context;
    }

    void CreateWindowHost() {
        static ATOM atom = 0;
        if (!atom) {
            WNDCLASSW type = {};
            type.lpfnWndProc = [](HWND window, UINT message, WPARAM w, LPARAM l) -> LRESULT {
                auto self = reinterpret_cast<TextService *>(GetWindowLongPtrW(window, GWLP_USERDATA));
                if (self && message == WM_TIMER && w == kDebounceTimer) { KillTimer(window, kDebounceTimer); self->RequestSuggestion(); return 0; }
                if (message == WM_COTYPIST_REPLY) {
                    std::unique_ptr<Reply> reply(reinterpret_cast<Reply *>(l));
                    if (self) self->ApplyReply(*reply);
                    return 0;
                }
                return DefWindowProcW(window, message, w, l);
            };
            type.hInstance = g_module;
            type.lpszClassName = L"MuesliCotypistHost";
            atom = RegisterClassW(&type);
        }
        _window = CreateWindowExW(0, L"MuesliCotypistHost", L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, g_module, nullptr);
        if (_window) SetWindowLongPtrW(_window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(this));
    }

    void RequestSuggestion() {
        ITfContext *context = FocusedContext();
        if (!context) return;
        UINT generation = _generation;
        RunEdit(context, _clientId, TF_ES_ASYNCDONTCARE | TF_ES_READ, [this, context, generation](TfEditCookie ec) {
            if (generation != _generation) return;
            TF_SELECTION selection = {};
            ULONG fetched = 0;
            if (FAILED(context->GetSelection(ec, TF_DEFAULT_SELECTION, 1, &selection, &fetched)) || fetched == 0) return;
            BOOL empty = FALSE;
            selection.range->IsEmpty(ec, &empty);
            if (!empty) { selection.range->Release(); return; }  // a selection: typing would replace it
            std::wstring field;
            LONG moved = 0;
            ITfRange *before = nullptr;
            if (SUCCEEDED(selection.range->Clone(&before))) {
                before->ShiftStart(ec, -(LONG)kContextChars, &moved, nullptr);
                std::wstring buffer(kContextChars, L'\0');
                ULONG count = 0;
                if (SUCCEEDED(before->GetText(ec, 0, buffer.data(), (ULONG)buffer.size(), &count))) field.assign(buffer.data(), count);
                before->Release();
            }
            // The caret at the start of a field that has text: nothing comes before it to continue.
            bool caretAtStart = false;
            ITfRange *after = nullptr;
            if (moved == 0 && SUCCEEDED(selection.range->Clone(&after))) {
                LONG ahead = 0;
                after->ShiftEnd(ec, 1, &ahead, nullptr);
                caretAtStart = ahead > 0;
                after->Release();
            }
            selection.range->Release();
            Send(field, caretAtStart, generation);
        });
        context->Release();
    }

    void Send(const std::wstring &field, bool caretAtStart, UINT generation) {
        JsonWriter base;
        base.Str("type", L"suggest").Str("app", _app).Str("field", field).Str("typed", _typed).Bool("midWord", _midWord)
            .Bool("afterSpace", _afterSpace).Str("client", _clientName).Num("generation", generation).Bool("caretAtStart", caretAtStart);
        HWND window = _window;
        ULONGLONG triggeredAt = _triggeredAt;
        RunDetached([window, base, generation, triggeredAt]() {
            auto ask = [&](bool firstWordOnly, int stage, std::string &json) {
                JsonWriter request = base;
                request.Bool("firstWordOnly", firstWordOnly);
                if (!SendToMuesli(request.Line(), 3000, &json)) return false;
                double delay = 0;
                if (JsonNumber(json, "midWordDelay", delay)) g_midWordDelayMs = (UINT)std::lround(delay * 1000);
                auto reply = new Reply();
                reply->generation = generation;
                reply->stage = stage;
                reply->text = JsonString(json, "text");
                reply->id = JsonString(json, "id");
                reply->source = JsonString(json, "source");
                reply->hasLogProb = JsonNumber(json, "firstWordLogProb", reply->firstWordLogProb);
                reply->elapsedMs = (double)(GetTickCount64() - triggeredAt);
                if (!PostMessageW(window, WM_COTYPIST_REPLY, 0, reinterpret_cast<LPARAM>(reply))) delete reply;
                return true;
            };
            // Stage 1: the first word alone, shown as soon as it clears the gate.
            std::string first;
            if (!ask(true, 1, first)) return;
            bool shown = !JsonString(first, "text").empty();
            // The gate said no: don't spend a second request, unless the first word ran past the
            // first stage's budget. One-word suggestions are complete after stage 1.
            if ((!shown && !JsonBool(first, "retryFull")) || JsonBool(first, "complete")) return;
            std::string full;
            ask(false, 2, full);
        });
    }

    void ApplyReply(const Reply &reply) {
        if (reply.generation != _generation || reply.text.empty()) return;
        std::wstring trigger = _midWord ? L"mid-word" : L"boundary";
        if (reply.stage == 2) {
            // Stage 2 extends the ghost in place only if it continues what's on screen.
            if (!_ghost.empty() && (reply.text.rfind(_ghost, 0) != 0 || reply.text.size() <= _ghost.size())) return;
        }
        ITfContext *context = FocusedContext();
        if (!context) return;
        ShowGhost(context, reply.text);
        context->Release();
        if (!_shown) {
            _shown = std::make_unique<JsonWriter>();
            _shownFields = {};
            _shownFields.app = _app;
            _shownFields.trigger = trigger;
            _shownFields.source = reply.source;
            _shownFields.firstMs = reply.elapsedMs;
            _shownFields.logProb = reply.hasLogProb ? reply.firstWordLogProb : 0;
        }
        _shownFields.text = reply.text;
        if (!reply.id.empty()) _shownFields.id = reply.id;
        if (reply.stage == 2) _shownFields.fullMs = reply.elapsedMs;
    }

    void ShowGhost(ITfContext *context, const std::wstring &text) {
        _lastOwnEditTick = GetTickCount64();
        _ghost = text;
        RunEdit(context, _clientId, TF_ES_ASYNCDONTCARE | TF_ES_READWRITE, [this, context, text](TfEditCookie ec) {
            if (_ghost != text) return;
            ITfRange *range = nullptr;
            if (_composition) _composition->GetRange(&range);
            else {
                TF_SELECTION selection = {};
                ULONG fetched = 0;
                if (FAILED(context->GetSelection(ec, TF_DEFAULT_SELECTION, 1, &selection, &fetched)) || !fetched) return;
                ITfContextComposition *compositions = nullptr;
                if (SUCCEEDED(context->QueryInterface(IID_ITfContextComposition, (void **)&compositions))) {
                    compositions->StartComposition(ec, selection.range, static_cast<ITfCompositionSink *>(this), &_composition);
                    compositions->Release();
                }
                selection.range->Release();
                if (!_composition) { _ghost.clear(); return; }
                _composition->GetRange(&range);
            }
            if (!range) return;
            range->SetText(ec, TF_ST_CORRECTION, text.c_str(), (LONG)text.size());
            ITfProperty *attribute = nullptr;
            if (SUCCEEDED(context->GetProperty(GUID_PROP_ATTRIBUTE, &attribute))) {
                VARIANT value;
                value.vt = VT_I4;
                value.lVal = (LONG)_ghostAtom;
                attribute->SetValue(ec, range, &value);
                attribute->Release();
            }
            // The caret stays at the start of the ghost, so it reads as ahead of the caret.
            ITfRange *caret = nullptr;
            if (SUCCEEDED(range->Clone(&caret))) {
                caret->Collapse(ec, TF_ANCHOR_START);
                TF_SELECTION selection = {caret, {TF_AE_NONE, FALSE}};
                context->SetSelection(ec, 1, &selection);
                caret->Release();
            }
            range->Release();
        });
    }

    // Inserts `text` from the ghost; `rest`, when given, stays on screen as the remaining suggestion.
    void Accept(ITfContext *context, const std::wstring &text, const std::wstring &rest) {
        KillTimer(_window, kDebounceTimer);
        _acceptedSoFar += text;
        _typed += text;
        _ghost.clear();
        _lastOwnEditTick = GetTickCount64();
        RunEdit(context, _clientId, TF_ES_SYNC | TF_ES_READWRITE, [this, context, text](TfEditCookie ec) {
            if (!_composition) return;
            ITfRange *range = nullptr;
            _composition->GetRange(&range);
            if (!range) return;
            range->SetText(ec, 0, text.c_str(), (LONG)text.size());
            ITfProperty *attribute = nullptr;
            if (SUCCEEDED(context->GetProperty(GUID_PROP_ATTRIBUTE, &attribute))) { attribute->Clear(ec, range); attribute->Release(); }
            ITfRange *caret = nullptr;
            if (SUCCEEDED(range->Clone(&caret))) {
                caret->Collapse(ec, TF_ANCHOR_END);
                TF_SELECTION selection = {caret, {TF_AE_NONE, FALSE}};
                context->SetSelection(ec, 1, &selection);
                caret->Release();
            }
            range->Release();
            _accepting = true;
            _composition->EndComposition(ec);
            _accepting = false;
            _composition->Release();
            _composition = nullptr;
        });
        if (rest.empty()) FinishShown(L"accepted");
        else ShowGhost(context, rest);
    }

    void ClearGhost(ITfContext *context) {
        if (_window) KillTimer(_window, kDebounceTimer);
        if (_ghost.empty() && !_composition) return;
        _ghost.clear();
        _lastOwnEditTick = GetTickCount64();
        RunEdit(context, _clientId, TF_ES_SYNC | TF_ES_READWRITE, [this](TfEditCookie ec) {
            if (!_composition) return;
            ITfRange *range = nullptr;
            if (SUCCEEDED(_composition->GetRange(&range)) && range) { range->SetText(ec, 0, L"", 0); range->Release(); }
            _accepting = true;  // already emptied: nothing for OnCompositionTerminated to do
            _composition->EndComposition(ec);
            _accepting = false;
            _composition->Release();
            _composition = nullptr;
        });
    }

    void Dismiss() {
        if (_shown) FinishShown(L"dismissed");
        ITfContext *context = FocusedContext();
        if (context) { ClearGhost(context); context->Release(); }
        else _ghost.clear();
    }

    // "accepted" covers all of it; otherwise words taken one at a time make it "partial".
    void FinishShown(const wchar_t *outcome) {
        std::wstring accepted = _acceptedSoFar;
        _acceptedSoFar.clear();
        if (!_shown) return;
        std::wstring result = !accepted.empty() && wcscmp(outcome, L"accepted") != 0 ? L"partial" : outcome;
        JsonWriter record;
        record.Str("type", L"outcome").Str("outcome", result).Str("accepted", accepted).Str("app", _shownFields.app)
            .Str("trigger", _shownFields.trigger).Str("text", _shownFields.text).Str("id", _shownFields.id)
            .Str("source", _shownFields.source).Num("first_ms", _shownFields.firstMs).Num("first_word_logprob", _shownFields.logProb);
        if (_shownFields.fullMs > 0) record.Num("full_ms", _shownFields.fullMs);
        _shown.reset();
        std::string line = record.Line();
        RunDetached([line]() { SendToMuesli(line, 1000, nullptr); });
    }

    struct Shown { std::wstring app, trigger, text, id, source; double firstMs = 0, fullMs = 0, logProb = 0; };

    std::atomic<ULONG> _refs{1};
    ITfThreadMgr *_threadMgr = nullptr;
    TfClientId _clientId = TF_CLIENTID_NULL;
    DWORD _threadMgrCookie = TF_INVALID_COOKIE;
    ITfContext *_editContext = nullptr;
    DWORD _editCookie = TF_INVALID_COOKIE;
    TfGuidAtom _ghostAtom = TF_INVALID_GUIDATOM;
    ITfComposition *_composition = nullptr;
    HWND _window = nullptr;
    std::wstring _clientName, _app;
    std::wstring _ghost;          // the suggestion on screen, empty when none
    std::wstring _typed;          // what the user typed into the current field (context fallback)
    std::wstring _acceptedSoFar;  // words of it taken one at a time
    std::unique_ptr<JsonWriter> _shown;  // non-null while a suggestion's outcome is pending
    Shown _shownFields;
    UINT _generation = 0;         // bumped on every key: a reply for an older generation is stale
    bool _afterSpace = false, _midWord = false, _accepting = false;
    ULONGLONG _triggeredAt = 0, _lastKeyTick = 0, _lastOwnEditTick = 0;
};

#pragma region COM server

class ClassFactory final : public IClassFactory {
public:
    STDMETHODIMP QueryInterface(REFIID riid, void **out) override {
        if (riid == IID_IUnknown || riid == IID_IClassFactory) { *out = this; return S_OK; }
        *out = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { ModuleAddRef(); return 2; }
    STDMETHODIMP_(ULONG) Release() override { ModuleRelease(); return 1; }
    STDMETHODIMP CreateInstance(IUnknown *outer, REFIID riid, void **out) override {
        if (outer) return CLASS_E_NOAGGREGATION;
        auto service = new TextService();
        HRESULT result = service->QueryInterface(riid, out);
        service->Release();
        return result;
    }
    STDMETHODIMP LockServer(BOOL lock) override { lock ? ModuleAddRef() : ModuleRelease(); return S_OK; }
};

static ClassFactory g_factory;

BOOL APIENTRY DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) { g_module = instance; DisableThreadLibraryCalls(instance); }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void **out) {
    if (!IsEqualCLSID(clsid, CLSID_Cotypist)) { *out = nullptr; return CLASS_E_CLASSNOTAVAILABLE; }
    return g_factory.QueryInterface(riid, out);
}

STDAPI DllCanUnloadNow() { return g_locks == 0 ? S_OK : S_FALSE; }

static std::wstring ClsidKey() {
    wchar_t text[64];
    StringFromGUID2(CLSID_Cotypist, text, ARRAYSIZE(text));
    return std::wstring(L"CLSID\\") + text;
}

static const GUID kCategories[] = {
    GUID_TFCAT_TIP_KEYBOARD, GUID_TFCAT_DISPLAYATTRIBUTEPROVIDER, GUID_TFCAT_TIPCAP_IMMERSIVESUPPORT,
    GUID_TFCAT_TIPCAP_SYSTRAYSUPPORT,
};

// Machine-wide (HKLM): TSF only discovers text services there, so registering needs an elevated
// regsvr32 (scripts/cotypist-register.ps1). Enabling and selecting the input method stay with the user.
STDAPI DllRegisterServer() {
    wchar_t path[MAX_PATH];
    GetModuleFileNameW(g_module, path, MAX_PATH);
    HKEY key = nullptr, server = nullptr;
    std::wstring clsid = ClsidKey();
    if (RegCreateKeyExW(HKEY_CLASSES_ROOT, clsid.c_str(), 0, nullptr, 0, KEY_WRITE, nullptr, &key, nullptr) != ERROR_SUCCESS) return SELFREG_E_CLASS;
    RegSetValueExW(key, nullptr, 0, REG_SZ, (const BYTE *)kDescription, sizeof(kDescription));
    LONG created = RegCreateKeyExW(key, L"InprocServer32", 0, nullptr, 0, KEY_WRITE, nullptr, &server, nullptr);
    RegCloseKey(key);
    if (created != ERROR_SUCCESS) return SELFREG_E_CLASS;
    RegSetValueExW(server, nullptr, 0, REG_SZ, (const BYTE *)path, (DWORD)((wcslen(path) + 1) * sizeof(wchar_t)));
    RegSetValueExW(server, L"ThreadingModel", 0, REG_SZ, (const BYTE *)L"Apartment", sizeof(L"Apartment"));
    RegCloseKey(server);

    HRESULT result = CoInitialize(nullptr);
    bool initialized = SUCCEEDED(result);
    ITfInputProcessorProfileMgr *profiles = nullptr;
    result = CoCreateInstance(CLSID_TF_InputProcessorProfiles, nullptr, CLSCTX_INPROC_SERVER, IID_ITfInputProcessorProfileMgr, (void **)&profiles);
    if (SUCCEEDED(result)) {
        result = profiles->RegisterProfile(CLSID_Cotypist, MAKELANGID(LANG_ENGLISH, SUBLANG_ENGLISH_US), GUID_CotypistProfile,
                                           kDescription, (ULONG)wcslen(kDescription), path, (ULONG)wcslen(path), 0, nullptr, 0, TRUE, 0);
        profiles->Release();
    }
    ITfCategoryMgr *categories = nullptr;
    if (SUCCEEDED(result) && SUCCEEDED(CoCreateInstance(CLSID_TF_CategoryMgr, nullptr, CLSCTX_INPROC_SERVER, IID_ITfCategoryMgr, (void **)&categories))) {
        for (const auto &category : kCategories) categories->RegisterCategory(CLSID_Cotypist, category, CLSID_Cotypist);
        categories->Release();
    }
    if (initialized) CoUninitialize();
    return SUCCEEDED(result) ? S_OK : SELFREG_E_CLASS;
}

STDAPI DllUnregisterServer() {
    HRESULT result = CoInitialize(nullptr);
    bool initialized = SUCCEEDED(result);
    ITfCategoryMgr *categories = nullptr;
    if (SUCCEEDED(CoCreateInstance(CLSID_TF_CategoryMgr, nullptr, CLSCTX_INPROC_SERVER, IID_ITfCategoryMgr, (void **)&categories))) {
        for (const auto &category : kCategories) categories->UnregisterCategory(CLSID_Cotypist, category, CLSID_Cotypist);
        categories->Release();
    }
    ITfInputProcessorProfileMgr *profiles = nullptr;
    if (SUCCEEDED(CoCreateInstance(CLSID_TF_InputProcessorProfiles, nullptr, CLSCTX_INPROC_SERVER, IID_ITfInputProcessorProfileMgr, (void **)&profiles))) {
        profiles->UnregisterProfile(CLSID_Cotypist, MAKELANGID(LANG_ENGLISH, SUBLANG_ENGLISH_US), GUID_CotypistProfile, 0);
        profiles->Release();
    }
    if (initialized) CoUninitialize();
    RegDeleteTreeW(HKEY_CLASSES_ROOT, ClsidKey().c_str());
    return S_OK;
}

#pragma endregion
