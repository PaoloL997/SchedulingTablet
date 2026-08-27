namespace Brembana;

internal static class ImageSharpnessHelper
{
    /// <summary>Minimum Laplacian variance considered acceptably sharp for OCR ROI.</summary>
    public const double MinimumSharpnessScore = 80.0;

    public static double ComputeLaplacianVariance(byte[] bgra, uint width, uint height)
    {
        if (bgra.Length < 4 || width < 3 || height < 3)
            return 0;

        var w = (int)width;
        var h = (int)height;
        var gray = new double[w * h];

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var offset = (y * w + x) * 4;
                gray[y * w + x] = 0.299 * bgra[offset + 2] + 0.587 * bgra[offset + 1] + 0.114 * bgra[offset];
            }
        }

        double sum = 0;
        double sumSq = 0;
        var count = 0;

        for (var y = 1; y < h - 1; y++)
        {
            for (var x = 1; x < w - 1; x++)
            {
                var center = gray[y * w + x];
                var laplacian =
                    gray[(y - 1) * w + x] +
                    gray[(y + 1) * w + x] +
                    gray[y * w + (x - 1)] +
                    gray[y * w + (x + 1)] -
                    4 * center;

                sum += laplacian;
                sumSq += laplacian * laplacian;
                count++;
            }
        }

        if (count == 0)
            return 0;

        var mean = sum / count;
        return sumSq / count - mean * mean;
    }

    public static bool IsSharpEnough(double score) => score >= MinimumSharpnessScore;
}
