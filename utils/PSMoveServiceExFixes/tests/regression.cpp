// Regression tests compile the actual patched upstream filter/math sources.
#include "OrientationFilter.h"
#include "MathEigen.h"
#include "OpticalCalibrationValidation.h"
#include <iostream>
#include <stdexcept>
#include <cmath>
#include <limits>

static void require(bool ok,const char* message) { if(!ok) throw std::runtime_error(message); }
static float angle(const Eigen::Quaternionf& a,const Eigen::Quaternionf& b) {
    if(!a.coeffs().allFinite() || !b.coeffs().allFinite() || a.norm()<.5f || b.norm()<.5f)
        return std::numeric_limits<float>::infinity();
    return 2.f*acosf(fminf(1.f,fabsf(a.normalized().dot(b.normalized()))));
}
int main() {
    require(optical_calibration::valid_curve(.12f,-.002f,100,1000),"normal decreasing optical-noise curve must be accepted");
    require(optical_calibration::valid_curve(.01f,0.f,100,1000),"constant finite variance is a valid curve");
    require(!optical_calibration::valid_curve(.12f,-.002f,100,110),"insufficient distance/projection coverage must not be saved");
    require(!optical_calibration::valid_curve(0,-.002f,100,1000),"zero variance coefficient must not be saved");
    require(!optical_calibration::valid_curve(.12f,.002f,100,1000),"increasing optical variance fit must be rejected");
    require(!optical_calibration::valid_curve(std::numeric_limits<float>::quiet_NaN(),-.002f,100,1000),"nonfinite fit must not be saved");
    require(!optical_calibration::valid_curve(.12f,-1.f,100,1000),"underflowing fit must not be saved");
    const float pi=3.14159265358979323846f;
    auto identity=Eigen::Quaternionf::Identity();
    Eigen::Quaternionf equivalent(-identity.coeffs());
    require(angle(eigen_quaternion_normalized_lerp(identity,equivalent,.5f),identity)<1e-5f,"antipodal same rotation must not produce NaN");
    Eigen::Quaternionf longYaw(Eigen::AngleAxisf(350.f*pi/180.f,Eigen::Vector3f::UnitY()));
    Eigen::Quaternionf halfway(Eigen::AngleAxisf(-5.f*pi/180.f,Eigen::Vector3f::UnitY()));
    require(angle(eigen_quaternion_normalized_lerp(identity,longYaw,.5f),halfway)<.001f,"interpolation must take short quaternion arc");
    OrientationFilterConstants constants; constants.clear();
    constants.tracking_shape.shape_type=LightBar;
    constants.gravity_calibration_direction=Eigen::Vector3f::UnitY();
    constants.orientation_variance_curve.A=.01f; constants.orientation_variance_curve.B=0;
    constants.orientation_variance_curve.MaxValue=1;
    Eigen::Quaternionf initial(Eigen::AngleAxisf(pi/3,Eigen::Vector3f::UnitY()));
    OrientationFilterComplementaryOpticalARG filter; filter.init(constants,initial);
    PoseFilterPacket packet; packet.clear(); packet.isSynced=true;
    packet.tracking_projection_area_px_sqr=500; // Position accepted; orientation rejected.
    auto now=std::chrono::high_resolution_clock::now();
    for(int i=0;i<30;i++) filter.update(now+std::chrono::milliseconds(i*16),packet);
    require(angle(filter.getOrientation(),initial)<.001f,"position-only packet must not pull orientation toward identity");
    packet.has_valid_optical_orientation=true;
    for(int i=30;i<60;i++) filter.update(now+std::chrono::milliseconds(i*16),packet);
    require(angle(filter.getOrientation(),initial)>.02f,"accepted optical orientation must still correct the estimate");
    packet.clear(); require(!packet.has_valid_optical_orientation,"cleared packet must not claim optical orientation");
    OrientationFilterPassThru opticalOnly; opticalOnly.init(constants,initial);
    packet.isSynced=true; packet.tracking_projection_area_px_sqr=500;
    opticalOnly.update(now,packet);
    require(angle(opticalOnly.getOrientation(),initial)<.001f,"optical-only mode must also reject position-only packets");
    packet.has_valid_optical_orientation=true; opticalOnly.update(now+std::chrono::milliseconds(16),packet);
    require(angle(opticalOnly.getOrientation(),identity)<.001f,"optical-only mode must publish accepted optical orientation");
    std::cout<<"PASS: quaternion hemisphere and independent optical validity regressions\n";
}
