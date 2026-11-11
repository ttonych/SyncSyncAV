// Local control Tool plugin for VirtualDub2. SPDX-License-Identifier: MIT
// SDK calls run exclusively on the host UI thread. No simulated input.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <commctrl.h>
#include <sddl.h>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <limits>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>
#include "sdk/vdtool.h"
#include "sdk/input_metadata.h"

namespace {
HINSTANCE module = nullptr;
constexpr UINT Dispatch = WM_APP + 213;
constexpr UINT FinalizeOpen = WM_APP + 214;
constexpr uint32_t Magic = 0x31434456; // bytes VDC1
constexpr uint32_t MaxPayload = 32768;
enum Operation : uint16_t { State = 1, Open = 2, Seek = 3, Step = 4, View = 5, Embed = 6 };
#pragma pack(push, 1)
struct Header {
    uint32_t magic; uint16_t version; uint16_t operation;
    uint32_t id; uint32_t payload_size; int64_t argument;
    uint32_t flags; uint32_t reserved;
};
#pragma pack(pop)
static_assert(sizeof(Header) == 32);

struct Handle {
    HANDLE h = nullptr;
    explicit Handle(HANDLE value = nullptr) : h(value) {}
    ~Handle() { if (h && h != INVALID_HANDLE_VALUE) CloseHandle(h); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
};
std::string utf8(const std::wstring& value) {
    if (value.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), (int)value.size(), nullptr, 0, nullptr, nullptr);
    if (!n) throw std::runtime_error("Invalid UTF-16 string");
    std::string result(n, '\0');
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), (int)value.size(), result.data(), n, nullptr, nullptr);
    return result;
}
std::wstring wide(const std::string& value) {
    if (value.empty()) return {};
    int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), (int)value.size(), nullptr, 0);
    if (!n) throw std::runtime_error("Payload is not valid UTF-8");
    std::wstring result(n, L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(), (int)value.size(), result.data(), n);
    if (result.find(L'\0') != std::wstring::npos) throw std::runtime_error("Embedded NUL is not allowed");
    return result;
}
std::string quote(const std::string& value) {
    std::string result = "\"";
    for (unsigned char ch : value) {
        if (ch == '\\' || ch == '"') { result += '\\'; result += char(ch); }
        else if (ch < 0x20) { char buf[7]; std::snprintf(buf, sizeof(buf), "\\u%04x", ch); result += buf; }
        else result += char(ch);
    }
    return result + "\"";
}
std::string error(uint32_t id, const char* code, const std::string& message, bool uncertain = false) {
    return "{\"ok\":false,\"id\":" + std::to_string(id) + ",\"error\":{\"code\":" + quote(code) +
        ",\"message\":" + quote(message) + ",\"may_have_applied\":" + (uncertain ? "true" : "false") + "}}";
}
struct Request {
    Header header{};
    std::string payload;
    Handle done{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    std::atomic<bool> cancelled{false}, started{false};
    ULONGLONG deadline = GetTickCount64() + 15000;
    std::string response;
};
struct Snapshot {
    std::wstring file;
    int64_t frame = 0, count = 0, selection_start = 0, selection_end = 0;
    int subsets = 0;
    bool opened = false;
    bool busy = false, cached = false, auto_fit = false;
    std::string view_mode = "input";
    std::shared_ptr<VDInputMetadataV1> video;
};

class Bridge final : public IVDXTool {
    std::atomic<int> refs_{1};
    IVDToolCallbacks* callbacks_;
    uint32_t sdk_version_;
    HWND parent_ = nullptr, dispatcher_ = nullptr;
    Handle stop_;
    std::thread worker_;
    std::mutex request_mutex_;
    std::shared_ptr<Request> queued_, pending_open_;
    std::wstring expected_file_, pipe_name_;
    std::string last_error_;
    bool media_open_ = false;
    Snapshot last_snapshot_;
    bool menu_dirty_ = true, menu_refreshing_ = false;
    unsigned host_change_depth_ = 0, menu_loop_depth_ = 0;
    HMENU refreshed_menu_ = nullptr;
    HMENU embedded_menu_ = nullptr;

    static LONG_PTR embedded_style(LONG_PTR style) {
        return (style & ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX)) |
            WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN;
    }

