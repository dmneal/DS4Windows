// SPDX-License-Identifier: GPL-3.0-or-later
using System.Numerics;

namespace PSEyeCapture;

internal static class PoseGeometry
{
    // Sony's approximate external DS4 dimensions; a bounding box, not a body/IMU calibration.
    public static readonly Vector3 Dimensions = new(0.162f,0.052f,0.098f);
    public static Vector3[] Corners(Quaternion attitude, Vector3 center) {
        var half = Dimensions/2;
        var vertices = new Vector3[8];
        for (int i = 0; i < 8; i++) vertices[i] = Vector3.Transform(new Vector3(
            (i&1)==0?-half.X:half.X,(i&2)==0?-half.Y:half.Y,(i&4)==0?-half.Z:half.Z),attitude)+center;
        return vertices;
    }
}
