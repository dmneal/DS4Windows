// SPDX-License-Identifier: GPL-3.0-or-later
// Child-process adapter for PSMoveServiceEx's real C client API.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <fcntl.h>
#include <io.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>
#include <thread>
#include <chrono>
#include "PSMoveClient_CAPI.h"

static HANDLE frame_pipe;
static bool write_all(const void* data, size_t size) {
    const auto* ptr = static_cast<const unsigned char*>(data);
    while (size) {
        DWORD written = 0;
        if (!WriteFile(frame_pipe, ptr, static_cast<DWORD>(size), &written, nullptr) || !written) return false;
        ptr += written; size -= written;
    }
    return true;
}
static bool emit(const unsigned char* frame) {
    LARGE_INTEGER timestamp; QueryPerformanceCounter(&timestamp);
    const uint32_t header[] = { 0x31455950, 1, 640, 480, 1920, 921600 };
    return write_all(header, sizeof(header)) && write_all(&timestamp.QuadPart, 8) && write_all(frame, 921600);
}

struct Api {
    HMODULE library = nullptr;
#define FUNCTION(name) decltype(&::name) name = nullptr
    FUNCTION(PSM_Initialize); FUNCTION(PSM_Shutdown); FUNCTION(PSM_Update);
    FUNCTION(PSM_GetTrackerList); FUNCTION(PSM_AllocateTrackerListener); FUNCTION(PSM_FreeTrackerListener);
    FUNCTION(PSM_StartTrackerDataStream); FUNCTION(PSM_StopTrackerDataStream);
    FUNCTION(PSM_OpenTrackerVideoStream); FUNCTION(PSM_CloseTrackerVideoStream);
    FUNCTION(PSM_PollTrackerVideoStream); FUNCTION(PSM_GetTrackerVideoFrameBuffer); FUNCTION(PSM_GetTrackerWidth);
    FUNCTION(PSM_GetControllerList); FUNCTION(PSM_GetController); FUNCTION(PSM_GetControllerPose);
    FUNCTION(PSM_AllocateControllerListener); FUNCTION(PSM_FreeControllerListener);
    FUNCTION(PSM_StartControllerDataStream); FUNCTION(PSM_StopControllerDataStream);
#undef FUNCTION
    bool load() {
        wchar_t path[32768]; DWORD n = GetModuleFileNameW(nullptr, path, 32768);
        if (!n || n >= 32768) return false;
        wchar_t* slash = wcsrchr(path, L'\\'); if (!slash) return false;
        wcscpy(slash + 1, L"PSMoveClient_CAPI.dll");
        // Load only the SDK DLL packaged beside this helper, after redirecting its console logs.
        library = LoadLibraryExW(path, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (!library) { std::fprintf(stderr, "Cannot load PSMoveClient_CAPI.dll (Windows error %lu)\n", GetLastError()); return false; }
#define LOAD(name) name = reinterpret_cast<decltype(name)>(GetProcAddress(library, #name)); if (!name) { std::fprintf(stderr, "SDK missing %s\n", #name); return false; }
        LOAD(PSM_Initialize); LOAD(PSM_Shutdown); LOAD(PSM_Update); LOAD(PSM_GetTrackerList);
        LOAD(PSM_AllocateTrackerListener); LOAD(PSM_FreeTrackerListener);
        LOAD(PSM_StartTrackerDataStream); LOAD(PSM_StopTrackerDataStream);
        LOAD(PSM_OpenTrackerVideoStream); LOAD(PSM_CloseTrackerVideoStream);
        LOAD(PSM_PollTrackerVideoStream); LOAD(PSM_GetTrackerVideoFrameBuffer); LOAD(PSM_GetTrackerWidth);
        LOAD(PSM_GetControllerList); LOAD(PSM_GetController); LOAD(PSM_GetControllerPose);
        LOAD(PSM_AllocateControllerListener); LOAD(PSM_FreeControllerListener);
        LOAD(PSM_StartControllerDataStream); LOAD(PSM_StopControllerDataStream);
#undef LOAD
        return true;
    }
    ~Api() { if (library) FreeLibrary(library); }
};

static int audit_pose(Api& api) {
    PSMControllerList controllers = {};
    if (api.PSM_GetControllerList(&controllers,2000) != PSMResult_Success) return 7;
    int id = -1;
    for (int i=0;i<controllers.count && i<PSMOVESERVICE_MAX_CONTROLLER_COUNT;i++) {
        std::fprintf(stderr,"Controller %d: type %d\n",controllers.controller_id[i],controllers.controller_type[i]);
        if (id<0 && controllers.controller_type[i]==PSMController_DualShock4) id=controllers.controller_id[i];
    }
    if (id<0) {
        const char* json="{\"ControllerFound\":false,\"Reason\":\"No DS4 in the service controller list\"}\n";
        write_all(json,std::strlen(json)); return 0;
    }
    if (api.PSM_AllocateControllerListener(id)!=PSMResult_Success) return 7;
    unsigned flags=PSMStreamFlags_includePositionData | PSMStreamFlags_includePhysicsData |
        PSMStreamFlags_includeRawSensorData | PSMStreamFlags_includeCalibratedSensorData | PSMStreamFlags_includeRawTrackerData;
    PSMResult start=api.PSM_StartControllerDataStream(id,flags,2000);
    int freshFrames=0, validPoses=0, trackedFrames=0, orientationFrames=0, positionFrames=0;
    int lastSequence=-1; double firstSensor=-1,lastSensor=-1;
    auto begun=std::chrono::steady_clock::now();
    if(start==PSMResult_Success) {
        while(std::chrono::steady_clock::now()-begun<std::chrono::seconds(10)) {
            if(api.PSM_Update()!=PSMResult_Success) break;
            const auto* c=api.PSM_GetController(id);
            if(c && c->ControllerType==PSMController_DualShock4 && c->IsConnected && c->bValid &&
                c->DataFrameLastReceivedTime>0 && c->OutputSequenceNum!=lastSequence) {
                lastSequence=c->OutputSequenceNum; freshFrames++;
                const auto& s=c->ControllerState.PSDS4State;
                if(s.bIsOrientationValid) orientationFrames++;
                if(s.bIsPositionValid) positionFrames++;
                if(s.bIsCurrentlyTracking) trackedFrames++;
                PSMPosef pose={}; if(api.PSM_GetControllerPose(id,&pose)==PSMResult_Success) validPoses++;
                lastSensor=s.CalibratedSensorData.TimeInSeconds;
                if(firstSensor<0) firstSensor=lastSensor;
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(2));
        }
    }
    const auto* c=api.PSM_GetController(id);
    const bool ds4=c && c->ControllerType==PSMController_DualShock4;
    const auto s=ds4?c->ControllerState.PSDS4State:PSMDualShock4{};
    PSMPosef pose={}; PSMResult poseResult=api.PSM_GetControllerPose(id,&pose);
    char json[2048];
    std::snprintf(json,sizeof(json),
        "{\"ControllerFound\":true,\"ControllerId\":%d,\"Connection\":\"%s\",\"StreamStartResult\":%d,"
        "\"ObservationSeconds\":10,\"FreshStateFrames\":%d,\"ValidPoseFrames\":%d,\"OpticallyTrackedFrames\":%d,"
        "\"OrientationValidFrames\":%d,\"PositionValidFrames\":%d,\"LastPoseGetterResult\":%d,"
        "\"LastOrientationValid\":%s,\"LastPositionValid\":%s,\"LastOpticallyTracking\":%s,"
        "\"SensorClockAdvanced\":%s,\"RawTrackerMask\":%u,\"CrossButtonState\":%d,"
        "\"TrackingEnabled\":%s,\"TrackingColor\":%d}\n",
        id,ds4 && s.ConnectionType==PSMConnectionType_USB?"USB":ds4 && s.ConnectionType==PSMConnectionType_BLUETOOTH?"Bluetooth":"Unknown",(int)start,
        freshFrames,validPoses,trackedFrames,orientationFrames,positionFrames,(int)poseResult,
        s.bIsOrientationValid?"true":"false",s.bIsPositionValid?"true":"false",s.bIsCurrentlyTracking?"true":"false",
        firstSensor>=0 && lastSensor>firstSensor?"true":"false",s.RawTrackerData.ValidTrackerBitmask,(int)s.CrossButton,
        s.bIsTrackingEnabled?"true":"false",(int)s.TrackingColorType);
    write_all(json,std::strlen(json));
    if(start==PSMResult_Success) api.PSM_StopControllerDataStream(id,1000);
    api.PSM_FreeControllerListener(id);
    return 0;
}

int main(int argc, char** argv) {
    // Preserve a binary output handle, then redirect SDK console logging to stderr.
    if (!DuplicateHandle(GetCurrentProcess(), GetStdHandle(STD_OUTPUT_HANDLE), GetCurrentProcess(), &frame_pipe, 0, FALSE, DUPLICATE_SAME_ACCESS)) return 2;
    SetStdHandle(STD_OUTPUT_HANDLE, GetStdHandle(STD_ERROR_HANDLE));
    _dup2(_fileno(stderr), _fileno(stdout));
    int index = 0; bool list = false, synthetic = false, poseAudit = false;
    for (int i = 1; i < argc; ++i) {
        if (!std::strcmp(argv[i], "--list")) { list = true; continue; }
        if (!std::strcmp(argv[i], "--synthetic")) { synthetic = true; continue; }
        if (!std::strcmp(argv[i], "--pose-audit")) { poseAudit = true; continue; }
        if (std::strcmp(argv[i], "--camera") || ++i == argc) { std::fprintf(stderr, "Use --camera index, --list, --pose-audit, or --synthetic\n"); return 2; }
        char* end = nullptr; long value = std::strtol(argv[i], &end, 10);
        if (end == argv[i] || *end || value < 0 || value >= PSMOVESERVICE_MAX_TRACKER_COUNT) return 2;
        index = static_cast<int>(value);
    }
    if (synthetic) {
        std::fprintf(stderr, "SYNTHETIC frames: no service/camera accessed\n");
        std::vector<unsigned char> frame(921600);
        for (int y = 230; y < 250; ++y) for (int x = 290; x < 350; ++x) {
            frame[(y * 640 + x) * 3] = 255; frame[(y * 640 + x) * 3 + 2] = 255;
        }
        while (emit(frame.data())) std::this_thread::sleep_for(std::chrono::milliseconds(16));
        return 0;
    }
    Api api;
    if (!api.load()) return 3;
    if (api.PSM_Initialize("127.0.0.1", PSMOVESERVICE_DEFAULT_PORT, 2000) != PSMResult_Success) {
        std::fprintf(stderr, "Cannot connect to PSMoveServiceEx on 127.0.0.1:%s. Start its service first.\n", PSMOVESERVICE_DEFAULT_PORT);
        api.PSM_Shutdown(); return 3;
    }
    if (poseAudit) { int result=audit_pose(api); api.PSM_Shutdown(); CloseHandle(frame_pipe); return result; }
    PSMTrackerList trackers = {};
    if (api.PSM_GetTrackerList(&trackers, 2000) != PSMResult_Success) {
        std::fprintf(stderr, "PSMoveServiceEx tracker discovery failed\n"); api.PSM_Shutdown(); return 4;
    }
    std::fprintf(stderr, "PSMoveServiceEx trackers: %d\n", trackers.count);
    for (int i = 0; i < trackers.count && i < PSMOVESERVICE_MAX_TRACKER_COUNT; ++i)
        std::fprintf(stderr, "Index %d: tracker %d, %.0fx%.0f, type %d\n", i, trackers.trackers[i].tracker_id,
            trackers.trackers[i].tracker_screen_dimensions.x, trackers.trackers[i].tracker_screen_dimensions.y, trackers.trackers[i].tracker_type);
    if (list || index >= trackers.count) {
        if (!list) std::fprintf(stderr, "Selected tracker unavailable. Configure PS Eye drivers in PSMoveServiceEx.\n");
        api.PSM_Shutdown(); return trackers.count > 0 && list ? 0 : 4;
    }
    const auto& info = trackers.trackers[index]; const int id = info.tracker_id;
    if (info.tracker_type != PSMTracker_PS3Eye || info.tracker_screen_dimensions.x != 640 || info.tracker_screen_dimensions.y != 480) {
        std::fprintf(stderr, "Select a PS Eye configured for 640x480 in PSMoveServiceEx Config Tool.\n"); api.PSM_Shutdown(); return 4;
    }
    bool allocated = false, started = false, opened = false;
    int result = 5;
    if (api.PSM_AllocateTrackerListener(id, &info) == PSMResult_Success) {
        allocated = true;
        if (api.PSM_StartTrackerDataStream(id, 2000) == PSMResult_Success) {
            started = true;
            if (api.PSM_OpenTrackerVideoStream(id) == PSMResult_Success) {
                opened = true;
                std::fprintf(stderr, "PSMoveServiceEx shared-memory video open. Exposure/gain/FPS belong to Config Tool.\n");
                result = 0;
                for (;;) {
                    if (api.PSM_Update() != PSMResult_Success) { result = 6; break; }
                    PSMResult polled = api.PSM_PollTrackerVideoStream(id);
                    if (polled == PSMResult_NoData) { std::this_thread::sleep_for(std::chrono::milliseconds(1)); continue; }
                    if (polled != PSMResult_Success) { result = 6; break; }
                    int width = 0;
                    if (api.PSM_GetTrackerWidth(id, &width) != PSMResult_Success) { result = 6; break; }
                    if (width == 0) { std::this_thread::sleep_for(std::chrono::milliseconds(1)); continue; }
                    if (width != 640) {
                        std::fprintf(stderr, "Tracker mode changed; stop capture before reconfiguring the service.\n"); result = 6; break;
                    }
                    const unsigned char* buffer = nullptr;
                    if (api.PSM_GetTrackerVideoFrameBuffer(id, &buffer) != PSMResult_Success || !buffer) { result = 6; break; }
                    if (!emit(buffer)) break;
                }
            }
        }
    }
    if (result != 0) std::fprintf(stderr, "PSMoveServiceEx video stream failed (stage result %d)\n", result);
    if (opened) api.PSM_CloseTrackerVideoStream(id);
    if (started) api.PSM_StopTrackerDataStream(id, 1000);
    if (allocated) api.PSM_FreeTrackerListener(id);
    api.PSM_Shutdown(); CloseHandle(frame_pipe);
    return result;
}
