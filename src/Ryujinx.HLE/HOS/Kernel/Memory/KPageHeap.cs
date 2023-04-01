using Ryujinx.Common;
using System;

namespace Ryujinx.HLE.HOS.Kernel.Memory
{
    class KPageHeap
    {
        private class Block
        {
            private readonly KPageBitmap _bitmap = new();
            private ulong _heapAddress;
            private ulong _endOffset;

            public int Shift { get; private set; }
            public int NextShift { get; private set; }
            public ulong Size => 1UL << Shift;
            public int PagesCount => (int)(Size / KPageTableBase.PageSize);
            public int FreeBlocksCount => _bitmap.BitsCount;
            public int FreePagesCount => FreeBlocksCount * PagesCount;

            public ArraySegment<ulong> Initialize(ulong address, ulong size, int blockShift, int nextBlockShift, ArraySegment<ulong> bitStorage)
            {
                // 使用64分支的树来表示是否占用
                Shift = blockShift;
                NextShift = nextBlockShift;

                ulong endAddress = address + size;

                ulong align = nextBlockShift != 0
                    ? 1UL << nextBlockShift
                    : 1UL << blockShift;

                address = BitUtils.AlignDown(address, align);
                endAddress = BitUtils.AlignUp(endAddress, align);

                _heapAddress = address;
                _endOffset = (endAddress - address) / (1UL << blockShift);

                return _bitmap.Initialize(bitStorage, _endOffset);
            }

            public ulong PushBlock(ulong address)
            {
                ulong offset = (address - _heapAddress) >> Shift;

                _bitmap.SetBit(offset);

                if (NextShift != 0)
                {
                    int diff = 1 << (NextShift - Shift);

                    offset = BitUtils.AlignDown(offset, (ulong)diff);

                    if (_bitmap.ClearRange(offset, diff))
                    {
                        return _heapAddress + (offset << Shift);
                    }
                }

                return 0;
            }

            public ulong PopBlock(bool random)
            {
                long sOffset = (long)_bitmap.FindFreeBlock(random);

                if (sOffset < 0L)
                {
                    return 0;
                }

                ulong offset = (ulong)sOffset;

                _bitmap.ClearBit(offset);

                return _heapAddress + (offset << Shift);
            }

            public static int CalculateManagementOverheadSize(ulong regionSize, int currBlockShift, int nextBlockShift)
            {
                ulong currBlockSize = 1UL << currBlockShift;
                ulong nextBlockSize = 1UL << nextBlockShift;
                ulong align = nextBlockShift != 0 ? nextBlockSize : currBlockSize;
                // 当  0 <= i < 6 时,  align = nextBlockSize i = 6时， align = currBlockSize
                // 举例，当 i = 0时， currBlockSize = 0x1000, nextBlockSize = 0x1_0000, 分别是4K 和 64K
                // align 为 64K， 这里的 2个额外的 align，应该是header的大小
                // 则传入的参数为 ( 64K * 2 + align(3285MB, 64K)) / 4K) = 840992, 0xcd520, 说明需要 3285 MB 加2个page，以4K分块，可以分 84W个
                return KPageBitmap.CalculateManagementOverheadSize((align * 2 + BitUtils.AlignUp(regionSize, align)) / currBlockSize);
            }
        }

        // 4, 5, 1, 3, 4, 1
        // size :   12, 0x001000    ,    4KB
        //          16, 0x010000    ,   64KB
        //          21, 0x200000    ,    2MB
        //          22, 0x400000    ,    4MB
        //          25, 0x2000000   ,   32MB
        //          29, 0x20000000  ,  512MB
        //          30, 0x40000000  , 1024MB 
        private static readonly int[] _memoryBlockPageShifts = { 12, 16, 21, 22, 25, 29, 30 };

#pragma warning disable IDE0052 // Remove unread private member
        private readonly ulong _heapAddress;
        private readonly ulong _heapSize;
        private ulong _usedSize;
#pragma warning restore IDE0052
        private readonly int _blocksCount;
        private readonly Block[] _blocks;

        public KPageHeap(ulong address, ulong size) : this(address, size, _memoryBlockPageShifts)
        {
        }

        public KPageHeap(ulong address, ulong size, int[] blockShifts)
        {
            _heapAddress = address;
            _heapSize = size;
            _blocksCount = blockShifts.Length;
            _blocks = new Block[_memoryBlockPageShifts.Length];
            // blockPageShifts 的作用大致就是，将 size

            // 总的来说就是计算 分别以 blockShifts 中的每个作为page大小，来管理size大小的空间，需要多少额外的存储空间
            // 比如说 size = 0x6000, 那么
            //  shift = 12, 1<< 12 == 0x1000, 则可分为6个页面

            // 总共需要 0x1c000 个 ulong 来以不同的层级管理3个多G的空间
            var currBitmapStorage = new ArraySegment<ulong>(new ulong[CalculateManagementOverheadSize(size, blockShifts)]);

            for (int i = 0; i < blockShifts.Length; i++)
            {
                int currBlockShift = blockShifts[i];
                int nextBlockShift = i != blockShifts.Length - 1 ? blockShifts[i + 1] : 0;

                _blocks[i] = new Block();

                currBitmapStorage = _blocks[i].Initialize(address, size, currBlockShift, nextBlockShift, currBitmapStorage);
            }
        }

        public void UpdateUsedSize()
        {
            _usedSize = _heapSize - (GetFreePagesCount() * KPageTableBase.PageSize);
        }

        public ulong GetFreePagesCount()
        {
            ulong freeCount = 0;

            for (int i = 0; i < _blocksCount; i++)
            {
                freeCount += (ulong)_blocks[i].FreePagesCount;
            }

            return freeCount;
        }

