#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace UnityPrefabConverter;

// Decodes the composite image of PSD/PSB files and baseline TIFF files into 8-bit RGBA, with no Godot or package dependencies
public static class TextureDecoder {
// ----- VALUES -----
    public sealed class DecodedImage {
        public int Width;
        public int Height;
        public byte[] Rgba;
    }
    private const int PSD_MODE_BITMAP = 0;
    private const int PSD_MODE_GRAYSCALE = 1;
    private const int PSD_MODE_INDEXED = 2;
    private const int PSD_MODE_RGB = 3;
    private const int PSD_MODE_CMYK = 4;
    private const int PSD_MODE_MULTICHANNEL = 7;
    private const int PSD_MODE_DUOTONE = 8;
    private const int TIFF_PHOTOMETRIC_WHITE_IS_ZERO = 0;
    private const int TIFF_PHOTOMETRIC_BLACK_IS_ZERO = 1;
    private const int TIFF_PHOTOMETRIC_RGB = 2;
    private const int TIFF_PHOTOMETRIC_PALETTE = 3;
    private const int TIFF_PHOTOMETRIC_CMYK = 5;
    
    private sealed class BigEndianReader {
        public readonly byte[] Data;
        public int Position;
        public BigEndianReader(byte[] data) {
            Data = data;
        }
        public byte U8() {
            return Data[Position++];
        }
        public int U16() {
            Position += 2;
            return (Data[Position - 2] << 8) | Data[Position - 1];
        }
        public uint U32() {
            Position += 4;
            return (uint)((Data[Position - 4] << 24) | (Data[Position - 3] << 16) | (Data[Position - 2] << 8) | Data[Position - 1]);
        }
        public long U64() {
            return ((long)U32() << 32) | U32();
        }
    }
    
// ----- PUBLIC CALLS -----
    // Decoding
    public static DecodedImage Decode(string path) {
        var data = File.ReadAllBytes(path);
        if (data.Length >= 4 && data[0] == '8' && data[1] == 'B' && data[2] == 'P' && data[3] == 'S') return decodePsd(data);
        if (data.Length >= 4 && ((data[0] == 'I' && data[1] == 'I') || (data[0] == 'M' && data[1] == 'M'))) return decodeTiff(data);
        throw new InvalidDataException("not a PSD or TIFF file");
    }
    
// ----- HELPERS -----
    // PSD
    private static DecodedImage decodePsd(byte[] data) {
        var reader = new BigEndianReader(data) { Position = 4 };
        var version = reader.U16();
        if (version != 1 && version != 2) throw new InvalidDataException($"unknown PSD version {version}");
        reader.Position += 6;
        var channels = reader.U16();
        var height = (int)reader.U32();
        var width = (int)reader.U32();
        var depth = reader.U16();
        var mode = reader.U16();
        var colorData = readBlock(reader, (int)reader.U32());
        var resources = readBlock(reader, (int)reader.U32());
        var layersLength = version == 1 ? reader.U32() : reader.U64();
        var mergedAlpha = psdMergedAlpha(reader, layersLength, version);
        reader.Position += (int)layersLength;
        var compression = reader.U16();
        var planes = readPsdPlanes(reader, compression, channels, width, height, depth, version);
        var samples = new byte[channels][];
        for (var c = 0; c < channels; c++) samples[c] = psdToEightBit(planes[c], width, height, depth);
        var rgba = psdToRgba(samples, mode, width * height, colorData);
        if (mergedAlpha ?? (writtenByPhotoshop(resources) || isWhiteMatted(rgba))) removeWhiteMatte(rgba);
        return new DecodedImage { Width = width, Height = height, Rgba = rgba };
    }
    // A negative layer count marks the composite's first extra channel as the image's transparency, with colours blended over white.
    // Otherwise that channel is one the artist saved by hand and the colours are untouched. Null when the file doesn't say
    private static bool? psdMergedAlpha(BigEndianReader reader, long layersLength, int version) {
        var lengthBytes = version == 1 ? 4 : 8;
        if (layersLength < lengthBytes + 2) return null;
        var start = reader.Position;
        var infoLength = version == 1 ? reader.U32() : reader.U64();
        var count = (short)reader.U16();
        reader.Position = start;
        if (infoLength == 0) return null;
        return count < 0;
    }
    private static byte[][] readPsdPlanes(BigEndianReader reader, int compression, int channels, int width, int height, int depth, int version) {
        var rowBytes = (width * depth + 7) / 8;
        var planes = new byte[channels][];
        switch (compression) {
            case 0:
                for (var c = 0; c < channels; c++) planes[c] = readBlock(reader, rowBytes * height);
                return planes;
            case 1:
                var counts = new int[channels * height];
                for (var i = 0; i < counts.Length; i++) counts[i] = version == 1 ? reader.U16() : (int)reader.U32();
                for (var c = 0; c < channels; c++) {
                    planes[c] = new byte[rowBytes * height];
                    for (var y = 0; y < height; y++) {
                        var length = counts[c * height + y];
                        unpackBits(reader.Data, reader.Position, length, planes[c], y * rowBytes, rowBytes);
                        reader.Position += length;
                    }
                }
                return planes;
            case 2:
            case 3:
                var inflated = inflate(reader.Data, reader.Position, reader.Data.Length - reader.Position);
                for (var c = 0; c < channels; c++) {
                    planes[c] = new byte[rowBytes * height];
                    Array.Copy(inflated, c * rowBytes * height, planes[c], 0, Math.Min(rowBytes * height, Math.Max(inflated.Length - c * rowBytes * height, 0)));
                    if (compression == 3) undoPsdPrediction(planes[c], width, height, depth);
                }
                return planes;
            default:
                throw new InvalidDataException($"unsupported PSD compression {compression}");
        }
    }
    private static void undoPsdPrediction(byte[] plane, int width, int height, int depth) {
        var bytesPerSample = depth / 8;
        for (var y = 0; y < height; y++) {
            var row = y * width * bytesPerSample;
            if (depth == 8) {
                for (var x = 1; x < width; x++) plane[row + x] += plane[row + x - 1];
            } else if (depth == 16) {
                for (var x = 1; x < width; x++) {
                    var index = row + x * 2;
                    var value = ((plane[index] << 8) | plane[index + 1]) + ((plane[index - 2] << 8) | plane[index - 1]);
                    plane[index] = (byte)(value >> 8);
                    plane[index + 1] = (byte)value;
                }
            } else if (depth == 32) {
                // 32-bit prediction stores the bytes of each row split into four planes before the delta
                var rowBytes = width * 4;
                for (var x = 1; x < rowBytes; x++) plane[row + x] += plane[row + x - 1];
                var shuffled = new byte[rowBytes];
                for (var x = 0; x < width; x++) {
                    for (var b = 0; b < 4; b++) shuffled[x * 4 + b] = plane[row + b * width + x];
                }
                Array.Copy(shuffled, 0, plane, row, rowBytes);
            }
        }
    }
    private static byte[] psdToRgba(byte[][] samples, int mode, int pixels, byte[] colorData) {
        var rgba = new byte[pixels * 4];
        var channels = samples.Length;
        for (var i = 0; i < pixels; i++) {
            byte r, g, b, a = 255;
            switch (mode) {
                case PSD_MODE_RGB:
                    r = samples[0][i];
                    g = channels > 1 ? samples[1][i] : r;
                    b = channels > 2 ? samples[2][i] : r;
                    if (channels > 3) a = samples[3][i];
                    break;
                case PSD_MODE_CMYK:
                    var k = channels > 3 ? samples[3][i] : 255;
                    r = (byte)(samples[0][i] * k / 255);
                    g = (byte)(samples[1][i] * k / 255);
                    b = (byte)(samples[2][i] * k / 255);
                    if (channels > 4) a = samples[4][i];
                    break;
                case PSD_MODE_INDEXED:
                    var index = samples[0][i];
                    r = colorData.Length >= 768 ? colorData[index] : index;
                    g = colorData.Length >= 768 ? colorData[256 + index] : index;
                    b = colorData.Length >= 768 ? colorData[512 + index] : index;
                    if (channels > 1) a = samples[1][i];
                    break;
                case PSD_MODE_BITMAP:
                    r = g = b = (byte)(255 - samples[0][i]);
                    break;
                case PSD_MODE_GRAYSCALE:
                case PSD_MODE_DUOTONE:
                case PSD_MODE_MULTICHANNEL:
                    r = g = b = samples[0][i];
                    if (channels > 1 && mode != PSD_MODE_MULTICHANNEL) a = samples[1][i];
                    break;
                default:
                    throw new InvalidDataException($"unsupported PSD color mode {mode}");
            }
            rgba[i * 4] = r;
            rgba[i * 4 + 1] = g;
            rgba[i * 4 + 2] = b;
            rgba[i * 4 + 3] = a;
        }
        return rgba;
    }
    
