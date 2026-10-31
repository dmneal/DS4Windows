#include "GyroAutoCalibration.h"
#include <stdexcept>
#include <iostream>
#include <limits>
static void check(bool b,const char* s) {if(!b) throw std::runtime_error(s);}
static Eigen::Vector3f run(GyroAutoCalibration& c,int hz,double seconds,float yawRate=0.f,bool camera=true,bool noisy=false) {
    Eigen::Vector3f bias=Eigen::Vector3f::Zero();
    const Eigen::Vector3f raw(.015f,-.010f,.005f), accel(0,1,0);
    double lastOptical=-1.;
    for(int i=0;i<=int(seconds*hz);++i) {
        double t=double(i)/hz;
        if(lastOptical<0 || t-lastOptical>=1./60.-1e-6) {
            Eigen::Quaternionf q(Eigen::AngleAxisf(float(t*yawRate*3.141592653589793/180.),Eigen::Vector3f::UnitY()));
            if(i%2) q.coeffs()=-q.coeffs(); // Antipodal equivalent camera rotations.
            c.observeOptical(t+.005,camera,Eigen::Vector3f::Zero(),q); lastOptical=t; // Cross-batch camera/IMU skew.
        }
        Eigen::Vector3f next;
        Eigen::Vector3f g=raw;g.y()+=yawRate;
        if(noisy) g.x()+=.10f*std::sin(float(i)*1.7f);
        if(c.observeImu(t,g,accel,bias,next)) bias=next;
        if(t<c.settings.window) check(c.updates==0,"must wait the complete time window independent of sample rate");
    }
    return bias;
}
int main() {
    for(int hz:{100,800}) {
        GyroAutoCalibration c;c.settings.enabled=true;
        auto b=run(c,hz,5.2);
        check(c.updates==1,"stationary window should update at both sample rates");
        check(b.norm()<=.010001f && b.y()<0,"bounded update must approach actual bias");
    }
    GyroAutoCalibration noCamera;noCamera.settings.enabled=true;run(noCamera,100,5.2,0,false);check(noCamera.updates==1,"IMU-only stillness must work with lost or jittery optical tracking");
    GyroAutoCalibration slowImu;slowImu.settings.enabled=true;run(slowImu,100,7,.2f,false);check(slowImu.updates==0,"mean residual rate must reject slow rotation in IMU-only mode");
    GyroAutoCalibration disabled;run(disabled,100,7);check(disabled.updates==0,"disabled must not learn");
    GyroAutoCalibration noise;noise.settings.enabled=true;run(noise,800,5.2,0,true,true);check(noise.updates==1,"sensor noise must not continuously reset valid stillness windows");
    GyroAutoCalibration lost;lost.settings.enabled=true;lost.settings.requireOptical=true;run(lost,100,7,0,false);check(lost.updates==0,"camera loss must block learning");
    GyroAutoCalibration slow;slow.settings.enabled=true;slow.settings.requireOptical=true;slow.settings.gyroLimit=.35;slow.settings.window=10;run(slow,100,20,.2f);check(slow.updates==0,"camera must reject steady slow yaw below gyro threshold");
    GyroAutoCalibration fast;fast.settings.enabled=true;run(fast,100,7,2);check(fast.updates==0,"movement must block learning");
    GyroAutoCalibration capped;capped.settings.enabled=true;capped.settings.maxTotal=.02;
    auto b=run(capped,100,40);check(b.norm()<=.020001f,"session limit must bound accumulated correction");
    GyroAutoCalibration invalid;invalid.settings.enabled=true;Eigen::Vector3f next;
    invalid.observeOptical(0,true,Eigen::Vector3f::Zero(),Eigen::Quaternionf::Identity());
    check(!invalid.observeImu(0,Eigen::Vector3f::Constant(std::numeric_limits<float>::quiet_NaN()),Eigen::Vector3f::UnitY(),Eigen::Vector3f::Zero(),next),"NaN rejected");
    GyroAutoCalibration gap;gap.settings.enabled=true;gap.settings.requireOptical=true;
    gap.observeOptical(0,true,Eigen::Vector3f::Zero(),Eigen::Quaternionf::Identity());
    gap.observeImu(0,Eigen::Vector3f::Zero(),Eigen::Vector3f::UnitY(),Eigen::Vector3f::Zero(),next);
    check(!gap.observeImu(.3,Eigen::Vector3f::Zero(),Eigen::Vector3f::UnitY(),Eigen::Vector3f::Zero(),next) && gap.progress==0,"stale camera must reset window");
    std::cout<<"PASS: timed stillness, camera loss, slow yaw, movement, bounds, NaN and gaps\n";
}
