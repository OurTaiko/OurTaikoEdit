namespace OurTaikoEdit.App.Export;

// A frame's RGBA or BGRA pixels as YUV 4:2:0 (planar: all of Y, then U, then V, each chroma sample the
// average of a 2×2 block), BT.709 in the video range (16–235, 16–240): what x264 encodes. Done here,
// on every core, rather than by ffmpeg on one; it also makes what goes through the pipe 3/8 of the size.
public static class Yuv420
{
    // The bytes of a frame of this size (even width and height).
    public static int Size(int width, int height) => width * height * 3 / 2;

    // BT.709 limited range, ×65536: Y from R, G, B, and the chroma differences.
    const int YR = 11966, YG = 40254, YB = 4064;            // 0.1826, 0.6142, 0.0620
    const int UR = -6596, UG = -22189, UB = 28785;          // -0.1006, -0.3386, 0.4392
    const int VR = 28785, VG = -26145, VB = -2640;          // 0.4392, -0.3989, -0.0403
    const int Half = 1 << 15;

    public static void Convert(ReadOnlySpan<byte> pixels, Span<byte> yuv, int width, int height, bool rgba)
    {
        if ((width | height) % 2 != 0) throw new ArgumentException("The width and height have to be even.");
        if (pixels.Length < width * height * 4 || yuv.Length < Size(width, height)) throw new ArgumentException("A buffer is too small.");
        unsafe
        {
            fixed (byte* src = pixels)
            fixed (byte* dst = yuv)
            {
                var s = (IntPtr)src;
                var d = (IntPtr)dst;
                int pairs = height / 2;
                // About 16 pairs of rows a piece: enough work per piece, enough pieces for every core.
                int pieces = Math.Max(1, Math.Min(pairs, Environment.ProcessorCount * 4));
                Parallel.For(0, pieces, piece =>
                {
                    int from = pairs * piece / pieces, to = pairs * (piece + 1) / pieces;
                    for (int pair = from; pair < to; pair++) RowPair((byte*)s, (byte*)d, width, height, pair, rgba);
                });
            }
        }
    }

    static unsafe void RowPair(byte* src, byte* dst, int width, int height, int pair, bool rgba)
    {
        int r = rgba ? 0 : 2, b = rgba ? 2 : 0;
        int stride = width * 4;
        byte* top = src + (2 * pair) * stride, bottom = top + stride;
        byte* y0 = dst + (2 * pair) * width, y1 = y0 + width;
        byte* u = dst + width * height + pair * (width / 2);
        byte* v = dst + width * height + width * height / 4 + pair * (width / 2);
        for (int x = 0; x < width; x += 2)
        {
            byte* p = top + x * 4, q = bottom + x * 4;
            int r0 = p[r], g0 = p[1], b0 = p[b], r1 = p[4 + r], g1 = p[5], b1 = p[4 + b];
            int r2 = q[r], g2 = q[1], b2 = q[b], r3 = q[4 + r], g3 = q[5], b3 = q[4 + b];
            y0[x] = (byte)(16 + ((YR * r0 + YG * g0 + YB * b0 + Half) >> 16));
            y0[x + 1] = (byte)(16 + ((YR * r1 + YG * g1 + YB * b1 + Half) >> 16));
            y1[x] = (byte)(16 + ((YR * r2 + YG * g2 + YB * b2 + Half) >> 16));
            y1[x + 1] = (byte)(16 + ((YR * r3 + YG * g3 + YB * b3 + Half) >> 16));
            // The block's sums, so the average is taken once (÷4 in the shift).
            int rs = r0 + r1 + r2 + r3, gs = g0 + g1 + g2 + g3, bs = b0 + b1 + b2 + b3;
            u[x / 2] = (byte)(128 + ((UR * rs + UG * gs + UB * bs + (Half << 2)) >> 18));
            v[x / 2] = (byte)(128 + ((VR * rs + VG * gs + VB * bs + (Half << 2)) >> 18));
        }
    }
}
