﻿using Iced.Intel;
using MBBSEmu.Disassembler.Artifacts;
using MBBSEmu.Logging;
using NLog;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Decoder = Iced.Intel.Decoder;


namespace MBBSEmu.Memory
{
    /// <summary>
    ///     Handles Memory Operations for the Module
    ///
    ///     Information of x86 Memory Segmentation: https://en.wikipedia.org/wiki/X86_memory_segmentation
    /// </summary>
    public class ProtectedMemoryCore : IMemoryCore
    {
        protected readonly ILogger _logger;
        private readonly byte[][] _memorySegments = new byte[0x10000][];
        private readonly Segment[] _segments = new Segment[0x10000];
        private readonly Instruction[][] _decompiledSegments = new Instruction[0x10000][];

        private readonly Dictionary<string, FarPtr> _variablePointerDictionary = new();
        private const ushort HEAP_BASE_SEGMENT = 0x1000; //0x1000->0x1FFF == 256MB
        private FarPtr _nextHeapPointer = new FarPtr(HEAP_BASE_SEGMENT, 0);
        private const ushort REALMODE_BASE_SEGMENT = 0x2000; //0x2000->0x2FFF == 256MB
        private FarPtr _currentRealModePointer = new FarPtr(REALMODE_BASE_SEGMENT, 0);
        private readonly PointerDictionary<Dictionary<ushort, FarPtr>> _bigMemoryBlocks = new();
        private readonly Dictionary<ushort, MemoryAllocator> _heapAllocators = new();

        /// <summary>
        ///     Default Compiler Hints for use on methods within the MemoryCore
        ///
        ///     Works fastest with just AggressiveOptimization. Enabling AggressiveInlining slowed
        ///     down the code.
        /// </summary>
        private const MethodImplOptions CompilerOptimizations = MethodImplOptions.AggressiveOptimization;

        public ProtectedMemoryCore(ILogger logger)
        {
            _logger = logger;
            //Add Segment 0 by default, stack segment
            AddSegment(0);
        }

        public FarPtr Malloc(ushort size)
        {
            foreach (var allocator in _heapAllocators.Values)
            {
                if (allocator.RemainingBytes < size)
                    continue;

                var ptr = allocator.Malloc(size);
                if (!ptr.IsNull())
                    return ptr;
            }

            // no segment could allocate, create a new allocator to handle it
            AddSegment(_nextHeapPointer.Segment);

            // I hate null pointers/offsets so start the allocator at offset 2
            var memoryAllocator = new MemoryAllocator(_logger, _nextHeapPointer + 2, 0xFFFE);
            _heapAllocators.Add(_nextHeapPointer.Segment, memoryAllocator);

            _nextHeapPointer.Segment++;

            return memoryAllocator.Malloc(size);
        }

        public void Free(FarPtr ptr)
        {
            if (!_heapAllocators.TryGetValue(ptr.Segment, out var memoryAllocator))
            {
                _logger.Error($"Attempted to deallocate memory from an unknown segment {ptr}");
                return;
            }

            memoryAllocator.Free(ptr);
        }

        /// <summary>
        ///     Deletes all defined Segments from Memory
        /// </summary>
        public void Clear()
        {
            Array.Clear(_memorySegments, 0, _memorySegments.Length);
            Array.Clear(_segments, 0, _segments.Length);
            Array.Clear(_decompiledSegments, 0, _decompiledSegments.Length);

            _variablePointerDictionary.Clear();
            _nextHeapPointer = new FarPtr(HEAP_BASE_SEGMENT, 0);
            _currentRealModePointer = new FarPtr(REALMODE_BASE_SEGMENT, 0);
            _bigMemoryBlocks.Clear();
            _heapAllocators.Clear();
        }

        /// <summary>
        ///     Allocates the specified variable name with the desired size
        /// </summary>
        /// <param name="name"></param>
        /// <param name="size"></param>
        /// <param name="declarePointer"></param>
        /// <returns></returns>
        public FarPtr AllocateVariable(string name, ushort size, bool declarePointer = false)
        {
            if (!string.IsNullOrEmpty(name) && _variablePointerDictionary.ContainsKey(name))
            {
                _logger.Warn($"Attempted to re-allocate variable: {name}");
                return _variablePointerDictionary[name];
            }

            var newPointer = Malloc(size);
            ((IMemoryCore)this).SetZero(newPointer, size);

            if (declarePointer && string.IsNullOrEmpty(name))
                throw new ArgumentException("Unsupported operation, declaring pointer type for NULL named variable");

            if (!string.IsNullOrEmpty(name))
            {
                _variablePointerDictionary[name] = newPointer;

                if (declarePointer)
                {
                    var variablePointer = AllocateVariable($"*{name}", 0x4, declarePointer: false);
                    ((IMemoryCore)this).SetArray(variablePointer, newPointer.Data);
                }
            }

            return newPointer;
        }

