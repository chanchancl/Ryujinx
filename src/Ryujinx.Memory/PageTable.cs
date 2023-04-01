namespace Ryujinx.Memory
{
    public class PageTable<T> where T : unmanaged
    {
        public const int PageBits = 12;
        public const int PageSize = 1 << PageBits; // 4KB
        public const int PageMask = PageSize - 1;  // 0xFFF

        private const int PtLevelBits = 9; // 9 * 4 + 12 = 48 (max address space size)
        private const int PtLevelSize = 1 << PtLevelBits; // 512
        private const int PtLevelMask = PtLevelSize - 1;  // 0x1ff

        private readonly T[][][][] _pageTable;

        public PageTable()
        {
            _pageTable = new T[PtLevelSize][][][];
        }

        // 从 虚拟地址 Virtual Address 到 Physical Address 的映射
        public T Read(ulong va)
        {
            // l0-l3, 每层9位，512个位置
            // l0            l1            l2            l3
            // PtLevelBits | PtLevelBits | PtLevelBits | PtLevelBits | PageBits
            // 实际只使用va的了 12-60 位，低12位没使用
            int l3 = (int)(va >> PageBits) & PtLevelMask;
            int l2 = (int)(va >> (PageBits + PtLevelBits)) & PtLevelMask;
            int l1 = (int)(va >> (PageBits + PtLevelBits * 2)) & PtLevelMask;
            int l0 = (int)(va >> (PageBits + PtLevelBits * 3)) & PtLevelMask;

            if (_pageTable[l0] == null)
            {
                return default;
            }

            if (_pageTable[l0][l1] == null)
            {
                return default;
            }

            if (_pageTable[l0][l1][l2] == null)
            {
                return default;
            }

            return _pageTable[l0][l1][l2][l3];
        }

        public void Map(ulong va, T value)
        {
            // 范围大约是 64 个 G
            //     l0         l1        l2        l3       ?
            // | 39 - 47 | 30 - 38 | 21 - 29 | 12 - 20 | 0 - 11 |
            int l3 = (int)(va >> PageBits) & PtLevelMask;
            int l2 = (int)(va >> (PageBits + PtLevelBits)) & PtLevelMask;
            int l1 = (int)(va >> (PageBits + PtLevelBits * 2)) & PtLevelMask;
            int l0 = (int)(va >> (PageBits + PtLevelBits * 3)) & PtLevelMask;

            if (_pageTable[l0] == null)
            {
                // 1 << 9,  512, [39-47] 的9位，表示的范围也是0-511
                _pageTable[l0] = new T[PtLevelSize][][];
            }

            if (_pageTable[l0][l1] == null)
            {
                _pageTable[l0][l1] = new T[PtLevelSize][];
            }

            if (_pageTable[l0][l1][l2] == null)
            {
                _pageTable[l0][l1][l2] = new T[PtLevelSize];
            }

            _pageTable[l0][l1][l2][l3] = value;
        }

        public void Unmap(ulong va)
        {
            int l3 = (int)(va >> PageBits) & PtLevelMask;
            int l2 = (int)(va >> (PageBits + PtLevelBits)) & PtLevelMask;
            int l1 = (int)(va >> (PageBits + PtLevelBits * 2)) & PtLevelMask;
            int l0 = (int)(va >> (PageBits + PtLevelBits * 3)) & PtLevelMask;

            if (_pageTable[l0] == null)
            {
                return;
            }

            if (_pageTable[l0][l1] == null)
            {
                return;
            }

            if (_pageTable[l0][l1][l2] == null)
            {
                return;
            }

            _pageTable[l0][l1][l2][l3] = default;

            bool empty = true;

            for (int i = 0; i < _pageTable[l0][l1][l2].Length; i++)
            {
                empty &= _pageTable[l0][l1][l2][i].Equals(default);
            }

            if (empty)
            {
                _pageTable[l0][l1][l2] = null;

                RemoveIfAllNull(l0, l1);
            }
        }

        private void RemoveIfAllNull(int l0, int l1)
        {
            bool empty = true;

            for (int i = 0; i < _pageTable[l0][l1].Length; i++)
            {
                empty &= (_pageTable[l0][l1][i] == null);
            }

            if (empty)
            {
                _pageTable[l0][l1] = null;

                RemoveIfAllNull(l0);
            }
        }

        private void RemoveIfAllNull(int l0)
        {
            bool empty = true;

            for (int i = 0; i < _pageTable[l0].Length; i++)
            {
                empty &= (_pageTable[l0][i] == null);
            }

            if (empty)
            {
                _pageTable[l0] = null;
            }
        }
    }
}
