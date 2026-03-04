using System;
using System.Buffers;
using System.IO;
using Microsoft.Xna.Framework;
using StbImageWriteSharp;

namespace BiomeDatasetCollector.Content;

public static class FramePngEncoder
{
    public static void EncodeToPng(Stream destination, Color[] pixels, int pixelCount, int width, int height)
    {
        if (destination is null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (pixels is null)
        {
            throw new ArgumentNullException(nameof(pixels));
        }

        if (width <= 0 || height <= 0 || pixelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelCount));
        }

        int expectedPixels = checked(width * height);
        if (pixelCount != expectedPixels || pixelCount > pixels.Length)
        {
            throw new ArgumentException("Pixel buffer length does not match frame dimensions.", nameof(pixels));
        }

        int byteCount = checked(pixelCount * 4);
        byte[] rgba = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            int offset = 0;
            for (int i = 0; i < pixelCount; i++)
            {
                Color color = pixels[i];
                rgba[offset++] = color.R;
                rgba[offset++] = color.G;
                rgba[offset++] = color.B;
                rgba[offset++] = color.A;
            }

            ImageWriter writer = new();
            writer.WritePng(rgba, width, height, ColorComponents.RedGreenBlueAlpha, destination);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rgba);
        }
    }
}