        /// <summary>
        ///     Returns the pointer to a defined variable
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public FarPtr GetVariablePointer(string name)
        {
            if (!TryGetVariablePointer(name, out var result))
                throw new ArgumentException($"Unknown Variable: {name}");

            return result;
        }

        /// <summary>
        ///     Safe retrieval of a pointer to a defined variable
        ///
        ///     Returns false if the variable isn't defined
        /// </summary>
        /// <param name="name"></param>
        /// <param name="pointer"></param>
        /// <returns></returns>
        public bool TryGetVariablePointer(string name, out FarPtr pointer)
        {
            if (!_variablePointerDictionary.TryGetValue(name, out var result))
            {
                pointer = null;
                return false;
            }

            pointer = result;
            return true;
        }

        /// <summary>
        ///     Safely try to retrieve a variable, or allocate it if it's not present
        /// </summary>
        /// <param name="name"></param>
        /// <param name="size"></param>
        /// <param name="declarePointer">
        ///     Some variables are pointers to an underlying value. Setting this value to TRUE declares not only the
        ///     desired variable of NAME of SIZE, but also a 2 byte variable named "*NAME" which holds a pointer to NAME
        /// </param>
        /// <returns></returns>
        public FarPtr GetOrAllocateVariablePointer(string name, ushort size = 0x0, bool declarePointer = false)
        {
            if (_variablePointerDictionary.TryGetValue(name, out var result))
                return result;

            return AllocateVariable(name, size, declarePointer);
        }

        /// <summary>
        ///     Adds a new Memory Segment containing 65536 bytes
        /// </summary>
        /// <param name="segmentNumber"></param>
        /// <param name="size"></param>
        public void AddSegment(ushort segmentNumber, int size = 0x10000)
        {
            if (_memorySegments[segmentNumber] != null)
                throw new Exception($"Segment with number {segmentNumber} already defined");

            _memorySegments[segmentNumber] = new byte[size];
        }

        /// <summary>
        ///     Removes the specified segment. Typically only used by a test.
        /// </summary>
        /// <param name="segment"></param>
        public void RemoveSegment(ushort segment)
        {
            _memorySegments[segment] = null;
            _segments[segment] = null;
            _decompiledSegments[segment] = null;
        }

        /// <summary>
        ///     Directly adds a raw segment from an NE file segment
        /// </summary>
        /// <param name="segment"></param>
        public void AddSegment(Segment segment)
        {
            //Get Address for this Segment
            var segmentMemory = new byte[0x10000];

            //Add the data to memory and record the segment offset in memory
            Array.Copy(segment.Data, 0, segmentMemory, 0, segment.Data.Length);
            _memorySegments[segment.Ordinal] = segmentMemory;

            if (segment.Flags.Contains(EnumSegmentFlags.Code))
            {
                //Decode the Segment
                var instructionList = new InstructionList();
                var codeReader = new ByteArrayCodeReader(segment.Data);
                var decoder = Decoder.Create(16, codeReader);
                decoder.IP = 0x0;

                while (decoder.IP < (ulong)segment.Data.Length)
                {
                    decoder.Decode(out instructionList.AllocUninitializedElement());
                }

                _decompiledSegments[segment.Ordinal] = new Instruction[0x10000];
                foreach (var i in instructionList)
                {
                    _decompiledSegments[segment.Ordinal][i.IP16] = i;
                }
            }

            _segments[segment.Ordinal] = segment;
        }

        /// <summary>
        ///     Adds a Decompiled code segment
        /// </summary>
        /// <param name="segmentNumber"></param>
        /// <param name="segmentInstructionList"></param>
        public void AddSegment(ushort segmentNumber, InstructionList segmentInstructionList)
        {
            _decompiledSegments[segmentNumber] = new Instruction[0x10000];
            foreach (var i in segmentInstructionList)
            {
                _decompiledSegments[segmentNumber][i.IP16] = i;
            }
        }

