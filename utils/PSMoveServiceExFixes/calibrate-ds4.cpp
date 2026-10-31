// SPDX-License-Identifier: Apache-2.0
// Uses the upstream calibration request; does not read controller HID directly.
#include "PSMoveClient_CAPI.h"
#include "PSMoveProtocolInterface.h"
#include "PSMoveProtocol.pb.h"
#include <chrono>
#include <thread>
#include <cstdlib>
#include <cstdio>
#include <cmath>
struct Reply {bool done=false; PSMResult result=PSMResult_Error;};
static void reply(const PSMResponseMessage* response,void* userdata) {
    auto& r=*static_cast<Reply*>(userdata); r.result=response->result_code; r.done=true;
}
int main(int argc,char** argv) {
    if(argc!=7 && argc!=3) {std::fprintf(stderr,"Use: calibrate-ds4 id biasX biasY biasZ driftRaw varianceRaw OR id --gain-only\n");return 2;}
    char* endId=nullptr; const long parsedId=std::strtol(argv[1],&endId,10);
    if(!endId || *endId || parsedId<0 || parsedId>=PSMOVESERVICE_MAX_CONTROLLER_COUNT) return 2;
    const int id=static_cast<int>(parsedId);
    RequestPtr request(new PSMoveProtocol::Request());
    request->set_type(PSMoveProtocol::Request_RequestType_SET_CONTROLLER_GYROSCOPE_CALIBRATION);
    auto* calibration=request->mutable_set_controller_gyroscope_calibration_request();
    calibration->set_controller_id(id);
    if(argc==7) {
        float values[5]; for(int i=0;i<5;i++) {char* end=nullptr;values[i]=std::strtof(argv[i+2],&end);if(!end || *end || !std::isfinite(values[i])) return 2;}
        calibration->mutable_raw_bias()->set_i(values[0]);calibration->mutable_raw_bias()->set_j(values[1]);calibration->mutable_raw_bias()->set_k(values[2]);
        calibration->set_drift(values[3]);calibration->set_variance(values[4]);calibration->set_gyro_gain_setting("");
    } else {
        if(std::string(argv[2])!="--gain-only") return 2;
        calibration->set_drift(-1);calibration->set_variance(-1);calibration->set_gyro_gain_setting("2000deg/s");
    }
    if(PSM_Initialize("127.0.0.1",PSMOVESERVICE_DEFAULT_PORT,5000)!=PSMResult_Success) return 3;
    PSMRequestID requestId; Reply result;
    if(PSM_SendOpaqueRequest(&request,&requestId)!=PSMResult_RequestSent || PSM_RegisterCallback(requestId,reply,&result)!=PSMResult_Success) {PSM_Shutdown();return 4;}
    auto start=std::chrono::steady_clock::now();
    while(!result.done && std::chrono::steady_clock::now()-start<std::chrono::seconds(5)) {PSM_Update();std::this_thread::sleep_for(std::chrono::milliseconds(2));}
    if(!result.done) PSM_CancelCallback(requestId);
    PSM_Shutdown(); std::printf("Calibration response: completed=%d result=%d\n",result.done,(int)result.result);
    return result.done && result.result==PSMResult_Success?0:5;
}