        public ulong AllocateBlock(int index, bool random)
        {
            ulong neededSize = _blocks[index].Size;
            // 从小到大找空间
            // 4K, 64K, 2MB

            // 假如有这样一个调用,   

            for (int i = index; i < _blocksCount; i++)
            {
                ulong address = _blocks[i].PopBlock(random);

                if (address != 0)
                {
                    ulong allocatedSize = _blocks[i].Size;

                    if (allocatedSize > neededSize)
                    {
                        Free(address + neededSize, (allocatedSize - neededSize) / KPageTableBase.PageSize);
                    }

                    return address;
                }
            }

            return 0;
        }

        private void FreeBlock(ulong block, int index)
        {
            do
            {
                block = _blocks[index++].PushBlock(block);
            }
            while (block != 0);
        }

        public void Free(ulong address, ulong pagesCount)
        {
            if (pagesCount == 0)
            {
                return;
            }

            int bigIndex = _blocksCount - 1;

            ulong start = address;
            ulong end = address + pagesCount * KPageTableBase.PageSize;
            ulong beforeStart = start;
            ulong beforeEnd = start;
            ulong afterStart = end;
            ulong afterEnd = end;

            // 比如 总共有10MB 内存，起始地址为 0x200000(2MB), 总共有 10M/4K=2560 个页面
            //               剩余页面
            // block[0], 4K,    0,
            // block[1],64K,    0,
            // block[2],2MB,  512, 填充 2MB <-> 4MB之间的大小 ,2MB,分配2个区块，总共2MB大小
            // block[3],4MB, 2048, 起始地址会被对齐到 BlockSize,4MB, 然后分配2个区块， 总共 8MB空间
            // block[4],32M,    0, 因为无法放下1个32M的块，所以GetBlockPagesCount为0

            // Block中，被标记为1，表示空闲，如果是0，那么就是已占用
            // 将 [address, address+size) 分为3个部分
            //
            //    |       start        |          |  ...  |      end     |
            //                      bigStart            bigEnd
            //         beforeStart  beforeEnd
            //  将 bigStart 和 bigEnd之间，用大块来填满
            while (bigIndex >= 0)
            {
                ulong blockSize = _blocks[bigIndex].Size;

                ulong bigStart = BitUtils.AlignUp(start, blockSize);
                ulong bigEnd = BitUtils.AlignDown(end, blockSize);

                if (bigStart < bigEnd)
                {
                    for (ulong block = bigStart; block < bigEnd; block += blockSize)
                    {
                        FreeBlock(block, bigIndex);
                    }

                    beforeEnd = bigStart;
                    afterStart = bigEnd;

                    break;
                }
                //  如果start 和 end 属于同一个block内，则 bigStart > bigEnd，那就用更小的区块来尝试
                //  直到能找到一个至少可以分配1个block的大小

                bigIndex--;
            }

            // 用小区块尝试把 beforeStart 和 beforeEnd，也就是 start 和 bigStart之间的内存标为空闲
            for (int i = bigIndex - 1; i >= 0; i--)
            {
                ulong blockSize = _blocks[i].Size; // 512 MB, 32MB, 4MB, 2MB, 64KB, 4KB

                while (beforeStart + blockSize <= beforeEnd)
                {
                    beforeEnd -= blockSize;
                    FreeBlock(beforeEnd, i);
                }
            }

            // 用小块尝试把 bigEnd 和 end 之间的内存标为空闲
            for (int i = bigIndex - 1; i >= 0; i--)
            {
                ulong blockSize = _blocks[i].Size;

                while (afterStart + blockSize <= afterEnd)
                {
                    FreeBlock(afterStart, i);
                    afterStart += blockSize;
                }
            }
        }

        public static int GetAlignedBlockIndex(ulong pagesCount, ulong alignPages)
        {
            ulong targetPages = Math.Max(pagesCount, alignPages);

            for (int i = 0; i < _memoryBlockPageShifts.Length; i++)
            {
                if (targetPages <= GetBlockPagesCount(i))
                {
                    return i;
                }
            }

            return -1;
        }

        public static int GetBlockIndex(ulong pagesCount)
        {
            // 想要分配 pagesCount 个页面，需要用哪个block Index？
            // 从大到小开始找, GetblockPagesCount 是一个block[index]的block内，有多少个pages
            // block[1] 为 64KB， 有 16 个 Pages
            // block[2] 为  2MB， 有512 个 Pages
            // 如果要分配 20个 pages， 则需要使用 index == 1， 因为此时 20 >= 16
            
            for (int i = _memoryBlockPageShifts.Length - 1; i >= 0; i--)
            {
                if (pagesCount >= GetBlockPagesCount(i))
                {
                    return i;
                }
            }

            return -1;
        }

        public static ulong GetBlockSize(int index)
        {
            return 1UL << _memoryBlockPageShifts[index];
        }

        public static ulong GetBlockPagesCount(int index)
        {
            return GetBlockSize(index) / KPageTableBase.PageSize;
        }

        private static int CalculateManagementOverheadSize(ulong regionSize, int[] blockShifts)
        {
            int overheadSize = 0;

            for (int i = 0; i < blockShifts.Length; i++)
            {
                int currBlockShift = blockShifts[i];
                int nextBlockShift = i != blockShifts.Length - 1 ? blockShifts[i + 1] : 0;
                overheadSize += Block.CalculateManagementOverheadSize(regionSize, currBlockShift, nextBlockShift);
            }
            // 需要的size分别为
            // 0x1a140, 0x1a28, 0xd8, 0x78, 0x20, 0x8, 0x8, sum = 0x1bce8, 对齐到4K后为 0x1c000

            return BitUtils.AlignUp(overheadSize, KPageTableBase.PageSize);
        }
    }
}