    static LRESULT CALLBACK ParentProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp,
                                       UINT_PTR, DWORD_PTR data) {
        auto* self = reinterpret_cast<Bridge*>(data);
        if (self->embedded_menu_ && IsWindowEnabled(hwnd) &&
            (msg == WM_LBUTTONDOWN || (msg == WM_PARENTNOTIFY &&
                (LOWORD(wp) == WM_LBUTTONDOWN || LOWORD(wp) == WM_RBUTTONDOWN)))) {
            // Native preview surfaces do not normally need to focus their
            // top-level editor. In a child panel, focus starts in the WPF shell.
            SetFocus(hwnd);
        }
        if (msg == WM_STYLECHANGING && static_cast<int>(wp) == GWL_STYLE && self->embedded_menu_) {
            auto* change = reinterpret_cast<STYLESTRUCT*>(lp);
            change->styleNew = static_cast<DWORD>(embedded_style(change->styleNew));
        }
        if (msg == WM_WINDOWPOSCHANGING && self->embedded_menu_) {
            // Auto-size and restored desktop placement must stay inside the
            // owning panel. The host still receives WM_SIZE to lay out video.
            RECT rect{};
            if (GetClientRect(GetParent(hwnd), &rect)) {
                auto* pos = reinterpret_cast<WINDOWPOS*>(lp);
                if (!(pos->flags & SWP_NOMOVE)) { pos->x = 0; pos->y = 0; }
                if (!(pos->flags & SWP_NOSIZE)) {
                    pos->cx = rect.right > 0 ? rect.right : 1;
                    pos->cy = rect.bottom > 0 ? rect.bottom : 1;
                }
            }
        }
        // A host command can detach the tool. Keep this callback's object alive
        // until DefSubclassProc and our bookkeeping have both finished.
        self->AddRef();
        const bool change = msg == WM_COMMAND || msg == WM_DROPFILES ||
            msg == WM_ENABLE || msg == WM_SIZE || msg == WM_CONTEXTMENU ||
            (msg >= WM_USER + 101 && msg <= WM_USER + 103) || msg == WM_USER + 105;
        const bool init = msg == WM_INITMENU;
        const bool was_refreshing = self->menu_refreshing_;
        if (change) { ++self->host_change_depth_; self->menu_dirty_ = true; }
        if (init) self->menu_refreshing_ = true;
        if (msg == WM_ENTERMENULOOP) ++self->menu_loop_depth_;
        LRESULT result = DefSubclassProc(hwnd, msg, wp, lp);
        if (init) {
            self->menu_refreshing_ = was_refreshing;
            self->refreshed_menu_ = reinterpret_cast<HMENU>(wp);
            self->menu_dirty_ = false;
        }
        if (change) { --self->host_change_depth_; self->menu_dirty_ = true; }
        if (msg == WM_EXITMENULOOP && self->menu_loop_depth_) --self->menu_loop_depth_;
        if (msg == WM_NCDESTROY) RemoveWindowSubclass(hwnd, ParentProc, reinterpret_cast<UINT_PTR>(self));
        self->Release();
        return result;
    }
    bool host_unavailable() const {
        return !parent_ || !IsWindowEnabled(parent_) || pending_open_ ||
            host_change_depth_ || menu_loop_depth_ || menu_refreshing_;
    }
    HMENU current_menu() {
        if (host_unavailable()) return nullptr;
        HMENU main = GetMenu(parent_);
        // Windows does not expose a menu on WS_CHILD windows. Keep the host's
        // normal HMENU for state checks; the native menu bar is not presented.
        if (!main) main = embedded_menu_;
        if (main && (menu_dirty_ || main != refreshed_menu_)) {
            // WM_INITMENU rebuilds Tools and touches the input driver. Refresh
            // once after a host change, never for each read in each state poll.
            SendMessageW(parent_, WM_INITMENU, reinterpret_cast<WPARAM>(main), 0);
        }
        return main;
    }

    static LRESULT CALLBACK WindowProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
        Bridge* self = reinterpret_cast<Bridge*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (msg == WM_NCCREATE) {
            self = static_cast<Bridge*>(reinterpret_cast<CREATESTRUCTW*>(lp)->lpCreateParams);
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
        }
        if (self) {
            if (msg == Dispatch) { self->drain(); return 0; }
            if (msg == FinalizeOpen) { self->finish_open(); return 0; }
            if (msg == WM_TIMER && wp == 1) { self->expire_open(); return 0; }
        }
        return DefWindowProcW(hwnd, msg, wp, lp);
    }
    static HMENU menu_for(HMENU menu, UINT command) {
        for (int i = 0, n = GetMenuItemCount(menu); i < n; ++i) {
            if (GetMenuItemID(menu, i) == command) return menu;
            if (HMENU sub = GetSubMenu(menu, i)) if (HMENU found = menu_for(sub, command)) return found;
        }
        return nullptr;
    }
    bool command_available(UINT command) {
        HMENU main = current_menu();
        if (!main) return false;
        HMENU menu = menu_for(main, command);
        if (!menu) return false;
        UINT state = GetMenuState(menu, command, MF_BYCOMMAND);
        return state != UINT(-1) && !(state & (MF_DISABLED | MF_GRAYED));
    }
    bool checked(UINT command) {
        HMENU main = current_menu();
        if (!main) return false;
        HMENU menu = menu_for(main, command);
        return menu && (GetMenuState(menu, command, MF_BYCOMMAND) & MF_CHECKED);
    }
    Snapshot snapshot() {
        // A modal loop can dispatch our posted requests while the input driver
        // is only partly initialized (notably the MPLS playlist picker). Even
        // WM_INITMENU then dereferences incomplete host objects. Do not call
        // the SDK or refresh menus until the host returns to an enabled state.
        if (host_unavailable()) {
            Snapshot s = last_snapshot_;
            s.busy = true; s.cached = true;
            return s;
        }
        Snapshot s;
        std::vector<wchar_t> name(32768);
        callbacks_->GetFileName(name.data(), name.size());
        IVDTimeline* timeline = callbacks_->GetTimeline();
        if (!timeline) throw std::runtime_error("Timeline callback is unavailable");
        s.subsets = timeline->GetSubsetCount();
        if (s.subsets < 0 || s.subsets > 100000) throw std::runtime_error("Unsupported timeline subset count");
        for (int i = 0; i < s.subsets; ++i) {
            int64 start = -1, end = -1;
            timeline->GetSubsetRange(i, start, end);
            if (start < 0 || end < start || end - start > INT64_MAX - s.count) throw std::runtime_error("Invalid timeline range");
            s.count += end - start;
        }
        // The SDK keeps the old filename/subset even after File/Close. The
        // verified File/Close command reflects the actual input source.
        s.opened = media_open_ && s.count > 0 && command_available(40016);
        if (!s.opened) { s.count = 0; s.subsets = 0; }
        if (s.opened) {
            s.file = name.data();
            s.frame = timeline->GetTimelinePos();
            timeline->GetSelection(s.selection_start, s.selection_end);
            // Optional extension from our input driver. It copies published
            // immutable metadata and does not enter FFmpeg or refresh menus.
            if (HMODULE driver = GetModuleHandleW(L"avlib-1.vdplugin")) {
                auto read = reinterpret_cast<VDReadInputMetadataV1>(GetProcAddress(driver, "VDReadInputMetadata"));
                if (read) {
                    auto info = std::make_shared<VDInputMetadataV1>();
                    if (read(s.file.c_str(), info.get(), sizeof(*info)) && info->version == 1 &&
                        info->fps_num > 0 && info->fps_den > 0 && info->width > 0 && info->height > 0) s.video = info;
                }
            }
        }
        s.busy = !command_available(40001);
        s.view_mode = checked(40512) ? "input" : checked(40513) ? "output" : "both";
        s.auto_fit = checked(40517);
        last_snapshot_ = s;
        return s;
    }
    std::string state_json(const Snapshot& s) {
        wchar_t executable[32768]{};
        GetModuleFileNameW(nullptr, executable, (DWORD)std::size(executable));
        std::string video = "null";
        if (s.video) {
            const auto& v = *s.video;
            video = "{\"fps_num\":" + std::to_string(v.fps_num) + ",\"fps_den\":" + std::to_string(v.fps_den) +
                ",\"width\":" + std::to_string(v.width) + ",\"height\":" + std::to_string(v.height) +
                ",\"codec\":" + quote(v.codec) + ",\"source_frames\":" + std::to_string(v.source_frames) +
                ",\"resolved_file\":" + quote(utf8(v.resolved_path)) + ",\"origin\":\"input_driver\"}";
        }
        return "{\"pid\":" + std::to_string(GetCurrentProcessId()) + ",\"hwnd\":" +
            std::to_string(HandleToULong(parent_)) + ",\"bridge_version\":\"1.2.0\",\"sdk_api\":" +
            std::to_string(sdk_version_) + ",\"executable\":" + quote(utf8(executable)) +
            ",\"has_media\":" + (s.opened ? "true" : "false") +
            ",\"busy\":" + (s.busy ? "true" : "false") + ",\"state_cached\":" + (s.cached ? "true" : "false") + ",\"file\":" + quote(utf8(s.file)) +
            ",\"frame\":" + (s.opened ? std::to_string(s.frame) : "null") + ",\"frame_count\":" + std::to_string(s.count) +
            ",\"selection\":[" + std::to_string(s.selection_start) + "," + std::to_string(s.selection_end) +
            "],\"subset_count\":" + std::to_string(s.subsets) + ",\"video\":" + video + ",\"fps\":null,\"decoded_ready\":null," +
            "\"readiness\":\"timeline_only\",\"pending_open\":" + (pending_open_ ? "true" : "false") +
            ",\"view\":{\"mode\":" + quote(s.view_mode) +
            ",\"auto_fit\":" + (s.auto_fit ? "true" : "false") + "},\"last_error\":" + quote(last_error_) +
            ",\"capabilities\":[\"state\",\"open_file\",\"seek_frame\",\"step\",\"view\",\"embed_window\"]}";
    }
    void complete(const std::shared_ptr<Request>& r, const std::string& response) {
        r->response = response;
        SetEvent(r->done.h);
    }
    void success(const std::shared_ptr<Request>& r, const char* operation, const Snapshot& s) {
        complete(r, "{\"ok\":true,\"id\":" + std::to_string(r->header.id) + ",\"operation\":" +
            quote(operation) + ",\"state\":" + state_json(s) + "}");
    }
    void drain() {
        std::shared_ptr<Request> r;
        { std::lock_guard<std::mutex> lock(request_mutex_); r.swap(queued_); }
        if (!r) return;
        if (r->cancelled || GetTickCount64() > r->deadline) { complete(r, error(r->header.id, "expired", "Request expired before execution")); return; }
        r->started = true;
        try { process(r); }
        catch (const std::exception& e) { complete(r, error(r->header.id, "bridge_error", e.what(), r->header.operation != State)); }
        catch (...) { complete(r, error(r->header.id, "host_error", "The host raised an exception", r->header.operation != State)); }
    }
    void process(const std::shared_ptr<Request>& r) {
        const auto& h = r->header;
        if (h.operation == State) { success(r, "state", snapshot()); return; }
        if (h.operation == Embed) {
            if (host_unavailable() || embedded_menu_) { complete(r, error(h.id, "busy", "Host is busy or already embedded")); return; }
            HWND target = reinterpret_cast<HWND>(static_cast<uintptr_t>(h.argument));
            DWORD target_pid = 0;
            if (h.argument <= 0 || h.flags || !r->payload.empty() || !IsWindow(target) ||
                !GetWindowThreadProcessId(target, &target_pid) || target_pid == GetCurrentProcessId()) {
                complete(r, error(h.id, "invalid_parent", "An existing window in the owning application is required")); return;
            }
            HMENU menu = GetMenu(parent_);
            if (!menu) { complete(r, error(h.id, "unsupported_host", "Host normal menu is unavailable")); return; }
            LONG_PTR style = GetWindowLongPtrW(parent_, GWL_STYLE);
            LONG_PTR ex = GetWindowLongPtrW(parent_, GWL_EXSTYLE);
            ShowWindow(parent_, SW_HIDE);
            embedded_menu_ = menu;
            SetWindowLongPtrW(parent_, GWL_STYLE, embedded_style(style));
            SetWindowLongPtrW(parent_, GWL_EXSTYLE, ex & ~WS_EX_APPWINDOW);
            SetParent(parent_, target);
            if (HandleToULong(GetParent(parent_)) != HandleToULong(target)) {
                embedded_menu_ = nullptr;
                SetWindowLongPtrW(parent_, GWL_STYLE, style);
                SetWindowLongPtrW(parent_, GWL_EXSTYLE, ex);
                complete(r, error(h.id, "embed_failed", "Host could not attach to the requested parent", true)); return;
            }
            RECT rect{}; GetClientRect(target, &rect);
            SetWindowPos(parent_, nullptr, 0, 0, rect.right, rect.bottom,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
            menu_dirty_ = true;
            success(r, "embed", snapshot()); return;
        }
        if (host_unavailable() || !command_available(40219)) {
            // With no source, Goto is disabled; opening still works if File/Open is enabled.
            if (!(h.operation == Open && !host_unavailable() && command_available(40001))) {
                complete(r, error(h.id, "busy", "The host is busy, modal, or has no editable video timeline")); return;
            }
        }
        if (h.operation == Open) {
            if (h.flags || h.argument || r->payload.empty()) { complete(r, error(h.id, "invalid_request", "Open requires only a UTF-8 absolute file path")); return; }
            std::wstring file = wide(r->payload);
            if (file.size() < 3 || !((file[1] == L':' && (file[2] == L'\\' || file[2] == L'/')) || file.rfind(L"\\\\", 0) == 0)) {
                complete(r, error(h.id, "invalid_path", "An absolute path is required")); return;
            }
            std::vector<wchar_t> full(32768);
            DWORD full_size = GetFullPathNameW(file.c_str(), (DWORD)full.size(), full.data(), nullptr);
            if (!full_size || full_size >= full.size()) {
                complete(r, error(h.id, "invalid_path", "Unable to normalize the path")); return;
            }
            file = full.data();
            auto dot = file.find_last_of(L'.');
            if (dot != std::wstring::npos && _wcsicmp(file.c_str() + dot, L".bdmv") == 0) {
                complete(r, error(h.id, "playlist_required", "Open a specific MPLS file to avoid an interactive playlist chooser")); return;
            }
            DWORD attr = GetFileAttributesW(file.c_str());
            if (attr == INVALID_FILE_ATTRIBUTES || (attr & FILE_ATTRIBUTE_DIRECTORY)) {
                complete(r, error(h.id, "file_not_found", "The requested media file does not exist")); return;
            }
            expected_file_ = file;
            pending_open_ = r;
            media_open_ = false;
            last_error_.clear();
            SetTimer(dispatcher_, 1, 100, nullptr);
            // Pin the bundled input driver. With automatic selection the host
            // omits HandleFileOpenError when no driver recognizes the file.
            callbacks_->SetFileName(file.c_str(), L"Caching input driver (MPLS + CLPI v4)", r.get());
            return;
        }
        Snapshot before = snapshot();
        if (!before.opened) { complete(r, error(h.id, "no_media", "No video is open")); return; }
        if (h.operation == Seek || h.operation == Step) {
            if (h.flags || !r->payload.empty()) { complete(r, error(h.id, "invalid_request", "Seek/step does not accept flags or a payload")); return; }
            int64_t target = h.argument;
            if (h.operation == Step) {
                if ((h.argument > 0 && before.frame > INT64_MAX - h.argument) ||
                    (h.argument < 0 && before.frame < INT64_MIN - h.argument)) {
                    complete(r, error(h.id, "out_of_range", "Frame arithmetic overflow")); return;
                }
                target += before.frame;
            }
            if (target < 0 || target >= before.count) {
                complete(r, error(h.id, "out_of_range", "Frame must be between 0 and frame_count - 1")); return;
            }
            callbacks_->GetTimeline()->SetTimelinePos(target);
            Snapshot after = snapshot();
            if (after.file != before.file || after.frame != target) {
                complete(r, error(h.id, "position_mismatch", "The host did not select the requested frame (a zoomed timeline or another editor may own navigation)", true)); return;
            }
            success(r, h.operation == Seek ? "seek" : "step", after);
            return;
        }
        if (h.operation == View) {
            if (h.argument < 0 || h.argument > 2 || h.flags > 1 || !r->payload.empty()) {
                complete(r, error(h.id, "invalid_request", "View mode must be 0=input, 1=output, 2=both; flags=auto_fit")); return;
            }
            const UINT mode = h.argument == 0 ? 40512 : h.argument == 1 ? 40513 : 40515;
            if (!command_available(mode) || !command_available(40517)) {
                complete(r, error(h.id, "unsupported_view", "This host does not expose the verified presentation commands")); return;
            }
            SendMessageW(parent_, WM_COMMAND, mode, 0);
            if (checked(40517) != bool(h.flags & 1)) SendMessageW(parent_, WM_COMMAND, 40517, 0);
            if (!checked(mode) || checked(40517) != bool(h.flags & 1)) {
                complete(r, error(h.id, "view_mismatch", "Presentation settings did not match the request", true)); return;
            }
            success(r, "view", snapshot());
            return;
        }
        complete(r, error(h.id, "unknown_operation", "Unsupported operation code"));
    }
    void finish_open() {
        auto r = pending_open_;
        if (!r) return;
        pending_open_.reset();
        KillTimer(dispatcher_, 1);
        try {
            Snapshot s = snapshot();
            if (!s.opened || _wcsicmp(s.file.c_str(), expected_file_.c_str()) != 0)
                complete(r, error(r->header.id, "open_mismatch", "The host opened a different source or no video timeline", true));
            else success(r, "open", s);
        } catch (...) { complete(r, error(r->header.id, "host_error", "Unable to read back the opened source", true)); }
    }
    void expire_open() {
        auto r = pending_open_;
        if (r && (r->cancelled || GetTickCount64() > r->deadline)) {
            pending_open_.reset();
            KillTimer(dispatcher_, 1);
            complete(r, error(r->header.id, "open_timeout", "Opening did not complete before the deadline", true));
        }
    }
    bool io(HANDLE pipe, bool read, void* data, DWORD size, DWORD& transferred, DWORD timeout) {
        Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        OVERLAPPED ov{}; ov.hEvent = event.h;
        BOOL ok = read ? ReadFile(pipe, data, size, &transferred, &ov) : WriteFile(pipe, data, size, &transferred, &ov);
        if (ok) return true;
        if (GetLastError() != ERROR_IO_PENDING) return false;
        HANDLE handles[] = {stop_.h, event.h};
        if (WaitForMultipleObjects(2, handles, FALSE, timeout) != WAIT_OBJECT_0 + 1) {
            CancelIoEx(pipe, &ov); GetOverlappedResult(pipe, &ov, &transferred, TRUE); return false;
        }
        return GetOverlappedResult(pipe, &ov, &transferred, FALSE) != FALSE;
    }
    void serve() {
        // Restrict the local pipe to this user and LocalSystem. Remote clients are rejected.
        Handle token;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token.h)) return;
        DWORD size = 0;
        GetTokenInformation(token.h, TokenUser, nullptr, 0, &size);
        std::vector<BYTE> user(size);
        if (!GetTokenInformation(token.h, TokenUser, user.data(), size, &size)) return;
        LPWSTR sid = nullptr;
        if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(user.data())->User.Sid, &sid)) return;
        std::wstring sddl = L"D:P(A;;GA;;;SY)(A;;GA;;;" + std::wstring(sid) + L")";
        LocalFree(sid);
        PSECURITY_DESCRIPTOR descriptor = nullptr;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &descriptor, nullptr)) return;
        SECURITY_ATTRIBUTES sa{sizeof(sa), descriptor, FALSE};
        while (WaitForSingleObject(stop_.h, 0) != WAIT_OBJECT_0) {
            Handle pipe(CreateNamedPipeW(pipe_name_.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
                PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 65536, 65536, 5000, &sa));
            if (pipe.h == INVALID_HANDLE_VALUE) break;
            Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
            OVERLAPPED ov{}; ov.hEvent = event.h;
            bool connected = ConnectNamedPipe(pipe.h, &ov) != FALSE;
            DWORD e = connected ? 0 : GetLastError();
            if (!connected && e == ERROR_PIPE_CONNECTED) connected = true;
            if (!connected && e == ERROR_IO_PENDING) {
                HANDLE handles[] = {stop_.h, event.h};
                if (WaitForMultipleObjects(2, handles, FALSE, INFINITE) == WAIT_OBJECT_0 + 1) {
                    DWORD n = 0; connected = GetOverlappedResult(pipe.h, &ov, &n, FALSE) != FALSE;
                } else {
                    CancelIoEx(pipe.h, &ov); DWORD n = 0; GetOverlappedResult(pipe.h, &ov, &n, TRUE);
                }
            }
            if (!connected) continue;
            std::vector<char> packet(sizeof(Header) + MaxPayload);
            DWORD received = 0;
            if (!io(pipe.h, true, packet.data(), (DWORD)packet.size(), received, 5000)) { DisconnectNamedPipe(pipe.h); continue; }
            Header h{};
            if (received >= sizeof(h)) std::memcpy(&h, packet.data(), sizeof(h));
            std::string response;
            if (received < sizeof(h) || h.magic != Magic || h.version != 1 || h.reserved || h.payload_size > MaxPayload || received != sizeof(h) + h.payload_size) {
                response = error(h.id, "invalid_packet", "Unsupported or malformed protocol packet");
            } else if (h.operation < State || h.operation > Embed) {
                response = error(h.id, "unknown_operation", "Unsupported operation code");
            } else if (h.operation == State && (h.argument || h.flags || h.payload_size)) {
                response = error(h.id, "invalid_request", "State does not accept arguments");
            } else {
                auto r = std::make_shared<Request>();
                r->header = h;
                r->payload.assign(packet.data() + sizeof(h), h.payload_size);
                { std::lock_guard<std::mutex> lock(request_mutex_); queued_ = r; }
                if (!PostMessageW(dispatcher_, Dispatch, 0, 0)) response = error(h.id, "unavailable", "The UI dispatcher is unavailable");
                else {
                    HANDLE handles[] = {stop_.h, r->done.h};
                    DWORD wait = WaitForMultipleObjects(2, handles, FALSE, 16000);
                    if (wait == WAIT_OBJECT_0 + 1) response = r->response;
                    else { r->cancelled = true; response = error(h.id, "timeout", "The request did not complete before the deadline", r->started); }
                }
            }
            DWORD written = 0;
            if (io(pipe.h, false, response.data(), (DWORD)response.size(), written, 5000) && written == response.size()) {
                // The client acknowledges after consuming the full message. Disconnecting
                // immediately after a buffered write would discard unread response bytes.
                char acknowledgement = 0; DWORD consumed = 0;
                io(pipe.h, true, &acknowledgement, 1, consumed, 5000);
            }
            // Do not FlushFileBuffers: an unresponsive client must not block shutdown.
            DisconnectNamedPipe(pipe.h);
        }
        LocalFree(descriptor);
    }
