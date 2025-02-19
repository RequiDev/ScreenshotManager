﻿using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using MelonLoader;
using HarmonyLib;
using VRC.Core;

using ScreenshotFileHandler = VRC.UserCamera.CameraUtil.ObjectNPrivateSealedStObUnique;

using ScreenshotManager.Config;

namespace ScreenshotManager.Core
{
    public static class FileDataHandler
    {

        // First 8 bytes of a PNG are always theses values otherwise the signature or file is corrupted
        private static readonly byte[] PngSignature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };

        public static bool UseLFS = false;

        // Method copied from https://github.com/knah/VRCMods/blob/master/LagFreeScreenshots/PngUtils.cs
        private static readonly uint[] ourCRCTable = Enumerable.Range(0, 256).Select(n =>
        {
            uint c = (uint)n;
            for (var k = 0; k <= 7; k++)
            {
                if ((c & 1) == 1)
                    c = 0xEDB88320 ^ ((c >> 1) & 0x7FFFFFFF);
                else
                    c = ((c >> 1) & 0x7FFFFFFF);
            }

            return c;
        }).ToArray();

        // Method copied from https://github.com/knah/VRCMods/blob/master/LagFreeScreenshots/PngUtils.cs
        private static uint PngCrc32(byte[] stream, int offset, int length, uint crc)
        {
            uint c = crc ^ 0xffffffff;
            var endOffset = offset + length;
            for (var i = offset; i < endOffset; i++)
            {
                c = ourCRCTable[(c ^ stream[i]) & 255] ^ ((c >> 8) & 0xFFFFFF);
            }

            return c ^ 0xffffffff;
        }

        public static void Init()
        {
            UseLFS = MelonHandler.Mods.Any(mod => mod.Info.Name.Equals("Lag Free Screenshots"));

            if (!UseLFS)
                ScreenshotManagerMod.Instance.HarmonyInstance.Patch(typeof(ScreenshotFileHandler).GetMethod("_TakeScreenShot_b__1"), postfix: new HarmonyMethod(typeof(FileDataHandler).GetMethod(nameof(DefaultVRCScreenshotResultPatch), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        private static void DefaultVRCScreenshotResultPatch(ScreenshotFileHandler __instance)
        {
            ImageHandler.AddFile(new FileInfo(__instance.field_Public_String_0));

            if (!Configuration.WriteImageMetadata.Value)
                return;

            string username = APIUser.CurrentUser.id + "," + APIUser.CurrentUser.displayName;

            ApiWorld apiWorld = RoomManager.field_Internal_Static_ApiWorld_0;
            string world = apiWorld == null ? "null,null,Not in world" : apiWorld.id + "," + RoomManager.field_Internal_Static_ApiWorldInstance_0.name + "," + apiWorld.name;

            string description = "screenshotmanager|0|author:" + username + "|" + world;

            if (__instance.field_Public_String_0.ToLower().EndsWith(ImageHandler.Extensions[0]))
            {
                bool result = WritePngChunk(__instance.field_Public_String_0, description);

                if (!result)
                    MelonLogger.Warning("Failed to write image metadata. Image will be saved without any data.");
            }
        }

        public static bool WritePngChunk(string file, string text)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(file);

                byte[] fileSignature = bytes.Take(8).ToArray();

                if (!Enumerable.SequenceEqual(fileSignature, PngSignature))
                {
                    MelonLogger.Warning("No PNG signature found in file: " + file);
                    return false;
                }

                byte[] originalBytes = null;
                byte[] endChunkBytes = null;

                // Skip file signature
                int index = 8;

                while (index < bytes.Length)
                {
                    // Read length of data and type of chunk
                    byte[] lengthDataField = bytes.Skip(index).Take(4).ToArray();
                    byte[] typeDataField = bytes.Skip(index + 4).Take(4).ToArray();
                    int length = (lengthDataField[0] << 24) | (lengthDataField[1] << 16) | (lengthDataField[2] << 8) | lengthDataField[3];
                    string type = Encoding.UTF8.GetString(typeDataField);

                    if (type.Equals("IEND"))
                    {
                        originalBytes = bytes.Take(index).ToArray();
                        endChunkBytes = bytes.Skip(index).ToArray();
                    }

                    index += length + 12;
                }

                string keyword = "Description";
                int chunkDataSize = keyword.Length + 5 + Encoding.UTF8.GetByteCount(text);
                byte[] chunkBytes = new byte[chunkDataSize + 12];

                // Write chunk length and type
                new byte[] { (byte)(chunkDataSize >> 24), (byte)(chunkDataSize >> 16), (byte)(chunkDataSize >> 8), (byte)chunkDataSize }.CopyTo(chunkBytes, 0);
                new byte[] { (byte)'i', (byte)'T', (byte)'X', (byte)'t' }.CopyTo(chunkBytes, 4);

                Encoding.UTF8.GetBytes(keyword).CopyTo(chunkBytes, 8);

                // Write empty flags
                new byte[] { 0, 0, 0, 0, 0 }.CopyTo(chunkBytes, 8 + keyword.Length);

                // Write actual description text
                Encoding.UTF8.GetBytes(text).CopyTo(chunkBytes, 8 + keyword.Length + 5);

                uint crc = PngCrc32(chunkBytes, 4, chunkBytes.Length - 8, 0);

                // Write crc to end of chunk
                new byte[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc }.CopyTo(chunkBytes, chunkBytes.Length - 4);

                // Create new file bytes and write it
                byte[] newFileContent = new byte[originalBytes.Length + chunkBytes.Length + endChunkBytes.Length];
                originalBytes.CopyTo(newFileContent, 0);
                chunkBytes.CopyTo(newFileContent, originalBytes.Length);
                endChunkBytes.CopyTo(newFileContent, originalBytes.Length + chunkBytes.Length);

                File.WriteAllBytes(file, newFileContent);

                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Error(e);
                MelonLogger.Error("Error while writing png chunks");
                return false;
            }
        }

        public static string ReadPngChunk(string file)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(file);

                byte[] fileSignature = bytes.Take(8).ToArray();

                if (!Enumerable.SequenceEqual(fileSignature, PngSignature))
                {
                    MelonLogger.Warning("No PNG signature found in file: " + file);
                    return null;
                }

                // Skip file signature
                int index = 8;

                while (index < bytes.Length)
                {
                    // Read length of data and type of chunk
                    byte[] lengthDataField = bytes.Skip(index).Take(4).ToArray();
                    byte[] typeDataField = bytes.Skip(index + 4).Take(4).ToArray();
                    int length = (lengthDataField[0] << 24) | (lengthDataField[1] << 16) | (lengthDataField[2] << 8) | lengthDataField[3];
                    string type = Encoding.UTF8.GetString(typeDataField);

                    if (type.Equals("iTXt"))
                    {
                        // Skip keyword and flag data and encode content
                        int bytesToSkip = Encoding.UTF8.GetByteCount("Description") + 5;
                        byte[] dataField = bytes.Skip(index + 8 + bytesToSkip).Take(length - bytesToSkip).ToArray();
                        return Encoding.UTF8.GetString(dataField);
                    }

                    if (type.Equals("IEND"))
                        return null;

                    index += length + 12;
                }
                return null;
            }
            catch (Exception e)
            {
                MelonLogger.Error(e);
                MelonLogger.Error("Error while reading png chunks");
                return null;
            }
        }

        public static string ReadJpegProperty(Image image)
        {
            try
            {
                // Read EXIF property
                PropertyItem property = image.GetPropertyItem(0x9286);
                return property == null ? null : Encoding.Unicode.GetString(property.Value);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
