using Ryujinx.Common;
using System;
using System.Numerics;

namespace Ryujinx.HLE.HOS.Kernel.Memory
{
    class KPageBitmap
    {
        private struct RandomNumberGenerator
        {
            private uint _entropy;
            private uint _bitsAvailable;

            private void RefreshEntropy()
            {
                _entropy = 0;
                _bitsAvailable = sizeof(uint) * 8;
            }

            private bool GenerateRandomBit()
            {
                if (_bitsAvailable == 0)
                {
                    RefreshEntropy();
                }

                bool bit = (_entropy & 1) != 0;

                _entropy >>= 1;
                _bitsAvailable--;

                return bit;
            }

            public int SelectRandomBit(ulong bitmap)
            {
                int selected = 0;

                int bitsCount = UInt64BitSize / 2;
                ulong mask = (1UL << bitsCount) - 1;

                while (bitsCount != 0)
                {
                    ulong low = bitmap & mask;
                    ulong high = (bitmap >> bitsCount) & mask;

                    bool chooseLow;

                    if (high == 0)
                    {
                        chooseLow = true;
                    }
                    else if (low == 0)
                    {
                        chooseLow = false;
                    }
                    else
                    {
                        chooseLow = GenerateRandomBit();
                    }

                    if (chooseLow)
                    {
                        bitmap = low;
                    }
                    else
                    {
                        bitmap = high;
                        selected += bitsCount;
                    }

                    bitsCount /= 2;
                    mask >>= bitsCount;
                }

                return selected;
            }
        }

        private const int UInt64BitSize = sizeof(ulong) * 8;
        private const int MaxDepth = 4;

        private readonly RandomNumberGenerator _rng;
        private readonly ArraySegment<ulong>[] _bitStorages;
        private int _usedDepths;

        public int BitsCount { get; private set; }

        public int HighestDepthIndex => _usedDepths - 1;

        public KPageBitmap()
        {
            _rng = new RandomNumberGenerator();
            _bitStorages = new ArraySegment<ulong>[MaxDepth];
        }

        public ArraySegment<ulong> Initialize(ArraySegment<ulong> storage, ulong size)
        {
            _usedDepths = GetRequiredDepth(size);

            // block[0]...
            // _bitStorages[0] = offset 0x3426, size 0x0001
            //             [1] = offset 0x3422, size 0x0004
            //             [2] = offset 0x3354, size 0x00ce
            //             [3] = offset 0     , size 0x3354, then 0x3354 * 8 * 4K = 0x19aa0000 >= 3252MB,
            // 从上至下，每一个bit，都代表这一块内存是否被占用，只有当子节点全是0是，当前节点才能是0
            for (int depth = HighestDepthIndex; depth >= 0; depth--)
            {
                _bitStorages[depth] = storage;
                size = BitUtils.DivRoundUp<ulong>(size, (ulong)UInt64BitSize);
                storage = storage.Slice((int)size);
            }

            return storage;
        }

        public ulong FindFreeBlock(bool random)
        {
            ulong offset = 0;
            int depth = 0;

            if (random)
            {
                do
                {
                    ulong v = _bitStorages[depth][(int)offset];

                    if (v == 0)
                    {
                        return ulong.MaxValue;
                    }

                    offset = offset * UInt64BitSize + (ulong)_rng.SelectRandomBit(v);
                }
                while (++depth < _usedDepths);
            }
            else
            {
                do
                {
                    // depth 从 0 -> 3, size从小到大
                    ulong v = _bitStorages[depth][(int)offset];
                    // 111...111   初始时，全部都是1，表明未分配

                    // if v == 0 说明没有block全都被分配出去了，没有free的
                    if (v == 0)
                    {
                        return ulong.MaxValue;
                    }

                    //      offset * UInt64BitsSize 表示树的下一个节点， TrailingZeroCount 表示第几个Block是空的
                    offset = offset * UInt64BitSize + (ulong)BitOperations.TrailingZeroCount(v);
                }
                while (++depth < _usedDepths);
            }

            return offset;
        }

        public void SetBit(ulong offset)
        {
            SetBit(HighestDepthIndex, offset);
            BitsCount++;
        }

        public void ClearBit(ulong offset)
        {
            ClearBit(HighestDepthIndex, offset);
            BitsCount--;
        }

        public bool ClearRange(ulong offset, int count)
        {
            int depth = HighestDepthIndex;
            var bits = _bitStorages[depth];

            int bitInd = (int)(offset / UInt64BitSize);

            if (count < UInt64BitSize)
            {
                int shift = (int)(offset % UInt64BitSize);

                ulong mask = ((1UL << count) - 1) << shift;

                ulong v = bits[bitInd];

                if ((v & mask) != mask)
                {
                    return false;
                }

                v &= ~mask;
                bits[bitInd] = v;

                if (v == 0)
                {
                    ClearBit(depth - 1, (ulong)bitInd);
                }
            }
            else
            {
                int remaining = count;
                int i = 0;

                do
                {
                    if (bits[bitInd + i++] != ulong.MaxValue)
                    {
                        return false;
                    }

                    remaining -= UInt64BitSize;
                }
                while (remaining > 0);

                remaining = count;
                i = 0;

                do
                {
                    bits[bitInd + i] = 0;
                    ClearBit(depth - 1, (ulong)(bitInd + i));
                    i++;
                    remaining -= UInt64BitSize;
                }
                while (remaining > 0);
            }

            BitsCount -= count;
            return true;
        }

        private void SetBit(int depth, ulong offset)
        {
            while (depth >= 0)
            {
                int ind = (int)(offset / UInt64BitSize);
                int which = (int)(offset % UInt64BitSize);

                ulong mask = 1UL << which;

                ulong v = _bitStorages[depth][ind];

                _bitStorages[depth][ind] = v | mask;

                if (v != 0)  // 说明这个块之前就已经分配过别的节点了，不需要通知自己的父节点，可以直接break，否则要把父节点也标记了
                {
                    break;
                }

                offset = (ulong)ind;
                depth--;
            }
        }

        private void ClearBit(int depth, ulong offset)
        {
            while (depth >= 0)
            {
                int ind = (int)(offset / UInt64BitSize);
                int which = (int)(offset % UInt64BitSize);

                ulong mask = 1UL << which;

                ulong v = _bitStorages[depth][ind];

                v &= ~mask;

                _bitStorages[depth][ind] = v;

                if (v != 0)
                {
                    break;
                }

                offset = (ulong)ind;
                depth--;
            }
        }

        private static int GetRequiredDepth(ulong regionSize)
        {
            int depth = 0;

            do
            {
                regionSize /= UInt64BitSize;
                depth++;
            }
            while (regionSize != 0);

            return depth;
        }

        public static int CalculateManagementOverheadSize(ulong regionSize)
        {
            int overheadBits = 0;

            // regionSize = 0xcd520
            // 64 ** 4 >= regionSize
            // 遍历 [3, 2, 1 0]
            // RegionSize依次为变为, 0x3355, 0xce, 0x4, 0x1
            // sum = 0x3428,  * sizeof(ulong) = 0x1a140
            // 比如说第一层 有64个子节点，为了表明他们的状态，需要 64个bit才行，那么就需要 8个 ulong才行，依次类推，总共需要 0x1a140个ulong
            for (int depth = GetRequiredDepth(regionSize) - 1; depth >= 0; depth--)
            {
                regionSize = BitUtils.DivRoundUp<ulong>(regionSize, UInt64BitSize);
                overheadBits += (int)regionSize;
            }

            return overheadBits * sizeof(ulong);
        }
    }
}
