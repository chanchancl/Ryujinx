using Ryujinx.Cpu;
using Ryujinx.HLE.HOS.Kernel.Common;
using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Kernel.SupervisorCall;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.Memory;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Ryujinx.HLE.HOS.Kernel
{
    class KernelContext : IDisposable
    {
        public long PrivilegedProcessLowestId { get; set; } = 1;
        public long PrivilegedProcessHighestId { get; set; } = 8;

        public bool EnableVersionChecks { get; set; }

        public bool KernelInitialized { get; }

        public bool Running { get; private set; }

        public Switch Device { get; }
        public MemoryBlock Memory { get; }
        public ITickSource TickSource { get; }
        public Syscall Syscall { get; }
        public SyscallHandler SyscallHandler { get; }

        public KResourceLimit ResourceLimit { get; }

        public KMemoryManager MemoryManager { get; }

        public KMemoryBlockSlabManager LargeMemoryBlockSlabManager { get; }
        public KMemoryBlockSlabManager SmallMemoryBlockSlabManager { get; }

        public KSlabHeap UserSlabHeapPages { get; }

        public KCriticalSection CriticalSection { get; }
        public KScheduler[] Schedulers { get; }
        public KPriorityQueue PriorityQueue { get; }
        public KTimeManager TimeManager { get; }
        public KSynchronization Synchronization { get; }
        public KContextIdManager ContextIdManager { get; }

        public ConcurrentDictionary<ulong, KProcess> Processes { get; }
        public ConcurrentDictionary<string, KAutoObject> AutoObjectNames { get; }

        public bool ThreadReselectionRequested { get; set; }

        private ulong _kipId;
        private ulong _processId;
        private ulong _threadUid;

        public KernelContext(
            ITickSource tickSource,
            Switch device,
            MemoryBlock memory,
            MemorySize memorySize,
            MemoryArrange memoryArrange)
        {
            TickSource = tickSource;
            Device = device;
            Memory = memory;

            Running = true;

            // 用来处理 syscall，在CPU执行到 syscall时，读取寄存器的值，执行模拟操作系统对应的功能，然后再将结果写入寄存器
            Syscall = new Syscall(this);

            SyscallHandler = new SyscallHandler(this);

            // 缺省值Limit，每个 Process 可以自己制定
            // ResourceLimit的主要作用就是可以为每种资源设置上限
            // 当申请者所申请的额度，大于所剩资源时，会阻塞执行，有一个默认的timeout，大概10s
            ResourceLimit = new KResourceLimit(this);

            KernelInit.InitializeResourceLimit(ResourceLimit, memorySize);

            // 将内存分成若干块，并管理是否被占用，不实际分配内存
            MemoryManager = new KMemoryManager(memorySize, memoryArrange);

            // 计数器，大的容量是 20000, 小的是 10000
            LargeMemoryBlockSlabManager = new KMemoryBlockSlabManager(KernelConstants.MemoryBlockAllocatorSize * 2);
            SmallMemoryBlockSlabManager = new KMemoryBlockSlabManager(KernelConstants.MemoryBlockAllocatorSize);

            // 将 0x3de000 分为若干个 0x1000 块，插入链表，可以获取一个item，和归还一个item
            // UserSlabHeapBase     0x800e5000
            // UserSlabHeapItemSize 0x1000, 4K
            // UserSlabHeapSize     0x3de000
            UserSlabHeapPages = new KSlabHeap(
                KernelConstants.UserSlabHeapBase,
                KernelConstants.UserSlabHeapItemSize,
                KernelConstants.UserSlabHeapSize);

            // VirtualAlloc
            // 0xe5000
            // 0x3de000
            CommitMemory(KernelConstants.UserSlabHeapBase - DramMemoryMap.DramBase, KernelConstants.UserSlabHeapSize);

            // 临界区
            CriticalSection = new KCriticalSection(this);
            // 调度器
            Schedulers = new KScheduler[KScheduler.CpuCoresCount];
            // 优先队列
            PriorityQueue = new KPriorityQueue();
            // 基于时间的执行器
            TimeManager = new KTimeManager(this);
            // 可以同步一切支持内核同步的对象， KSynchronizationObject
            Synchronization = new KSynchronization(this);
            // 貌似是有点问题的计数器，利用8个int，总共32个字节，管理256个ID
            ContextIdManager = new KContextIdManager();

            for (int core = 0; core < KScheduler.CpuCoresCount; core++)
            {
                Schedulers[core] = new KScheduler(this, core);
            }

            StartPreemptionThread();

            KernelInitialized = true;

            Processes = new ConcurrentDictionary<ulong, KProcess>();
            AutoObjectNames = new ConcurrentDictionary<string, KAutoObject>();

            _kipId = KernelConstants.InitialKipId;
            _processId = KernelConstants.InitialProcessId;
        }

        private void StartPreemptionThread()
        {
            void PreemptionThreadStart()
            {
                KScheduler.PreemptionThreadLoop(this);
            }

            new Thread(PreemptionThreadStart) { Name = "HLE.PreemptionThread" }.Start();
        }

        public void CommitMemory(ulong address, ulong size)
        {
            ulong alignment = MemoryBlock.GetPageSize();
            ulong endAddress = address + size;

            address &= ~(alignment - 1);
            endAddress = (endAddress + (alignment - 1)) & ~(alignment - 1);

            Memory.Commit(address, endAddress - address);
        }

        public ulong NewThreadUid()
        {
            return Interlocked.Increment(ref _threadUid) - 1;
        }

        public ulong NewKipId()
        {
            return Interlocked.Increment(ref _kipId) - 1;
        }

        public ulong NewProcessId()
        {
            return Interlocked.Increment(ref _processId) - 1;
        }

        public void Dispose()
        {
            Running = false;

            for (int i = 0; i < KScheduler.CpuCoresCount; i++)
            {
                Schedulers[i].Dispose();
            }

            TimeManager.Dispose();
        }
    }
}
