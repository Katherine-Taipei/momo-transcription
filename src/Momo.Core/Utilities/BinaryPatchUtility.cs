using System;
using System.IO;
using System.IO.Compression;

namespace Momo.Core.Utilities
{
    public static class BinaryPatchUtility
    {
        private const string MagicHeader = "MOMODIFF";

        public static void ApplyPatch(string oldFile, string newFile, string patchFile)
        {
            using (var oldStream = File.OpenRead(oldFile))
            using (var newStream = File.Create(newFile))
            using (var patchStream = File.OpenRead(patchFile))
            {
                ApplyPatch(oldStream, newStream, patchStream);
            }
        }

        public static void ApplyPatch(Stream oldStream, Stream newStream, Stream patchStream)
        {
            // Verify Magic
            byte[] magicBytes = new byte[8];
            int read = patchStream.Read(magicBytes, 0, 8);
            if (read < 8 || System.Text.Encoding.ASCII.GetString(magicBytes) != MagicHeader)
            {
                throw new InvalidDataException("Invalid patch header magic.");
            }

            // Read control, diff, and new file sizes
            byte[] headerBuffer = new byte[24];
            if (patchStream.Read(headerBuffer, 0, 24) < 24)
            {
                throw new InvalidDataException("Truncated patch header.");
            }

            long ctrlSize = BitConverter.ToInt64(headerBuffer, 0);
            long diffSize = BitConverter.ToInt64(headerBuffer, 8);
            long newSize = BitConverter.ToInt64(headerBuffer, 16);

            if (ctrlSize < 0 || diffSize < 0 || newSize < 0)
            {
                throw new InvalidDataException("Invalid dimensions in patch header.");
            }

            // Read blocks
            byte[] ctrlBlock = new byte[ctrlSize];
            if (patchStream.Read(ctrlBlock, 0, (int)ctrlSize) < ctrlSize)
            {
                throw new InvalidDataException("Truncated control block.");
            }

            byte[] diffBlock = new byte[diffSize];
            if (patchStream.Read(diffBlock, 0, (int)diffSize) < diffSize)
            {
                throw new InvalidDataException("Truncated diff block.");
            }

            // The remaining patch stream is the extra block
            long extraSize = patchStream.Length - patchStream.Position;
            byte[] extraBlock = new byte[extraSize];
            if (patchStream.Read(extraBlock, 0, (int)extraSize) < extraSize)
            {
                throw new InvalidDataException("Truncated extra block.");
            }

            // Decompress blocks
            using (var ctrlMem = new MemoryStream(ctrlBlock))
            using (var ctrlDecompress = new DeflateStream(ctrlMem, CompressionMode.Decompress))
            using (var ctrlReader = new BinaryReader(ctrlDecompress))
            using (var diffMem = new MemoryStream(diffBlock))
            using (var diffDecompress = new DeflateStream(diffMem, CompressionMode.Decompress))
            using (var extraMem = new MemoryStream(extraBlock))
            using (var extraDecompress = new DeflateStream(extraMem, CompressionMode.Decompress))
            {
                long oldPosition = 0;
                long newPosition = 0;

                while (newPosition < newSize)
                {
                    // Read control tuple: [diffCount, extraCount, offsetAdjustment]
                    long diffCount = ctrlReader.ReadInt64();
                    long extraCount = ctrlReader.ReadInt64();
                    long offsetAdjustment = ctrlReader.ReadInt64();

                    if (newPosition + diffCount > newSize)
                    {
                        throw new InvalidDataException("Patch mismatch: diff size exceeds new file limit.");
                    }

                    // Read diff block bytes and add to old file bytes
                    byte[] tempDiff = new byte[diffCount];
                    int diffRead = diffDecompress.Read(tempDiff, 0, (int)diffCount);
                    if (diffRead < diffCount)
                    {
                        throw new InvalidDataException("Truncated diff decompression stream.");
                    }

                    for (int i = 0; i < diffCount; i++)
                    {
                        if (oldPosition >= oldStream.Length)
                        {
                            throw new InvalidDataException("Control block points past EOF of base file.");
                        }

                        oldStream.Position = oldPosition;
                        int oldByte = oldStream.ReadByte();
                        if (oldByte == -1)
                        {
                            throw new InvalidDataException("Unexpected EOF of base file stream.");
                        }

                        byte newByte = (byte)((tempDiff[i] + oldByte) & 0xFF);
                        newStream.WriteByte(newByte);
                        newPosition++;
                        oldPosition++;
                    }

                    // Read extra block bytes and copy directly to output
                    if (newPosition + extraCount > newSize)
                    {
                        throw new InvalidDataException("Patch mismatch: extra size exceeds new file limit.");
                    }

                    byte[] tempExtra = new byte[extraCount];
                    int extraRead = extraDecompress.Read(tempExtra, 0, (int)extraCount);
                    if (extraRead < extraCount)
                    {
                        throw new InvalidDataException("Truncated extra decompression stream.");
                    }

                    newStream.Write(tempExtra, 0, (int)extraCount);
                    newPosition += extraCount;

                    // Adjust old position pointer
                    oldPosition += offsetAdjustment;
                }
            }
        }

        public static void CreatePatch(string oldFile, string newFile, string patchFile)
        {
            using (var oldStream = File.OpenRead(oldFile))
            using (var newStream = File.OpenRead(newFile))
            using (var patchStream = File.Create(patchFile))
            {
                CreatePatch(oldStream, newStream, patchStream);
            }
        }

        public static void CreatePatch(Stream oldStream, Stream newStream, Stream patchStream)
        {
            // Simple MOMODIFF generator for differential patch unit tests.
            // Generates a difference patch between old and new files.
            // Writes magic header
            byte[] magic = System.Text.Encoding.ASCII.GetBytes(MagicHeader);
            patchStream.Write(magic, 0, 8);

            long newSize = newStream.Length;
            
            using (var ctrlMem = new MemoryStream())
            using (var diffMem = new MemoryStream())
            using (var extraMem = new MemoryStream())
            {
                using (var ctrlCompress = new DeflateStream(ctrlMem, CompressionMode.Compress, true))
                using (var ctrlWriter = new BinaryWriter(ctrlCompress))
                {
                    ctrlWriter.Write((long)0); // diffCount
                    ctrlWriter.Write(newSize); // extraCount
                    ctrlWriter.Write((long)0); // offsetAdjustment
                }

                // Compressing empty diff block
                using (var diffCompress = new DeflateStream(diffMem, CompressionMode.Compress, true))
                {
                }

                using (var extraCompress = new DeflateStream(extraMem, CompressionMode.Compress, true))
                {
                    newStream.Position = 0;
                    newStream.CopyTo(extraCompress);
                }

                byte[] ctrlBytes = ctrlMem.ToArray();
                byte[] diffBytes = diffMem.ToArray();
                byte[] extraBytes = extraMem.ToArray();

                patchStream.Write(BitConverter.GetBytes((long)ctrlBytes.Length), 0, 8);
                patchStream.Write(BitConverter.GetBytes((long)diffBytes.Length), 0, 8);
                patchStream.Write(BitConverter.GetBytes(newSize), 0, 8);

                patchStream.Write(ctrlBytes, 0, ctrlBytes.Length);
                patchStream.Write(diffBytes, 0, diffBytes.Length);
                patchStream.Write(extraBytes, 0, extraBytes.Length);
            }
        }
    }
}
