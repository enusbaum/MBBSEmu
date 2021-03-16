using NLog;
using System;

namespace MBBSEmu.Memory
{
    /// <summary>
    ///     Represents DOS Real Mode memory.
    ///
    ///     Memory consists of 16-byte overlapped segments. Address 0000:0010 is the same as
    ///     address 0001:0000 and 2000:0050 is the same as 2005:0000. If you do the math, each
    ///     MSB in segment is 64k in length (0000-1000), of which you have 16, which yields a total
    ///     addressable memory space of 1MB.
    ///
    ///     We just allocate a 1MB memory block and map accordingly.
    ///
    ///     Memory is mapped with executable at the top of the address space, and 64k heap at
    ///     0x1000-0x2000
    /// </summary>
    public class RealModeMemoryCore : AbstractMemoryCore, IMemoryCore
    {
        private const int MAX_REAL_MODE_MEMORY = 1024 * 1024; // (1 mb)
        private readonly FarPtr HEAP_BASE = new FarPtr(0x1000, 0);
        private const int HEAP_MAX_SIZE = 64*1024;

        private readonly byte[] _memory = new byte[MAX_REAL_MODE_MEMORY];

        private readonly MemoryAllocator _memoryAllocator;

        public RealModeMemoryCore(ILogger logger) : base(logger)
        {
            _memoryAllocator = new MemoryAllocator(logger, HEAP_BASE, HEAP_MAX_SIZE);
        }

        public override Span<byte> VirtualToPhysical(ushort segment, ushort offset) => _memory.AsSpan((segment << 4) + offset);

        public override FarPtr AllocateRealModeSegment(ushort segmentSize = ushort.MaxValue) => throw new NotImplementedException();
        public override FarPtr AllocateBigMemoryBlock(ushort quantity, ushort size) => throw new NotImplementedException();
        public override FarPtr GetBigMemoryBlock(FarPtr block, ushort index) => throw new NotImplementedException();

        public override FarPtr Malloc(ushort size) => _memoryAllocator.Malloc(size);
        public override void Free(FarPtr ptr) => _memoryAllocator.Free(ptr);
    }
}