public:
    explicit Bridge(const VDXToolContext& context) : callbacks_(context.mpCallbacks), sdk_version_(context.mAPIVersion) {}
    ~Bridge() { Detach(reinterpret_cast<VDXHWND>(parent_)); }
    int VDXAPIENTRY AddRef() override { return ++refs_; }
    int VDXAPIENTRY Release() override { int n = --refs_; if (!n) delete this; return n; }
    void* VDXAPIENTRY AsInterface(uint32 iid) override { return iid == IVDXTool::kIID || iid == IVDXUnknown::kIID ? static_cast<IVDXTool*>(this) : nullptr; }
    bool VDXAPIENTRY GetMenuInfo(int id, char* name, int size, bool* enabled) override {
        if (id) return false;
        std::snprintf(name, size, "Local SDK control bridge..."); *enabled = true; return true;
    }
    bool VDXAPIENTRY GetCommandId(int id, char* name, int size) override {
        if (id) return false; std::snprintf(name, size, "LocalControl.Status"); return true;
    }
    bool VDXAPIENTRY ExecuteMenu(int id, VDXHWND parent) override {
        if (id) return false;
        std::wstring text = L"Local SDK control bridge 1.2.0\n\n" + pipe_name_ +
            L"\n\nOpen and seek use the Tool SDK on the UI thread.\nPosition is confirmed; decoded-frame readiness is not exposed by this SDK.";
        MessageBoxW(reinterpret_cast<HWND>(parent), text.c_str(), L"VirtualDub2 SDK control", MB_OK);
        return true;
    }
    bool VDXAPIENTRY TranslateMessage(MSG&) override { return false; }
    bool VDXAPIENTRY HandleError(const char* message, int, void* data) override {
        menu_dirty_ = true;
        auto r = pending_open_;
        if (!r || data != r.get()) return false;
        last_error_ = message ? message : "File open failed";
        media_open_ = false;
        pending_open_.reset(); KillTimer(dispatcher_, 1);
        complete(r, error(r->header.id, "open_failed", last_error_, true)); return true;
    }
    bool VDXAPIENTRY HandleFileOpen(const wchar_t*, const wchar_t*, VDXHWND) override {
        menu_dirty_ = true;
        media_open_ = true;
        if (pending_open_) PostMessageW(dispatcher_, FinalizeOpen, 0, 0);
        return false;
    }
    bool VDXAPIENTRY HandleFileOpenError(const wchar_t* file, const wchar_t*, VDXHWND, const char* message, int) override {
        menu_dirty_ = true;
        auto r = pending_open_;
        if (!r || !file || _wcsicmp(file, expected_file_.c_str()) != 0) return false;
        last_error_ = message ? message : "File open failed";
        media_open_ = false;
        pending_open_.reset(); KillTimer(dispatcher_, 1);
        complete(r, error(r->header.id, "open_failed", last_error_, true));
        return true;
    }
    void VDXAPIENTRY Attach(VDXHWND parent) override {
        if (dispatcher_) return;
        parent_ = reinterpret_cast<HWND>(parent);
        WNDCLASSW wc{}; wc.lpfnWndProc = WindowProc; wc.hInstance = module; wc.lpszClassName = L"VDubLocalSdkControl1";
        RegisterClassW(&wc);
        dispatcher_ = CreateWindowExW(0, wc.lpszClassName, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, module, this);
        if (!dispatcher_) return;
        menu_dirty_ = true; refreshed_menu_ = nullptr;
        if (!SetWindowSubclass(parent_, ParentProc, reinterpret_cast<UINT_PTR>(this), reinterpret_cast<DWORD_PTR>(this))) {
            DestroyWindow(dispatcher_); dispatcher_ = nullptr; parent_ = nullptr;
            return;
        }
        stop_.h = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        pipe_name_ = L"\\\\.\\pipe\\vdub-sdk-" + std::to_wstring(GetCurrentProcessId());
        worker_ = std::thread([this] { try { serve(); } catch (...) {} });
    }
    void VDXAPIENTRY Detach(VDXHWND) override {
        if (!dispatcher_) return;
        SetEvent(stop_.h);
        if (worker_.joinable()) worker_.join();
        { std::lock_guard<std::mutex> lock(request_mutex_); queued_.reset(); }
        pending_open_.reset();
        if (parent_) RemoveWindowSubclass(parent_, ParentProc, reinterpret_cast<UINT_PTR>(this));
        DestroyWindow(dispatcher_); dispatcher_ = nullptr; parent_ = nullptr;
        CloseHandle(stop_.h); stop_.h = nullptr;
    }
};
bool VDXAPIENTRY create(const VDXToolContext* context, IVDXTool** output) {
    if (!context || context->mAPIVersion < 2 || !context->mpCallbacks || !output) return false;
    try { *output = new Bridge(*context); return true; } catch (...) { *output = nullptr; return false; }
}
const VDXToolDefinition definition = {sizeof(VDXToolDefinition), create};
const VDXPluginInfo info = {sizeof(VDXPluginInfo), L"Local SDK control bridge", L"Local development",
    L"Local named-pipe control using Tool SDK v2; explicit timeline-only readiness.", 0x01020000,
    kVDXPluginType_Tool, 0, kVDXPlugin_APIVersion, kVDXPlugin_APIVersion, 2, 2, &definition, nullptr, nullptr};
const VDXPluginInfo* infos[] = {&info, nullptr};
}
extern "C" __declspec(dllexport) const VDXPluginInfo* const* VDXAPIENTRY VDGetPluginInfo() { return infos; }
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) module = instance;
    return TRUE;
}
