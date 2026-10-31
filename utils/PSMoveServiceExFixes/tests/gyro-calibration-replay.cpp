// Replay local telemetry without sending any calibration requests to hardware.
// CSV: seconds, raw gyro deg/s XYZ, acceleration g XYZ, optical meters XYZ,
// optical quaternion XYZW, optical valid (0/1). Optional initial bias XYZ deg/s.
#include "GyroAutoCalibration.h"
#include <fstream>
#include <sstream>
#include <iostream>
#include <map>
int main(int argc,char** argv) {
    if(argc!=2 && argc!=5) return 2;
    std::ifstream file(argv[1]);if(!file) return 2;
    GyroAutoCalibration c;c.settings.enabled=true;
    Eigen::Vector3f bias=Eigen::Vector3f::Zero();
    if(argc==5) for(int i=0;i<3;i++) bias[i]=std::stof(argv[i+2]);
    std::map<std::string,int> reasons;std::string line;int samples=0;
    while(std::getline(file,line)) {
        std::replace(line.begin(),line.end(),',',' ');std::istringstream row(line);double v[15];
        for(double &x:v) if(!(row>>x)) return 3;
        Eigen::Vector3f gyro{float(v[1]),float(v[2]),float(v[3])};
        Eigen::Vector3f accel{float(v[4]),float(v[5]),float(v[6])};
        Eigen::Vector3f pos{float(v[7]),float(v[8]),float(v[9])};
        Eigen::Quaternionf q{float(v[13]),float(v[10]),float(v[11]),float(v[12])};
        c.observeOptical(v[0],v[14]!=0,pos,q);Eigen::Vector3f next;
        if(c.observeImu(v[0],gyro,accel,bias,next)) bias=next;
        ++samples;++reasons[c.reason];
    }
    std::cout<<"Samples: "<<samples<<"; accepted windows: "<<c.updates<<"; longest window: "<<c.longestWindow<<" s\nBias deg/s: "<<bias.transpose()<<"\n";
    for(const auto& pair:reasons) std::cout<<pair.first<<": "<<pair.second<<" samples\n";
    return 0;
}
