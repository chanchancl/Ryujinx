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
                // ʹ��64��֧��������ʾ�Ƿ�ռ��
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
                // ��  0 <= i < 6 ʱ,  align = nextBlockSize i = 6ʱ�� align = currBlockSize
                // �������� i = 0ʱ�� currBlockSize = 0x1000, nextBlockSize = 0x1_0000, �ֱ���4K �� 64K
                // align Ϊ 64K�� ����� 2������� align��Ӧ����header�Ĵ�С
                // ����Ĳ���Ϊ ( 64K * 2 + align(3285MB, 64K)) / 4K) = 840992, 0xcd520, ˵����Ҫ 3285 MB ��2��page����4K�ֿ飬���Է� 84W��
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
            // blockPageShifts �����ô��¾��ǣ��� size

            // �ܵ���˵���Ǽ��� �ֱ��� blockShifts �е�ÿ����Ϊpage��С��������size��С�Ŀռ䣬��Ҫ���ٶ���Ĵ洢�ռ�
            // ����˵ size = 0x6000, ��ô
            //  shift = 12, 1<< 12 == 0x1000, ��ɷ�Ϊ6��ҳ��

            // �ܹ���Ҫ 0x1c000 �� ulong ���Բ�ͬ�Ĳ㼶����3����G�Ŀռ�
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
            // ��С�����ҿռ�
            // 4K, 64K, 2MB

            // ����������һ������,   

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

            // ���� �ܹ���10MB �ڴ棬��ʼ��ַΪ 0x200000(2MB), �ܹ��� 10M/4K=2560 ��ҳ��
            //               ʣ��ҳ��
            // block[0], 4K,    0,
            // block[1],64K,    0,
            // block[2],2MB,  512, ��� 2MB <-> 4MB֮��Ĵ�С ,2MB,����2�����飬�ܹ�2MB��С
            // block[3],4MB, 2048, ��ʼ��ַ�ᱻ���뵽 BlockSize,4MB, Ȼ�����2�����飬 �ܹ� 8MB�ռ�
            // block[4],32M,    0, ��Ϊ�޷�����1��32M�Ŀ飬����GetBlockPagesCountΪ0

            // Block�У������Ϊ1����ʾ���У������0����ô������ռ��
            // �� [address, address+size) ��Ϊ3������
            //
            //    |       start        |          |  ...  |      end     |
            //                      bigStart            bigEnd
            //         beforeStart  beforeEnd
            //  �� bigStart �� bigEnd֮�䣬�ô��������
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
                //  ���start �� end ����ͬһ��block�ڣ��� bigStart > bigEnd���Ǿ��ø�С������������
                //  ֱ�����ҵ�һ�����ٿ��Է���1��block�Ĵ�С

                bigIndex--;
            }

            // ��С���鳢�԰� beforeStart �� beforeEnd��Ҳ���� start �� bigStart֮����ڴ��Ϊ����
            for (int i = bigIndex - 1; i >= 0; i--)
            {
                ulong blockSize = _blocks[i].Size; // 512 MB, 32MB, 4MB, 2MB, 64KB, 4KB

                while (beforeStart + blockSize <= beforeEnd)
                {
                    beforeEnd -= blockSize;
                    FreeBlock(beforeEnd, i);
                }
            }

            // ��С�鳢�԰� bigEnd �� end ֮����ڴ��Ϊ����
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
            // ��Ҫ���� pagesCount ��ҳ�棬��Ҫ���ĸ�block Index��
            // �Ӵ�С��ʼ��, GetblockPagesCount ��һ��block[index]��block�ڣ��ж��ٸ�pages
            // block[1] Ϊ 64KB�� �� 16 �� Pages
            // block[2] Ϊ  2MB�� ��512 �� Pages
            // ���Ҫ���� 20�� pages�� ����Ҫʹ�� index == 1�� ��Ϊ��ʱ 20 >= 16
            
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
            // ��Ҫ��size�ֱ�Ϊ
            // 0x1a140, 0x1a28, 0xd8, 0x78, 0x20, 0x8, 0x8, sum = 0x1bce8, ���뵽4K��Ϊ 0x1c000

            return BitUtils.AlignUp(overheadSize, KPageTableBase.PageSize);
        }
    }
}