        /// <summary>
        ///     Returns the Segment information for the desired Segment Number
        /// </summary>
        /// <param name="segmentNumber"></param>
        /// <returns></returns>
        [MethodImpl(CompilerOptimizations)]
        public Segment GetSegment(ushort segmentNumber) => _segments[segmentNumber];

        /// <summary>
        ///     Verifies the specified segment is defined
        /// </summary>
        /// <param name="segmentNumber"></param>
        /// <returns></returns>
        [MethodImpl(CompilerOptimizations)]
        public bool HasSegment(ushort segmentNumber) => _memorySegments[segmentNumber] != null;

        /// <summary>
        ///     Returns the decompiled instruction from the specified segment:pointer
        /// </summary>
        /// <param name="segment"></param>
        /// <param name="instructionPointer"></param>
        /// <returns></returns>
        [MethodImpl(CompilerOptimizations)]
        public Instruction GetInstruction(ushort segment, ushort instructionPointer) =>
            _decompiledSegments[segment][instructionPointer];

        public Instruction Recompile(ushort segment, ushort instructionPointer)
        {
            //If it wasn't able to decompile linear through the data, there might have been
            //data in the path of the code that messed up decoding, in this case, we grab up to
            //6 bytes at the IP and decode the instruction manually. This works 9 times out of 10
            Span<byte> segmentData = _segments[segment].Data;
            var reader = new ByteArrayCodeReader(segmentData.Slice(instructionPointer, 6).ToArray());
            var decoder = Decoder.Create(16, reader);
            decoder.IP = instructionPointer;
            decoder.Decode(out var outputInstruction);

            _decompiledSegments[segment][instructionPointer] = outputInstruction;
            return outputInstruction;
        }

        /// <summary>
        ///     Returns a single byte from the specified segment:offset
        /// </summary>
        /// <param name="segment"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        [MethodImpl(CompilerOptimizations)]
        public byte GetByte(ushort segment, ushort offset) => _memorySegments[segment][offset];

        /// <summary>
        ///     Returns an unsigned word from the specified segment:offset
        /// </summary>
        /// <param name="segment"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        [MethodImpl(CompilerOptimizations)]
        public unsafe ushort GetWord(ushort segment, ushort offset) {
            fixed (byte *p = _memorySegments[segment]) {
                ushort* ptr = (ushort*)(p + offset);
                return *ptr;
            }
        }

        [MethodImpl(CompilerOptimizations)]
        public unsafe uint GetDWord(ushort segment, ushort offset)
        {
            fixed (byte* p = _memorySegments[segment])
            {
                uint* ptr = (uint*)(p + offset);
                return *ptr;
            }
        }

        /// <summary>
        ///     Returns an array with the desired count from the specified segment:offset
        /// </summary>
        /// <param name="segment"></param>
        /// <param name="offset"></param>
        /// <param name="count"></param>
        /// <returns></returns>
        [MethodImpl(CompilerOptimizations)]
        public ReadOnlySpan<byte> GetArray(ushort segment, ushort offset, ushort count) =>
            _memorySegments[segment].AsSpan().Slice(offset, count);

        /// <summary>
        ///     Returns an array containing the cstring stored at the specified segment:offset
        /// </summary>
        /// <param name="segment"></param>
        /// <param name="offset"></param>
        /// <param name="stripNull"></param>
        /// <returns></returns>
        /// TODO move to IMemoryCore
        public ReadOnlySpan<byte> GetString(ushort segment, ushort offset, bool stripNull = false)
        {
            ReadOnlySpan<byte> segmentSpan = _memorySegments[segment];

            for (int i = offset; i <= ushort.MaxValue; i++)
            {
                if (segmentSpan[i] == 0x0)
                    return segmentSpan.Slice(offset, (i - offset) + (stripNull ? 0 : 1));
            }

            throw new Exception($"Invalid String at {segment:X4}:{offset:X4}");
        }

        /// <summary>
        ///     Sets the specified byte at the defined variable
        /// </summary>
        /// <param name="variableName"></param>
        /// <param name="value"></param>
        [MethodImpl(CompilerOptimizations)]
        public void SetByte(string variableName, byte value) => SetByte(GetVariablePointer(variableName), value);

