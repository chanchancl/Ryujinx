using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.Horizon.Common;
using System;

namespace Ryujinx.HLE.HOS.Kernel.Common
{
    static class KernelInit
    {
        private readonly struct MemoryRegion
        {
            public ulong Address { get; }
            public ulong Size { get; }

            public ulong EndAddress => Address + Size;

            public MemoryRegion(ulong address, ulong size)
            {
                Address = address;
                Size = size;
            }
        }

        public static void InitializeResourceLimit(KResourceLimit resourceLimit, MemorySize size)
        {
            static void EnsureSuccess(Result result)
            {
                if (result != Result.Success)
                {
                    throw new InvalidOperationException($"Unexpected result \"{result}\".");
                }
            }

            // 4GiB  0x1 0000 0000
            ulong ramSize = KSystemControl.GetDramSize(size);

            EnsureSuccess(resourceLimit.SetLimitValue(LimitableResource.Memory, (long)ramSize));
            EnsureSuccess(resourceLimit.SetLimitValue(LimitableResource.Thread, 800));
            EnsureSuccess(resourceLimit.SetLimitValue(LimitableResource.Event, 700));
            EnsureSuccess(resourceLimit.SetLimitValue(LimitableResource.TransferMemory, 200));
            EnsureSuccess(resourceLimit.SetLimitValue(LimitableResource.Session, 900));

            if (!resourceLimit.Reserve(LimitableResource.Memory, 0) ||
                !resourceLimit.Reserve(LimitableResource.Memory, 0x60000))
            {
                throw new InvalidOperationException("Unexpected failure reserving memory on resource limit.");
            }
        }

        public static KMemoryRegionManager[] GetMemoryRegions(MemorySize size, MemoryArrange arrange)
        {
            // 0x80000000 + 4G 6G 8G, usually 4G
            // 低地址2G + 4G
            /*  0
            *   |
            *   |
            *   2G
            *   |
            *   ServicePool // Not used
            *   | Address = SlabHeapEnd = 0x80b06000 ?= 2059 MiB , 0x00000000_80b0_6000
            *   | Size    = 0xFB32000,  ?= 251.20 MiB            , 0x00000000_0fb3_2000
            *   |
            *   nvServicesPool
            *   | Address = 2352 - 41.78125 = 2310.21875 MiB     , 0x00000000_9063_8000
            *   | Size    = 42784 KiB                            , 0x00000000_029c_8000
            *   |
            *   appletPool
            *   | Address = 2859 - 507 = 2352 Mib,               , 0x00000000_9300_0000
            *   | Size    = 507 MiB                              , 0x00000000_1fb0_0000
            *   |
            *   applicationPool
            *   | Address = 2048 + 4096-3285 = 2859 MiB          , 0x00000000_b2b0_0000
            *   | Size    = 3285 MiB                             , 0x00000000_cd50_0000
            *   |
            *   2G+4G  pollEnd 0x1_8000_0000
            *
            */
            // 6144 * MiB
            ulong poolEnd             = KSystemControl.GetDramEndAddress(size);
            // 3285 * MiB
            ulong applicationPoolSize = KSystemControl.GetApplicationPoolSize(arrange);
            //  507 * MiB
            ulong appletPoolSize      = KSystemControl.GetAppletPoolSize(arrange);

            MemoryRegion servicePool;
            MemoryRegion nvServicesPool;
            MemoryRegion appletPool;
            MemoryRegion applicationPool;

            //  42784 * KiB, 41.78125 MiB
            ulong nvServicesPoolSize = KSystemControl.GetMinimumNonSecureSystemPoolSize();

            applicationPool = new MemoryRegion(poolEnd - applicationPoolSize, applicationPoolSize);

            ulong nvServicesPoolEnd = applicationPool.Address - appletPoolSize;

            nvServicesPool = new MemoryRegion(nvServicesPoolEnd - nvServicesPoolSize, nvServicesPoolSize);
            appletPool = new MemoryRegion(nvServicesPoolEnd, appletPoolSize);

            // Note: There is an extra region used by the kernel, however
            // since we are doing HLE we are not going to use that memory, so give all
            // the remaining memory space to services.
            ulong servicePoolSize = nvServicesPool.Address - DramMemoryMap.SlabHeapEnd;

            servicePool = new MemoryRegion(DramMemoryMap.SlabHeapEnd, servicePoolSize);

            return new[]
            {
                GetMemoryRegion(applicationPool),
                GetMemoryRegion(appletPool),
                GetMemoryRegion(servicePool),
                GetMemoryRegion(nvServicesPool),
            };
        }

        private static KMemoryRegionManager GetMemoryRegion(MemoryRegion region)
        {
            // 将每个 MemoryRegion 转为以page为单位进行管理的连续区域
            return new KMemoryRegionManager(region.Address, region.Size, region.EndAddress);
        }
    }
}