    // TIFF
    private static DecodedImage decodeTiff(byte[] data) {
        var little = data[0] == 'I';
        if (readU16(data, 2, little) != 42) throw new InvalidDataException("BigTIFF is not supported");
        var tags = readIfd(data, (int)readU32(data, 4, little), little);
        var width = (int)tagValue(tags, 256, 0);
        var height = (int)tagValue(tags, 257, 0);
        var samplesPerPixel = (int)tagValue(tags, 277, 1);
        var bits = (int)tagValue(tags, 258, 1);
        var compression = (int)tagValue(tags, 259, 1);
        var photometric = (int)tagValue(tags, 262, TIFF_PHOTOMETRIC_BLACK_IS_ZERO);
        var planar = (int)tagValue(tags, 284, 1);
        var predictor = (int)tagValue(tags, 317, 1);
        var sampleFormat = (int)tagValue(tags, 339, 1);
        if (width <= 0 || height <= 0) throw new InvalidDataException("invalid TIFF size");
        var tiled = tags.ContainsKey(322);
        var chunkWidth = tiled ? (int)tagValue(tags, 322, width) : width;
        var chunkHeight = tiled ? (int)tagValue(tags, 323, height) : (int)Math.Min(tagValue(tags, 278, height), height);
        var offsets = tags.TryGetValue(tiled ? 324 : 273, out var offsetValues) ? offsetValues : throw new InvalidDataException("TIFF has no image data");
        var byteCounts = tags.TryGetValue(tiled ? 325 : 279, out var countValues) ? countValues : null;
        var planeSamples = planar == 2 ? 1 : samplesPerPixel;
        var planesCount = planar == 2 ? samplesPerPixel : 1;
        var rowBytes = (width * planeSamples * bits + 7) / 8;
        var planes = new byte[planesCount][];
        for (var p = 0; p < planesCount; p++) planes[p] = new byte[rowBytes * height];
        var chunksAcross = (width + chunkWidth - 1) / chunkWidth;
        var chunksDown = (height + chunkHeight - 1) / chunkHeight;
        var chunkRowBytes = (chunkWidth * planeSamples * bits + 7) / 8;
        for (var chunk = 0; chunk < offsets.Length; chunk++) {
            var plane = chunk / (chunksAcross * chunksDown);
            if (plane >= planesCount) break;
            var index = chunk % (chunksAcross * chunksDown);
            var length = byteCounts != null && chunk < byteCounts.Length ? (int)byteCounts[chunk] : data.Length - (int)offsets[chunk];
            var decoded = decompressTiff(data, (int)offsets[chunk], length, compression, chunkRowBytes * chunkHeight);
            if (predictor == 2) undoTiffPrediction(decoded, chunkRowBytes, chunkWidth, planeSamples, bits, little);
            var originX = index % chunksAcross * chunkWidth;
            var originY = index / chunksAcross * chunkHeight;
            var copyBytes = Math.Min(chunkRowBytes, rowBytes - (originX * planeSamples * bits) / 8);
            for (var y = 0; y < chunkHeight && originY + y < height; y++) {
                var source = y * chunkRowBytes;
                if (source >= decoded.Length) break;
                Array.Copy(decoded, source, planes[plane], (originY + y) * rowBytes + originX * planeSamples * bits / 8, Math.Min(copyBytes, decoded.Length - source));
            }
        }
        var colorMap = tags.TryGetValue(320, out var map) ? map : null;
        return new DecodedImage { Width = width, Height = height, Rgba = tiffToRgba(planes, planar, width, height, samplesPerPixel, bits, photometric, sampleFormat, little, colorMap) };
    }
    private static Dictionary<int, long[]> readIfd(byte[] data, int offset, bool little) {
        var tags = new Dictionary<int, long[]>();
        var count = readU16(data, offset, little);
        for (var i = 0; i < count; i++) {
            var entry = offset + 2 + i * 12;
            var tag = readU16(data, entry, little);
            var type = readU16(data, entry + 2, little);
            var valueCount = (int)readU32(data, entry + 4, little);
            var size = type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 => 4, 5 or 10 or 12 => 8, _ => 1 };
            var position = size * valueCount <= 4 ? entry + 8 : (int)readU32(data, entry + 8, little);
            var values = new long[valueCount];
            for (var v = 0; v < valueCount && position + v * size + size <= data.Length; v++) {
                values[v] = size switch {
                    1 => data[position + v],
                    2 => readU16(data, position + v * 2, little),
                    _ => readU32(data, position + v * size, little)
                };
            }
            tags[tag] = values;
        }
        return tags;
    }
    private static byte[] decompressTiff(byte[] data, int offset, int length, int compression, int expected) {
        length = Math.Min(length, data.Length - offset);
        switch (compression) {
            case 1:
                var raw = new byte[length];
                Array.Copy(data, offset, raw, 0, length);
                return raw;
            case 5:
                return decodeLzw(data, offset, length, expected);
            case 8:
            case 32946:
                return inflate(data, offset, length);
            case 32773:
                var output = new byte[expected];
                unpackBits(data, offset, length, output, 0, expected);
                return output;
            default:
                throw new InvalidDataException(compression == 7 ? "JPEG-compressed TIFF is not supported" : $"unsupported TIFF compression {compression}");
        }
    }
    private static void undoTiffPrediction(byte[] buffer, int rowBytes, int width, int samples, int bits, bool little) {
        for (var row = 0; row + rowBytes <= buffer.Length; row += rowBytes) {
            if (bits == 8) {
                for (var i = samples; i < width * samples; i++) buffer[row + i] += buffer[row + i - samples];
            } else if (bits == 16) {
                for (var i = samples; i < width * samples; i++) {
                    var value = readU16(buffer, row + i * 2, little) + readU16(buffer, row + (i - samples) * 2, little);
                    writeU16(buffer, row + i * 2, value, little);
                }
            }
        }
    }
    private static byte[] tiffToRgba(byte[][] planes, int planar, int width, int height, int samples, int bits, int photometric, int sampleFormat, bool little, long[] colorMap) {
        var pixels = width * height;
        var channel = new byte[samples][];
        for (var s = 0; s < samples; s++) channel[s] = new byte[pixels];
        var rowBytes = planar == 2 ? (width * bits + 7) / 8 : (width * samples * bits + 7) / 8;
        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                for (var s = 0; s < samples; s++) {
                    var plane = planar == 2 ? planes[s] : planes[0];
                    var sampleIndex = planar == 2 ? x : x * samples + s;
                    channel[s][y * width + x] = tiffSample(plane, y * rowBytes, sampleIndex, bits, sampleFormat, little, photometric == TIFF_PHOTOMETRIC_PALETTE);
                }
            }
        }
        var rgba = new byte[pixels * 4];
        var paletteSize = 1 << Math.Min(bits, 16);
        for (var i = 0; i < pixels; i++) {
            byte r, g, b, a = 255;
            switch (photometric) {
                case TIFF_PHOTOMETRIC_RGB:
                    r = channel[0][i];
                    g = samples > 1 ? channel[1][i] : r;
                    b = samples > 2 ? channel[2][i] : r;
                    if (samples > 3) a = channel[3][i];
                    break;
                case TIFF_PHOTOMETRIC_PALETTE:
                    var index = channel[0][i];
                    r = colorMap != null ? (byte)(colorMap[index] >> 8) : index;
                    g = colorMap != null ? (byte)(colorMap[paletteSize + index] >> 8) : index;
                    b = colorMap != null ? (byte)(colorMap[paletteSize * 2 + index] >> 8) : index;
                    break;
                case TIFF_PHOTOMETRIC_CMYK:
                    var k = 255 - channel[3][i];
                    r = (byte)((255 - channel[0][i]) * k / 255);
                    g = (byte)((255 - channel[1][i]) * k / 255);
                    b = (byte)((255 - channel[2][i]) * k / 255);
                    if (samples > 4) a = channel[4][i];
                    break;
                default:
                    r = g = b = photometric == TIFF_PHOTOMETRIC_WHITE_IS_ZERO ? (byte)(255 - channel[0][i]) : channel[0][i];
                    if (samples > 1) a = channel[1][i];
                    break;
            }
            rgba[i * 4] = r;
            rgba[i * 4 + 1] = g;
            rgba[i * 4 + 2] = b;
            rgba[i * 4 + 3] = a;
        }
        return rgba;
    }
    // Palette indices keep their raw value; other samples are scaled to 0-255
    private static byte tiffSample(byte[] plane, int rowStart, int index, int bits, int sampleFormat, bool little, bool rawIndex) {
        switch (bits) {
            case 8:
                return plane[rowStart + index];
            case 16:
                var value16 = readU16(plane, rowStart + index * 2, little);
                return rawIndex ? (byte)value16 : (byte)(value16 >> 8);
            case 32:
                var offset = rowStart + index * 4;
                var raw = readU32(plane, offset, little);
                if (sampleFormat == 3) return linearToSrgb(BitConverter.Int32BitsToSingle((int)raw));
                return (byte)(raw >> 24);
            default:
                var bitOffset = index * bits;
                var current = plane[rowStart + bitOffset / 8];
                var sample = (current >> (8 - bits - bitOffset % 8)) & ((1 << bits) - 1);
                return rawIndex ? (byte)sample : (byte)(sample * 255 / ((1 << bits) - 1));
        }
    }
    
    // Compression
    private static void unpackBits(byte[] source, int offset, int length, byte[] output, int outputOffset, int outputLength) {
        var end = offset + length;
        var written = 0;
        while (offset < end && written < outputLength) {
            var header = (sbyte)source[offset++];
            if (header >= 0) {
                var count = Math.Min(header + 1, Math.Min(outputLength - written, end - offset));
                Array.Copy(source, offset, output, outputOffset + written, count);
                offset += header + 1;
                written += count;
            } else if (header != -128) {
                var count = Math.Min(1 - header, outputLength - written);
                var value = source[offset++];
                for (var i = 0; i < count; i++) output[outputOffset + written + i] = value;
                written += count;
            }
        }
    }
    private static byte[] inflate(byte[] data, int offset, int length) {
        using var input = new MemoryStream(data, offset, length);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        try {
            zlib.CopyTo(output);
        } catch (InvalidDataException) {
        }
        return output.ToArray();
    }
    private static byte[] decodeLzw(byte[] data, int offset, int length, int expected) {
        var output = new List<byte>(expected);
        var table = new List<byte[]>(4096);
        resetLzwTable(table);
        var bitPosition = (long)offset * 8;
        var end = (long)(offset + length) * 8;
        var codeBits = 9;
        byte[] previous = null;
        while (bitPosition + codeBits <= end) {
            var code = 0;
            for (var i = 0; i < codeBits; i++) {
                var bit = (data[(bitPosition + i) >> 3] >> (7 - (int)((bitPosition + i) & 7))) & 1;
                code = (code << 1) | bit;
            }
            bitPosition += codeBits;
            if (code == 257) break;
            if (code == 256) {
                resetLzwTable(table);
                codeBits = 9;
                previous = null;
                continue;
            }
            byte[] entry;
            if (code < table.Count) {
                entry = table[code];
                if (previous != null) table.Add(concat(previous, entry[0]));
            } else if (previous != null) {
                entry = concat(previous, previous[0]);
                table.Add(entry);
            } else {
                break;
            }
            output.AddRange(entry);
            previous = entry;
            if (table.Count + 1 >= 1 << codeBits && codeBits < 12) codeBits++;
        }
        return output.ToArray();
    }
    private static void resetLzwTable(List<byte[]> table) {
        table.Clear();
        for (var i = 0; i < 256; i++) table.Add(new[] { (byte)i });
        table.Add(Array.Empty<byte>());
        table.Add(Array.Empty<byte>());
    }
    private static byte[] concat(byte[] prefix, byte last) {
        var result = new byte[prefix.Length + 1];
        Array.Copy(prefix, result, prefix.Length);
        result[prefix.Length] = last;
        return result;
    }
    
    private static bool writtenByPhotoshop(byte[] resources) {
        var marker = System.Text.Encoding.BigEndianUnicode.GetBytes("Adobe Photoshop");
        return resources.AsSpan().IndexOf(marker) >= 0;
    }
    // Photoshop stores semi-transparent composite pixels blended over white; fully transparent pixels being pure white gives it away
    private static bool isWhiteMatted(byte[] rgba) {
        var transparent = 0;
        for (var i = 0; i < rgba.Length; i += 4) {
            if (rgba[i + 3] != 0) continue;
            if (rgba[i] != 255 || rgba[i + 1] != 255 || rgba[i + 2] != 255) return false;
            transparent++;
        }
        return transparent > 0;
    }
    private static void removeWhiteMatte(byte[] rgba) {
        for (var i = 0; i < rgba.Length; i += 4) {
            int alpha = rgba[i + 3];
            if (alpha == 255) continue;
            for (var c = 0; c < 3; c++) {
                rgba[i + c] = alpha == 0 ? (byte)0 : (byte)Math.Clamp(((rgba[i + c] - 255 + alpha) * 255 + alpha / 2) / alpha, 0, 255);
            }
        }
    }
    // 32-bit float images hold linear light
    private static byte linearToSrgb(float value) {
        value = Math.Clamp(value, 0f, 1f);
        var srgb = value <= 0.0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1f / 2.4f) - 0.055f;
        return (byte)Math.Clamp(srgb * 255f + 0.5f, 0, 255);
    }
    
    // Samples and bytes
    private static byte[] psdToEightBit(byte[] plane, int width, int height, int depth) {
        if (depth == 8) return plane;
        var result = new byte[width * height];
        var rowBytes = (width + 7) / 8;
        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                var i = y * width + x;
                result[i] = depth switch {
                    1 => (byte)(((plane[y * rowBytes + x / 8] >> (7 - x % 8)) & 1) * 255),
                    16 => plane[i * 2],
                    32 => linearToSrgb(BitConverter.Int32BitsToSingle((int)readU32(plane, i * 4, false))),
                    _ => 0
                };
            }
        }
        return result;
    }
    private static byte[] readBlock(BigEndianReader reader, int length) {
        var block = new byte[Math.Max(length, 0)];
        Array.Copy(reader.Data, reader.Position, block, 0, Math.Min(block.Length, reader.Data.Length - reader.Position));
        reader.Position += length;
        return block;
    }
    private static long tagValue(Dictionary<int, long[]> tags, int tag, long fallback) {
        return tags.TryGetValue(tag, out var values) && values.Length > 0 ? values[0] : fallback;
    }
    private static int readU16(byte[] data, int offset, bool little) {
        return little ? data[offset] | (data[offset + 1] << 8) : (data[offset] << 8) | data[offset + 1];
    }
    private static uint readU32(byte[] data, int offset, bool little) {
        return little
            ? (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24))
            : (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
    }
    private static void writeU16(byte[] data, int offset, int value, bool little) {
        if (little) {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
        } else {
            data[offset] = (byte)(value >> 8);
            data[offset + 1] = (byte)value;
        }
    }
}
#endif
