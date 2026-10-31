param(
    [Parameter(Mandatory=$true)][string]$PsmSource,
    [Parameter(Mandatory=$true)][string]$DependencyRoot,
    [Parameter(Mandatory=$true)][string]$BuildDirectory
)
# Run in a VS 2022 x64 developer shell, or import the local portable native-env.ps1.
# Build dependencies as documented in README.md before invoking this script.
$ErrorActionPreference='Stop'
$source=(Resolve-Path $PsmSource).Path.Replace('\','/')
$deps=(Resolve-Path $DependencyRoot).Path.Replace('\','/')
python "$PSScriptRoot/apply-patches.py" $source
if($LASTEXITCODE -ne 0) {throw 'Native source patch verification failed'}
$arguments=@('-S',$source,'-B',$BuildDirectory,'-G','Ninja',
    '-DCMAKE_BUILD_TYPE=Release',"-DOpenCV_DIR=$deps/build-opencv",
    "-DBOOST_ROOT=$deps/boost_1_78_0","-DBOOST_LIBRARYDIR=$deps/boost_1_78_0/stage/lib",'-DBoost_NO_BOOST_CMAKE=ON',
    "-DPROTOBUF_INCLUDE_DIR=$source/thirdparty/protobuf/src","-DPROTOBUF_LIBRARY=$deps/build-protobuf/libprotobuf.lib",
    "-DPROTOBUF_PROTOC_EXECUTABLE=$deps/build-protobuf/protoc.exe","-DSDL2_INCLUDE_DIR=$source/thirdparty/SDL2/include",
    "-DSDL2_LIBRARY=$deps/build-sdl/SDL2main.lib;$deps/build-sdl/SDL2.lib;opengl32.lib;winmm.lib;imm32.lib;version.lib;setupapi.lib",
    "-DLIBUSB_INCLUDE_DIR=$deps/libusb/include","-DLIBUSB_LIBRARIES=$deps/libusb/VS2015/MS64/static/libusb-1.0.lib",
    '-DCMAKE_CXX_FLAGS=/DWIN32 /D_WINDOWS /EHsc /DBOOST_ALL_NO_LIB /D_CRT_SECURE_NO_WARNINGS /DNOMINMAX /DWIN32_LEAN_AND_MEAN')
cmake @arguments
if($LASTEXITCODE -ne 0) {throw 'Native configuration failed'}
cmake --build $BuildDirectory --target PSMoveService PSMoveConfigTool --parallel 8
if($LASTEXITCODE -ne 0) {throw 'Native build failed'}
cmake -S "$PSScriptRoot/tests" -B "$BuildDirectory/regressions" -G Ninja "-DPSM_SOURCE=$source" -DCMAKE_BUILD_TYPE=Release
if($LASTEXITCODE -ne 0) {throw 'Native test configuration failed'}
cmake --build "$BuildDirectory/regressions" --parallel 8
if($LASTEXITCODE -ne 0) {throw 'Native test build failed'}
ctest --test-dir "$BuildDirectory/regressions" --output-on-failure
if($LASTEXITCODE -ne 0) {throw 'Native filter regressions failed'}
