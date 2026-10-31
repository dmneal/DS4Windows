cmake_minimum_required(VERSION 3.20)
project(PSMDS4Regressions LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 14)
set(CMAKE_MSVC_RUNTIME_LIBRARY MultiThreaded)
if(NOT PSM_SOURCE)
  message(FATAL_ERROR "Pass -DPSM_SOURCE=path/to/patched/PSMoveServiceEx")
endif()
set(MATH "${PSM_SOURCE}/src/psmovemath")
set(SERVICE "${PSM_SOURCE}/src/psmoveservice")
add_executable(ds4-regressions regression.cpp
  "${MATH}/MathEigen.cpp" "${MATH}/MathUtility.cpp" "${MATH}/MathAlignment.cpp"
  "${SERVICE}/Filter/OrientationFilter.cpp" "${SERVICE}/Filter/PoseFilterInterface.cpp"
  "${SERVICE}/Server/ServerLog.cpp")
target_compile_definitions(ds4-regressions PRIVATE IS_TESTING_KALMAN _CRT_SECURE_NO_WARNINGS)
target_include_directories(ds4-regressions PRIVATE "${PSM_SOURCE}/thirdparty/eigen" "${PSM_SOURCE}/thirdparty/glm"
  "${MATH}" "${SERVICE}/Filter" "${SERVICE}/Server" "${SERVICE}/Device/Interface")
enable_testing()
add_test(NAME ds4-regressions COMMAND ds4-regressions)
