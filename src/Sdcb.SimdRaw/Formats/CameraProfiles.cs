namespace Sdcb.SimdRaw.Formats;

/// <summary>Per-model develop parameters that are not stored in the file.</summary>
/// <param name="CamXyz">XYZ(D65) → camera matrix ×10000, as published in Adobe DNG Converter (ColorMatrix2) and used by
/// dcraw / LibRaw / RawSpeed.</param>
/// <param name="White">Saturation level in decoded-mosaic units when the file does not carry one.</param>
/// <param name="CropRight">Columns on the right edge that do not carry image data.</param>
internal sealed record CameraProfile(string Make, string Model, short[] CamXyz, int? White = null, int CropRight = 0);

internal static class CameraProfiles
{
    private static readonly CameraProfile[] s_profiles =
    [
        new("SONY", "ILCE-7S", [5838, -1430, -246, -3497, 11477, 2297, -748, 1885, 5778], CropRight: 32),
        new("SONY", "ILCE-7RM2", [6629, -1900, -483, -4618, 12349, 2550, -622, 1381, 6514], CropRight: 32),
        new("SONY", "ILCE-7RM3", [6640, -1847, -503, -5238, 13010, 2474, -993, 1673, 6527], CropRight: 20),
        new("SONY", "ILCE-7RM3A", [6640, -1847, -503, -5238, 13010, 2474, -993, 1673, 6527], CropRight: 20),
        new("NIKON CORPORATION", "NIKON D6", [9028, -3423, -1035, -6321, 14265, 2217, -1013, 1683, 6928], White: 15520),
    ];

    public static CameraProfile? Find(string make, string model) =>
        Array.Find(s_profiles, p => string.Equals(p.Make, make, StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase));
}
