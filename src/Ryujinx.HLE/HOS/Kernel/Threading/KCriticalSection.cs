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
            // û������
            if (_recursionCount == 0)
            {
                return;
            }

            // �˳���
            if (--_recursionCount == 0)
            {
                // �����������߳�
                // scheduledCoresMask �ĵ�4λ��¼�˷����߳��л��� core
                ulong scheduledCoresMask = KScheduler.SelectThreads(_context);

                Monitor.Exit(_lock);

                KThread currentThread = KernelStatic.GetCurrentThread();
                bool isCurrentThreadSchedulable = currentThread != null && currentThread.IsSchedulable;
                if (isCurrentThreadSchedulable)
                {
                    // currentThread���Ա�4��ģ��CPU����
                    KScheduler.EnableScheduling(_context, scheduledCoresMask);
                }
                else
                {
                    // ���ɵ��ȣ� 1.Ҫô�� customThreadStart ���� ������ _forcedUnschedulable
                    KScheduler.EnableSchedulingFromForeignThread(_context, scheduledCoresMask);

                    // If the thread exists but is not schedulable, we still want to suspend
                    // it if it's not runnable. That allows the kernel to still block HLE threads
                    // even if they are not scheduled on guest cores.
                    if (currentThread != null && !currentThread.IsSchedulable && currentThread.Context.Running)
                    {
                        // ServerBase loop
                        // ֻ�е� SchedulerWaitEvent��Reset֮�󣬲Ż�ʵ�ʵȴ�Set
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
