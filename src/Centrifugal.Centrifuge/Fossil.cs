/*
Copyright 2024 Centrifugal (C# port)
Copyright 2014-2024 Dmitry Chestnykh (JavaScript port)
Copyright 2007 D. Richard Hipp (original C version)

Fossil SCM delta compression algorithm, this is only the applyDelta part.
This is a C# port based on the JavaScript version from fossil-delta-js.
Licensed under Simplified BSD License.
*/

using System;
using System.Collections.Generic;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// Fossil delta compression algorithm implementation.
    /// Provides functionality to apply delta patches to reconstruct target data from source data.
    /// </summary>
    internal static class Fossil
    {
        // Lookup table for base64-like encoding used in Fossil delta format
        private static readonly int[] ZValue = new int[]
        {
            -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, -1, -1,
            -1, -1, -1, -1, -1, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23,
            24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, -1, -1, -1, -1, 36, -1, 37,
            38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56,
            57, 58, 59, 60, 61, 62, -1, -1, -1, 63, -1
        };

        /// <summary>
        /// Reader for reading bytes, characters, and base64-encoded integers from a byte array.
        /// </summary>
        private class Reader
        {
            internal readonly byte[] _array;
            internal int _pos;

            public Reader(byte[] array)
            {
                _array = array;
                _pos = 0;
            }

            public bool HaveBytes()
            {
                return _pos < _array.Length;
            }

            public byte GetByte()
            {
                if (_pos >= _array.Length)
                    throw new IndexOutOfRangeException("out of bounds");
                var b = _array[_pos];
                _pos++;
                return b;
            }

            public char GetChar()
            {
                return (char)GetByte();
            }

            /// <summary>
            /// Read base64-encoded unsigned integer from the delta stream.
            /// </summary>
            public uint GetInt()
            {
                uint v = 0;
                int c;
                while (HaveBytes())
                {
                    var b = GetByte();
                    c = ZValue[0x7f & b];
                    if (c < 0)
                        break;
                    v = (v << 6) + (uint)c;
                }
                _pos--;
                return v;
            }
        }

        /// <summary>
        /// Writer for building the output byte array.
        /// </summary>
        private class Writer
        {
            private readonly List<byte> _bytes = new List<byte>();

            public byte[] ToByteArray()
            {
                return _bytes.ToArray();
            }

            /// <summary>
            /// Copy a range of bytes from source array to output.
            /// </summary>
            public void PutArray(byte[] array, int start, int end)
            {
                for (int i = start; i < end; i++)
                {
                    _bytes.Add(array[i]);
                }
            }
        }

        /// <summary>
        /// Compute a 32-bit checksum of the byte array.
        /// Uses the same algorithm as the original Fossil implementation.
        /// </summary>
        private static uint Checksum(byte[] arr)
        {
            uint sum0 = 0, sum1 = 0, sum2 = 0, sum3 = 0;
            int z = 0;
            int n = arr.Length;

            // Unrolled loop for performance - process 16 bytes at a time
            while (n >= 16)
            {
                sum0 = (sum0 + arr[z + 0]);
                sum1 = (sum1 + arr[z + 1]);
                sum2 = (sum2 + arr[z + 2]);
                sum3 = (sum3 + arr[z + 3]);

                sum0 = (sum0 + arr[z + 4]);
                sum1 = (sum1 + arr[z + 5]);
                sum2 = (sum2 + arr[z + 6]);
                sum3 = (sum3 + arr[z + 7]);

                sum0 = (sum0 + arr[z + 8]);
                sum1 = (sum1 + arr[z + 9]);
                sum2 = (sum2 + arr[z + 10]);
                sum3 = (sum3 + arr[z + 11]);

                sum0 = (sum0 + arr[z + 12]);
                sum1 = (sum1 + arr[z + 13]);
                sum2 = (sum2 + arr[z + 14]);
                sum3 = (sum3 + arr[z + 15]);

                z += 16;
                n -= 16;
            }

            // Process remaining bytes in groups of 4
            while (n >= 4)
            {
                sum0 = (sum0 + arr[z + 0]);
                sum1 = (sum1 + arr[z + 1]);
                sum2 = (sum2 + arr[z + 2]);
                sum3 = (sum3 + arr[z + 3]);
                z += 4;
                n -= 4;
            }

            // Combine sums
            sum3 = (sum3 + (sum2 << 8) + (sum1 << 16) + (sum0 << 24));

            // Handle remaining 1-3 bytes
            switch (n)
            {
                case 3:
                    sum3 = (sum3 + (uint)(arr[z + 2] << 8));
                    goto case 2;
                case 2:
                    sum3 = (sum3 + (uint)(arr[z + 1] << 16));
                    goto case 1;
                case 1:
                    sum3 = (sum3 + (uint)(arr[z + 0] << 24));
                    break;
            }

            return sum3;
        }

        /// <summary>
        /// Apply a delta byte array to a source byte array, returning the target byte array.
        /// </summary>
        /// <param name="source">The original source data.</param>
        /// <param name="delta">The delta patch to apply.</param>
        /// <returns>The reconstructed target data.</returns>
        /// <exception cref="InvalidOperationException">Thrown when delta is malformed or checksum fails.</exception>
        public static byte[] ApplyDelta(byte[] source, byte[] delta)
        {
            uint total = 0;
            var zDelta = new Reader(delta);
            int lenSrc = source.Length;
            int lenDelta = delta.Length;

            // Read the expected output size
            var limit = zDelta.GetInt();
            if (zDelta.GetChar() != '\n')
                throw new InvalidOperationException("size integer not terminated by '\\n'");

            var zOut = new Writer();

            // Process delta commands
            while (zDelta.HaveBytes())
            {
                var cnt = zDelta.GetInt();

                var op = zDelta.GetChar();
                switch (op)
                {
                    case '@':
                        // Copy command: copy cnt bytes from source at offset ofst
                        {
                            var ofst = zDelta.GetInt();
                            if (zDelta.HaveBytes() && zDelta.GetChar() != ',')
                                throw new InvalidOperationException("copy command not terminated by ','");

                            total += cnt;
                            if (total > limit)
                                throw new InvalidOperationException("copy exceeds output file size");
                            if (ofst + cnt > lenSrc)
                                throw new InvalidOperationException("copy extends past end of input");

                            zOut.PutArray(source, (int)ofst, (int)(ofst + cnt));
                            break;
                        }

                    case ':':
                        // Insert command: insert cnt bytes from delta stream
                        {
                            total += cnt;
                            if (total > limit)
                                throw new InvalidOperationException("insert command gives an output larger than predicted");
                            if (cnt > lenDelta)
                                throw new InvalidOperationException("insert count exceeds size of delta");

                            zOut.PutArray(delta, (int)zDelta._pos, (int)(zDelta._pos + cnt));
                            zDelta._pos += (int)cnt;
                            break;
                        }

                    case ';':
                        // End command: verify checksum and return result
                        {
                            var output = zOut.ToByteArray();
                            if (cnt != Checksum(output))
                                throw new InvalidOperationException("bad checksum");
                            if (total != limit)
                                throw new InvalidOperationException("generated size does not match predicted size");
                            return output;
                        }

                    default:
                        throw new InvalidOperationException("unknown delta operator");
                }
            }

            throw new InvalidOperationException("unterminated delta");
        }
    }
}
