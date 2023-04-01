using System.Threading;

namespace Ryujinx.HLE.HOS.Kernel.Threading
{
    class KCriticalSection
    {
        private readonly KernelContext _context;
        private readonly object _lock;
        private int _recursionCount;

        public object Lock => _lock;

        public KCriticalSection(KernelContext context)
        {
            _context = context;
            _lock = new object();
        }

        public void Enter()
        {
            Monitor.Enter(_lock);

            _recursionCount++;
        }

        public void Leave()
        {
            // 没有上锁
            if (_recursionCount == 0)
            {
                return;
            }

            // 退出锁
            if (--_recursionCount == 0)
            {
                // 调度器调度线程
                // scheduledCoresMask 的低4位记录了发生线程切换的 core
                ulong scheduledCoresMask = KScheduler.SelectThreads(_context);

                Monitor.Exit(_lock);

                KThread currentThread = KernelStatic.GetCurrentThread();
                bool isCurrentThreadSchedulable = currentThread != null && currentThread.IsSchedulable;
                if (isCurrentThreadSchedulable)
                {
                    // currentThread可以被4个模拟CPU调度
                    KScheduler.EnableScheduling(_context, scheduledCoresMask);
                }
                else
                {
                    // 不可调度， 1.要么有 customThreadStart 或者 被设置 _forcedUnschedulable
                    KScheduler.EnableSchedulingFromForeignThread(_context, scheduledCoresMask);

                    // If the thread exists but is not schedulable, we still want to suspend
                    // it if it's not runnable. That allows the kernel to still block HLE threads
                    // even if they are not scheduled on guest cores.
                    if (currentThread != null && !currentThread.IsSchedulable && currentThread.Context.Running)
                    {
                        // ServerBase loop
                        // 只有当 SchedulerWaitEvent被Reset之后，才会实际等待Set
                        currentThread.SchedulerWaitEvent.WaitOne();
                    }
                }
            }
            else
            {
                Monitor.Exit(_lock);
            }
        }
    }
}