        /// <summary>
        ///     Sets the specified byte at the desired pointer
        /// </summary>
        /// <param name="pointer"></param>
        /// <param name="value"></param>
        [MethodImpl(CompilerOptimizations)]
        public void SetByte(FarPtr pointer, byte value) => SetByte(pointer.Segment, pointer.Offset, value);

        /// <summary>
        ///     Sets the specified byte at the desired segment:offset
        /// </summary>
        /// <param name="segment"></param>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        [MethodImpl(CompilerOptimizations)]
        public void SetByte(ushort segment, ushort offset, byte value) =>_memorySegments[segment][offset] = value;

        /// <summary>
        ///     Sets the specified word at the desired pointer
        /// </summary>
        /// <param name="pointer"></param>
        /// <param name="value"></param>
        [MethodImpl(CompilerOptimizations)]
        public void SetWord(FarPtr pointer, ushort value) => SetWord(pointer.Segment, pointer.Offset, value);

        /// <summary>
        ///     Sets the specified word at the desired segment:offset
        /// </summary>
        /// <param name="segment"></param>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        [MethodImpl(CompilerOptimizations)]
        public unsafe void SetWord(ushort segment, ushort offset, ushort value)
        {
            fixed (byte *dst = _memorySegments[segment]) {
                ushort *ptr = (ushort*)(dst + offset);
                *ptr = value;
            }
        }

        /// <summary>
        ///     Sets the specified word at the defined variable
        /// </summary>
        /// <param name="variableName"></param>
        /// <param name="value"></param>
        [MethodImpl(CompilerOptimizations)]
        public void SetWord(string variableName, ushort value) => SetWord(GetVariablePointer(variableName), value);

        [MethodImpl(CompilerOptimizations)]
        public void SetDWord(FarPtr pointer, uint value) => SetDWord(pointer.Segment, pointer.Offset, value);

        [MethodImpl(CompilerOptimizations)]
        public unsafe void SetDWord(ushort segment, ushort offset, uint value)
        {
            fixed (byte* dst = _memorySegments[segment])
            {
                uint* ptr = (uint*)(dst + offset);
                *ptr = value;
            }
        }

        [MethodImpl(CompilerOptimizations)]
        public void SetArray(ushort segment, ushort offset, ReadOnlySpan<byte> array)
        {
            var destinationSpan = _memorySegments[segment].AsSpan(offset);
            array.CopyTo(destinationSpan);
        }

        /// <summary>
        ///     Writes the specified byte the specified number of times starting at the specified pointer
        /// </summary>
        /// <param name="segment"></param>
        /// <param name="offset"></param>
        /// <param name="count"></param>
        /// <param name="value"></param>
        public void FillArray(ushort segment, ushort offset, int count, byte value)
        {
            Array.Fill(_memorySegments[segment], value, offset, count);
        }

        /// <summary>
        ///     Allocates the specific number of Big Memory Blocks with the desired size
        /// </summary>
        /// <param name="quantity"></param>
        /// <param name="size"></param>
        /// <returns></returns>
        public FarPtr AllocateBigMemoryBlock(ushort quantity, ushort size)
        {
            var newBlockOffset = _bigMemoryBlocks.Allocate(new Dictionary<ushort, FarPtr>());

            //Fill the Region
            for (ushort i = 0; i < quantity; i++)
                _bigMemoryBlocks[newBlockOffset].Add(i, AllocateVariable($"ALCBLOK-{newBlockOffset}-{i}", size));

            return new FarPtr(0xFFFF, (ushort)newBlockOffset);
        }

        /// <summary>
        ///     Returns the specified block by index in the desired memory block
        /// </summary>
        /// <param name="block"></param>
        /// <param name="index"></param>
        /// <returns></returns>
        [MethodImpl(CompilerOptimizations)]
        public FarPtr GetBigMemoryBlock(FarPtr block, ushort index) => _bigMemoryBlocks[block.Offset][index];

        /// <summary>
        ///     Returns a newly allocated Segment in "Real Mode" memory
        /// </summary>
        /// <returns></returns>
        public FarPtr AllocateRealModeSegment(ushort segmentSize = ushort.MaxValue)
        {
            _currentRealModePointer.Segment++;
            var realModeSegment = new FarPtr(_currentRealModePointer);
            AddSegment(realModeSegment.Segment, segmentSize);
            return realModeSegment;
        }

        public void Dispose()
        {
            Clear();
        }
    }
}