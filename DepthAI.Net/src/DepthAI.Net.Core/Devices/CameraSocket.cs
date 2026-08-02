namespace DepthAI.Devices;

/// <summary>
/// Socket kamera fisik pada board OAK. Penamaan mengikuti konvensi depthai-core V3
/// (CAM_A..CAM_H) dengan alias yang lebih mudah dibaca.
/// </summary>
public enum CameraSocket
{
    Auto = -1,
    CamA = 0,
    CamB = 1,
    CamC = 2,
    CamD = 3,
    CamE = 4,
    CamF = 5,
    CamG = 6,
    CamH = 7,

    /// <summary>Alias CAM_A — kamera warna utama pada sebagian besar board OAK.</summary>
    Rgb = CamA,

    /// <summary>Alias CAM_B — kamera mono kiri.</summary>
    Left = CamB,

    /// <summary>Alias CAM_C — kamera mono kanan.</summary>
    Right = CamC,
}
