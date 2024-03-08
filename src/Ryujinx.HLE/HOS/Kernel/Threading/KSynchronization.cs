using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Kernel.Common;
using Ryujinx.Horizon.Common;
using System;
using System.Buffers;
using System.Collections.Generic;

namespace Ryujinx.HLE.HOS.Kernel.Threading
{
    class KSynchronization
    {
        private readonly KernelContext _context;

        public KSynchronization(KernelContext context)
        {
            _context = context;
        }

        public Result WaitFor(Span<KSynchronizationObject> syncObjs, long timeout, out int handleIndex)
        {
            handleIndex = 0;

            Result result = KernelResult.TimedOut;

            _context.CriticalSection.Enter();

            // Check if objects are already signaled before waiting.
            for (int index = 0; index < syncObjs.Length; index++)
            {
                if (!syncObjs[index].IsSignaled())
                {
                    continue;
                }

                handleIndex = index;

                _context.CriticalSection.Leave();

                return Result.Success;
            }

            if (timeout == 0)
            {
                _context.CriticalSection.Leave();

                return result;
            }

            KThread currentThread = KernelStatic.GetCurrentThread();

            if (currentThread.TerminationRequested)
            {
                result = KernelResult.ThreadTerminating;
            }
            else if (currentThread.SyncCancelled)
            {
                currentThread.SyncCancelled = false;

                result = KernelResult.Cancelled;
            }
            else
            {
                LinkedListNode<KThread>[] syncNodesArray = ArrayPool<LinkedListNode<KThread>>.Shared.Rent(syncObjs.Length);

                Span<LinkedListNode<KThread>> syncNodes = syncNodesArray.AsSpan(0, syncObjs.Length);

                for (int index = 0; index < syncObjs.Length; index++)
                {
                    // ����ʱ������֪ͨ������syncObjs[index]�ϵȴ����߳�
                    syncNodes[index] = syncObjs[index].AddWaitingThread(currentThread);
                }

                currentThread.WaitingSync = true;
                currentThread.SignaledObj = null;
                currentThread.ObjSyncResult = result;

                // Reset _schedulerWaitEvent ����CriticalSection.Leave()��ȥ�ȴ�
                currentThread.Reschedule(ThreadSchedState.Paused);
                
                if (timeout > 0)
                {
                    // ��timeout���ڵ�ʱ�򣬵��� currentThread.Timeup
                    _context.TimeManager.ScheduleFutureInvocation(currentThread, timeout);
                }

                // �ȴ� currentThread.SchedulerWaitEvent.WaitOne();
                // ���������ǣ���ྭ�� timeout ʱ��󣬷������µĵ�����
                //  KThread.TimeUp -> ReleaseAndResume -> SetNewSchedFlags -> AdjustScheduling -> _schedulerWaitEvent.Set();
                //      
                _context.CriticalSection.Leave();

                // ���ڣ����߱�ʵ�ʻ��Ѻ�

                currentThread.WaitingSync = false;

                if (timeout > 0)
                {
                    _context.TimeManager.UnscheduleFutureInvocation(currentThread);
                }

                _context.CriticalSection.Enter();

                result = currentThread.ObjSyncResult;

                handleIndex = -1;

                for (int index = 0; index < syncObjs.Length; index++)
                {
                    syncObjs[index].RemoveWaitingThread(syncNodes[index]);

                    if (syncObjs[index] == currentThread.SignaledObj)
                    {
                        handleIndex = index;
                    }
                }

                ArrayPool<LinkedListNode<KThread>>.Shared.Return(syncNodesArray);
            }

            // ����Ͳ���ȴ��ˣ���Ϊ _schedulerWaitEvent û�б�Reset
            _context.CriticalSection.Leave();

            return result;
        }

        public void SignalObject(KSynchronizationObject syncObj)
        {
            _context.CriticalSection.Enter();

            if (syncObj.IsSignaled())
            {
                // ���ѵȴ�syncObj�������߳�
                LinkedListNode<KThread> node = syncObj.WaitingThreads.First;

                while (node != null)
                {
                    KThread thread = node.Value;

                    if ((thread.SchedFlags & ThreadSchedState.LowMask) == ThreadSchedState.Paused)
                    {
                        thread.SignaledObj = syncObj;
                        thread.ObjSyncResult = Result.Success;

                        // ͨ�� _schedulerWaitEvent.Set() �����źţ� 
                        thread.Reschedule(ThreadSchedState.Running);
                    }

                    node = node.Next;
                }
            }

            _context.CriticalSection.Leave();
        }
    }
}
