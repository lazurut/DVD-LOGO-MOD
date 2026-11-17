using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DVDLogoMod
{
    public struct GifFrame
    {
        public Texture2D Texture;
        public float DelaySeconds;
    }

    public static class GifDecoder
    {
        private const int MaxLzwBits = 12;
        private const int MaxLzwCodes = 1 << MaxLzwBits;





        public static List<GifFrame> Decode(byte[] bytes)
        {
            var frames = new List<GifFrame>();

            using (var ms = new MemoryStream(bytes))
            using (var br = new BinaryReader(ms))
            {
                byte[] sig = br.ReadBytes(6);
                string sigStr = System.Text.Encoding.ASCII.GetString(sig);
                if (sigStr != "GIF87a" && sigStr != "GIF89a")
                    throw new InvalidDataException("Not a GIF file (no GIF87a/GIF89a signature).");

                ushort screenWidth = br.ReadUInt16();
                ushort screenHeight = br.ReadUInt16();
                byte packedFields = br.ReadByte();
                br.ReadByte();
                br.ReadByte();

                bool hasGlobalColorTable = (packedFields & 0x80) != 0;
                int globalColorTableSize = 2 << (packedFields & 0x07);

                Color32[] globalColorTable = null;
                if (hasGlobalColorTable)
                    globalColorTable = ReadColorTable(br, globalColorTableSize);

                Color32[] canvas = new Color32[screenWidth * screenHeight];
                Color32[] previousCanvas = null;

                int transparentIndex = -1;
                float frameDelay = 0.1f;
                int disposalMethod = 0;

                bool reading = true;
                while (reading)
                {
                    byte blockType = br.ReadByte();

                    switch (blockType)
                    {
                        case 0x21:
                            {
                                byte label = br.ReadByte();
                                if (label == 0xF9)
                                {
                                    byte blockSize = br.ReadByte();
                                    byte flags = br.ReadByte();
                                    ushort delayCentiseconds = br.ReadUInt16();
                                    byte transIdx = br.ReadByte();
                                    br.ReadByte();

                                    disposalMethod = (flags >> 2) & 0x07;
                                    bool hasTransparency = (flags & 0x01) != 0;
                                    transparentIndex = hasTransparency ? transIdx : -1;

                                    frameDelay = delayCentiseconds > 0
                                        ? delayCentiseconds / 100f
                                        : 0.1f;
                                }
                                else
                                {
                                    SkipSubBlocks(br);
                                }
                                break;
                            }

                        case 0x2C:
                            {
                                ushort imgLeft = br.ReadUInt16();
                                ushort imgTop = br.ReadUInt16();
                                ushort imgWidth = br.ReadUInt16();
                                ushort imgHeight = br.ReadUInt16();
                                byte imgFlags = br.ReadByte();

                                bool hasLocalColorTable = (imgFlags & 0x80) != 0;
                                bool interlaced = (imgFlags & 0x40) != 0;
                                int localColorTableSize = 2 << (imgFlags & 0x07);

                                Color32[] activeColorTable = globalColorTable;
                                if (hasLocalColorTable)
                                    activeColorTable = ReadColorTable(br, localColorTableSize);

                                if (activeColorTable == null)
                                    throw new InvalidDataException("GIFs have neither a global nor a local color table.");

                                byte lzwMinCodeSize = br.ReadByte();
                                byte[] compressed = ReadSubBlocksData(br);

                                int[] indices = LzwDecode(compressed, lzwMinCodeSize, imgWidth * imgHeight);

                                if (interlaced)
                                    indices = Deinterlace(indices, imgWidth, imgHeight);

                                if (disposalMethod == 3 && previousCanvas != null)
                                {
                                    Array.Copy(previousCanvas, canvas, canvas.Length);
                                }

                                Color32[] snapshotBeforeThisFrame = (Color32[])canvas.Clone();

                                CompositeFrame(canvas, screenWidth, screenHeight,
                                    indices, activeColorTable, transparentIndex,
                                    imgLeft, imgTop, imgWidth, imgHeight);

                                Texture2D tex = new Texture2D(screenWidth, screenHeight, TextureFormat.RGBA32, false);
                                tex.filterMode = FilterMode.Point;
                                tex.wrapMode = TextureWrapMode.Clamp;
                                tex.SetPixels32(FlipVertical(canvas, screenWidth, screenHeight));
                                tex.Apply(false, true);

                                frames.Add(new GifFrame { Texture = tex, DelaySeconds = frameDelay });

                                switch (disposalMethod)
                                {
                                    case 2:
                                        ClearRegion(canvas, screenWidth, screenHeight, imgLeft, imgTop, imgWidth, imgHeight);
                                        previousCanvas = null;
                                        break;
                                    case 3:
                                        previousCanvas = snapshotBeforeThisFrame;
                                        break;
                                    default:
                                        previousCanvas = null;
                                        break;
                                }

                                transparentIndex = -1;
                                disposalMethod = 0;
                                break;
                            }

                        case 0x3B:
                            reading = false;
                            break;

                        default:
                            Debug.LogWarning($"[GifDecoder] Unexpected byte in a block 0x{blockType:X2} — Parsing stopped; frames decoded: {frames.Count}.");
                            reading = false;
                            break;
                    }

                    if (ms.Position >= ms.Length)
                        reading = false;
                }
            }

            if (frames.Count == 0)
                throw new InvalidDataException("No frames were found in the GIF.");

            return frames;
        }

        private static Color32[] ReadColorTable(BinaryReader br, int size)
        {
            var table = new Color32[size];
            for (int i = 0; i < size; i++)
            {
                byte r = br.ReadByte();
                byte g = br.ReadByte();
                byte b = br.ReadByte();
                table[i] = new Color32(r, g, b, 255);
            }
            return table;
        }

        private static void SkipSubBlocks(BinaryReader br)
        {
            byte len;
            while ((len = br.ReadByte()) != 0)
                br.ReadBytes(len);
        }

        private static byte[] ReadSubBlocksData(BinaryReader br)
        {
            using (var buffer = new MemoryStream())
            {
                byte len;
                while ((len = br.ReadByte()) != 0)
                {
                    byte[] chunk = br.ReadBytes(len);
                    buffer.Write(chunk, 0, chunk.Length);
                }
                return buffer.ToArray();
            }
        }

        private static int[] LzwDecode(byte[] data, int minCodeSize, int expectedPixelCount)
        {
            int clearCode = 1 << minCodeSize;
            int endCode = clearCode + 1;
            int codeSize = minCodeSize + 1;
            int nextCode = endCode + 1;

            var dictionary = new List<byte[]>(MaxLzwCodes);

            void ResetDictionary()
            {
                dictionary.Clear();
                for (int i = 0; i < clearCode; i++)
                    dictionary.Add(new byte[] { (byte)i });
                dictionary.Add(null);
                dictionary.Add(null);
                codeSize = minCodeSize + 1;
                nextCode = endCode + 1;
            }

            ResetDictionary();

            var output = new List<byte>(expectedPixelCount);

            int bitPos = 0;
            int totalBits = data.Length * 8;
            byte[] prevEntry = null;

            int ReadCode()
            {
                int code = 0;
                for (int i = 0; i < codeSize; i++)
                {
                    if (bitPos >= totalBits)
                        return endCode;

                    int byteIndex = bitPos >> 3;
                    int bitIndex = bitPos & 7;
                    int bit = (data[byteIndex] >> bitIndex) & 1;
                    code |= bit << i;
                    bitPos++;
                }
                return code;
            }

            while (output.Count < expectedPixelCount)
            {
                int code = ReadCode();

                if (code == clearCode)
                {
                    ResetDictionary();
                    prevEntry = null;
                    continue;
                }

                if (code == endCode)
                    break;

                byte[] entry;

                if (code < dictionary.Count && dictionary[code] != null)
                {
                    entry = dictionary[code];
                }
                else if (code == nextCode && prevEntry != null)
                {
                    entry = new byte[prevEntry.Length + 1];
                    Array.Copy(prevEntry, entry, prevEntry.Length);
                    entry[prevEntry.Length] = prevEntry[0];
                }
                else
                {
                    break;
                }

                output.AddRange(entry);

                if (prevEntry != null && nextCode < MaxLzwCodes)
                {
                    var newEntry = new byte[prevEntry.Length + 1];
                    Array.Copy(prevEntry, newEntry, prevEntry.Length);
                    newEntry[prevEntry.Length] = entry[0];

                    if (nextCode == dictionary.Count)
                        dictionary.Add(newEntry);
                    else
                        dictionary[nextCode] = newEntry;

                    nextCode++;

                    if (nextCode == (1 << codeSize) && codeSize < MaxLzwBits)
                        codeSize++;
                }

                prevEntry = entry;
            }

            var result = new int[expectedPixelCount];
            int n = Math.Min(output.Count, expectedPixelCount);
            for (int i = 0; i < n; i++)
                result[i] = output[i];

            return result;
        }

        private static int[] Deinterlace(int[] indices, int width, int height)
        {
            var result = new int[indices.Length];
            int srcRow = 0;

            void CopyPass(int start, int step)
            {
                for (int row = start; row < height; row += step)
                {
                    Array.Copy(indices, srcRow * width, result, row * width, width);
                    srcRow++;
                }
            }

            CopyPass(0, 8);
            CopyPass(4, 8);
            CopyPass(2, 4);
            CopyPass(1, 2);

            return result;
        }

        private static void CompositeFrame(
            Color32[] canvas, int canvasWidth, int canvasHeight,
            int[] indices, Color32[] colorTable, int transparentIndex,
            int left, int top, int frameWidth, int frameHeight)
        {
            for (int y = 0; y < frameHeight; y++)
            {
                int canvasY = top + y;
                if (canvasY < 0 || canvasY >= canvasHeight) continue;

                for (int x = 0; x < frameWidth; x++)
                {
                    int canvasX = left + x;
                    if (canvasX < 0 || canvasX >= canvasWidth) continue;

                    int idx = indices[y * frameWidth + x];

                    if (idx == transparentIndex)
                        continue;

                    if (idx < 0 || idx >= colorTable.Length)
                        continue;

                    canvas[canvasY * canvasWidth + canvasX] = colorTable[idx];
                }
            }
        }

        private static void ClearRegion(
            Color32[] canvas, int canvasWidth, int canvasHeight,
            int left, int top, int regionWidth, int regionHeight)
        {
            Color32 clear = new Color32(0, 0, 0, 0);
            for (int y = 0; y < regionHeight; y++)
            {
                int canvasY = top + y;
                if (canvasY < 0 || canvasY >= canvasHeight) continue;

                for (int x = 0; x < regionWidth; x++)
                {
                    int canvasX = left + x;
                    if (canvasX < 0 || canvasX >= canvasWidth) continue;

                    canvas[canvasY * canvasWidth + canvasX] = clear;
                }
            }
        }

        private static Color32[] FlipVertical(Color32[] pixels, int width, int height)
        {
            var flipped = new Color32[pixels.Length];
            for (int y = 0; y < height; y++)
            {
                int srcRow = y * width;
                int dstRow = (height - 1 - y) * width;
                Array.Copy(pixels, srcRow, flipped, dstRow, width);
            }
            return flipped;
        }
    }
}