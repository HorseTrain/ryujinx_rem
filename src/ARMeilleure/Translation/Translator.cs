using ARMeilleure.CodeGen;
using ARMeilleure.Common;
using ARMeilleure.Decoders;
using ARMeilleure.Diagnostics;
using ARMeilleure.Instructions;
using ARMeilleure.IntermediateRepresentation;
using ARMeilleure.Memory;
using ARMeilleure.State;
using ARMeilleure.Translation.Cache;
using ARMeilleure.Translation.PTC;
using Gommon;
using Ryujinx.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using static ARMeilleure.IntermediateRepresentation.Operand.Factory;

namespace ARMeilleure.Translation
{
    public unsafe class Translator
    {
        private readonly IJitMemoryAllocator _allocator;
        private readonly ConcurrentQueue<KeyValuePair<ulong, TranslatedFunction>> _oldFuncs;

        private readonly Ptc _ptc;

        internal TranslatorCache<TranslatedFunction> Functions { get; }
        internal IAddressTable<ulong> FunctionTable { get; }
        internal EntryTable<uint> CountTable { get; }
        internal TranslatorStubs Stubs { get; }
        internal TranslatorQueue Queue { get; }
        internal IMemoryManager Memory { get; }

        private Thread[] _backgroundTranslationThreads;
        private volatile int _threadCount;

        void* rem_context;

        static ulong call_svc(ulong address, int svc)
        {
            NativeInterface.SupervisorCall(address, svc);

            return address + 4;
        }

        static bool single_instruction = true;

        ulong execute_single(void* context_ptr, ulong address)
        {
            TranslatedFunction func = GetOrTranslate(address, ExecutionMode.Aarch64);

            ulong start = address;

            address = func._func((IntPtr)context_ptr);

            if (address != start + 4)
            {
                Console.WriteLine("asasd");

                throw new Exception();
            }

            return address;
        }

        delegate ulong call_svc_delegate(ulong address, int svc);
        delegate ulong call_undefined_delegate(void* context, ulong address);
        delegate ulong get_counter_delegate();

        call_svc_delegate svc_function = call_svc;
        get_counter_delegate get_counter_function = NativeInterface.GetCntpctEl0;
        call_undefined_delegate call_undefined_function;
        
        public Translator(IJitMemoryAllocator allocator, IMemoryManager memory, IAddressTable<ulong> functionTable)
        {
            _allocator = allocator;
            Memory = memory;

            _oldFuncs = new ConcurrentQueue<KeyValuePair<ulong, TranslatedFunction>>();

            _ptc = new Ptc();

            Queue = new TranslatorQueue();

            JitCache.Initialize(allocator);

            CountTable = new EntryTable<uint>();
            Functions = new TranslatorCache<TranslatedFunction>();
            FunctionTable = functionTable;
            Stubs = new TranslatorStubs(FunctionTable);

            FunctionTable.Fill = (ulong)Stubs.SlowDispatchStub;

            rem_imports.aarch64_context_offsets offsets = new rem_imports.aarch64_context_offsets()
            {
                x_offset = (int)Marshal.OffsetOf<NativeContext.NativeCtxStorage>(nameof(NativeContext.NativeCtxStorage.X)),
                q_offset = (int)Marshal.OffsetOf<NativeContext.NativeCtxStorage>(nameof(NativeContext.NativeCtxStorage.V)),

                n_offset = (int)Marshal.OffsetOf<NativeContext.NativeCtxStorage>(nameof(NativeContext.NativeCtxStorage.Flags)) + (int)PState.NFlag * sizeof(uint),
                z_offset = (int)Marshal.OffsetOf<NativeContext.NativeCtxStorage>(nameof(NativeContext.NativeCtxStorage.Flags)) + (int)PState.ZFlag * sizeof(uint),
                c_offset = (int)Marshal.OffsetOf<NativeContext.NativeCtxStorage>(nameof(NativeContext.NativeCtxStorage.Flags)) + (int)PState.CFlag * sizeof(uint),
                v_offset = (int)Marshal.OffsetOf<NativeContext.NativeCtxStorage>(nameof(NativeContext.NativeCtxStorage.Flags)) + (int)PState.VFlag * sizeof(uint),

                exclusive_address_offset =  (int)Marshal.OffsetOf<NativeContext.NativeCtxStorage>(nameof(NativeContext.NativeCtxStorage.ExclusiveAddress)),
                exclusive_value_offset =  (int)Marshal.OffsetOf<NativeContext.NativeCtxStorage>(nameof(NativeContext.NativeCtxStorage.ExclusiveValueLow)),

                fpcr_offset = (int)Marshal.OffsetOf<NativeContext.NativeCtxStorage>(nameof(NativeContext.NativeCtxStorage.FpFlags)),
                fpsr_offset = (int)Marshal.OffsetOf<NativeContext.NativeCtxStorage>(nameof(NativeContext.NativeCtxStorage.FpFlags)) + 4,

                thread_local_1 = (int)Marshal.OffsetOf<NativeContext.NativeCtxStorage>(nameof(NativeContext.NativeCtxStorage.TpidrEl0)),
                thread_local_0 = (int)Marshal.OffsetOf<NativeContext.NativeCtxStorage>(nameof(NativeContext.NativeCtxStorage.TpidrroEl0)),

                context_size = sizeof(NativeContext.NativeCtxStorage)
            } ;

            call_undefined_function = execute_single;

            void* undefined = (void*)Marshal.GetFunctionPointerForDelegate(call_undefined_function);
            //void* undefined = null;

            rem_context = rem_imports.create_rem_context(
                (void*)memory.PageTablePointer, 
                &offsets, 
                (void*)Marshal.GetFunctionPointerForDelegate(svc_function), 
                (void*)Marshal.GetFunctionPointerForDelegate(get_counter_function), 
                undefined
            );
        }

        public IPtcLoadState LoadDiskCache(string titleIdText, string displayVersion, bool enabled, string cacheSelector)
        {
            _ptc.Initialize(titleIdText, displayVersion, enabled, Memory.Type, cacheSelector);
            return _ptc;
        }

        public void PrepareCodeRange(ulong address, ulong size)
        {
            if (_ptc.Profiler.StaticCodeSize == 0)
            {
                _ptc.Profiler.StaticCodeStart = address;
                _ptc.Profiler.StaticCodeSize = size;
            }
        }

        public void Execute(State.ExecutionContext context, ulong address)
        {
            if (Interlocked.Increment(ref _threadCount) == 1)
            {
                if (_ptc.State == PtcState.Enabled)
                {
                    Debug.Assert(Functions.Count == 0);
                    _ptc.LoadTranslations(this);
                    _ptc.MakeAndSaveTranslations(this);
                }

                _ptc.Profiler.Start();

                _ptc.Disable();

                // Simple heuristic, should be user configurable in future. (1 for 4 core/ht or less, 2 for 6 core + ht
                // etc). All threads are normal priority except from the last, which just fills as much of the last core
                // as the os lets it with a low priority. If we only have one rejit thread, it should be normal priority
                // as highCq code is performance critical.
                //
                // TODO: Use physical cores rather than logical. This only really makes sense for processors with
                // hyperthreading. Requires OS specific code.
                int unboundedThreadCount = Math.Max(1, (Environment.ProcessorCount - 6) / 3);
                int threadCount = Math.Min(4, unboundedThreadCount);

                Thread[] backgroundTranslationThreads = new Thread[threadCount];

                for (int i = 0; i < threadCount; i++)
                {
                    bool last = i != 0 && i == unboundedThreadCount - 1;

                    backgroundTranslationThreads[i] = new(BackgroundTranslate)
                    {
                        Name = "CPU.BackgroundTranslatorThread." + i,
                        Priority = last ? ThreadPriority.Lowest : ThreadPriority.Normal,
                    };

                    backgroundTranslationThreads[i].Start();
                }

                Interlocked.Exchange(ref _backgroundTranslationThreads, backgroundTranslationThreads);
            }

            Statistics.InitializeTimer();

            NativeInterface.RegisterThread(context, Memory, this);

            if (true)
            {
                void* is_running = (byte*)context.NativeContextPtr + Marshal.OffsetOf<NativeContext.NativeCtxStorage>(nameof(NativeContext.NativeCtxStorage.Running));

                address = rem_imports.jit_until_long_jump(rem_context, address, (byte*)context.NativeContextPtr, (bool*)is_running, null);
            }
            else
            {
                if (Optimizations.UseUnmanagedDispatchLoop)
                {
                    Stubs.DispatchLoop(context.NativeContextPtr, address);
                }
                else
                {
                    do
                    {
                        address = ExecuteSingle(context, address);
                    }
                    while (context.Running && address != 0);
                }
            }

            NativeInterface.UnregisterThread();

            if (Interlocked.Decrement(ref _threadCount) == 0)
            {
                Queue.Dispose();

                Thread[] backgroundTranslationThreads = Interlocked.Exchange(ref _backgroundTranslationThreads, null);

                if (backgroundTranslationThreads != null)
                {
                    foreach (Thread thread in backgroundTranslationThreads)
                    {
                        thread.Join();
                    }
                }

                ClearJitCache();

                Stubs.Dispose();
                FunctionTable.Dispose();
                CountTable.Dispose();

                _ptc.Close();
                _ptc.Profiler.Stop();

                _ptc.Dispose();
                _ptc.Profiler.Dispose();
            }
        }

        private ulong ExecuteSingle(State.ExecutionContext context, ulong address)
        {
            TranslatedFunction func = GetOrTranslate(address, context.ExecutionMode);

            Statistics.StartTimer();

            ulong nextAddr = func.Execute(Stubs.ContextWrapper, context);

            Statistics.StopTimer(address);

            return nextAddr;
        }

        public ulong Step(State.ExecutionContext context, ulong address)
        {
            TranslatedFunction func = Translate(address, context.ExecutionMode, highCq: false, singleStep: true);

            address = func.Execute(Stubs.ContextWrapper, context);

            EnqueueForDeletion(address, func);

            return address;
        }

        internal TranslatedFunction GetOrTranslate(ulong address, ExecutionMode mode)
        {
            if (!Functions.TryGetValue(address, out TranslatedFunction func))
            {
                func = Translate(address, mode, highCq: false);

                TranslatedFunction oldFunc = Functions.GetOrAdd(address, func.GuestSize, func);

                if (oldFunc != func)
                {
                    JitCache.Unmap(func.FuncPointer);
                    func = oldFunc;
                }

                if (_ptc.Profiler.Enabled)
                {
                    _ptc.Profiler.AddEntry(address, mode, highCq: false);
                }

                RegisterFunction(address, func);
            }

            return func;
        }

        internal void RegisterFunction(ulong guestAddress, TranslatedFunction func)
        {
            if (FunctionTable.IsValid(guestAddress) && (Optimizations.AllowLcqInFunctionTable || func.HighCq))
            {
                Volatile.Write(ref FunctionTable.GetValue(guestAddress), (ulong)func.FuncPointer);
            }
        }

        internal TranslatedFunction Translate(ulong address, ExecutionMode mode, bool highCq, bool singleStep = false)
        {
            ArmEmitterContext context = new(
                Memory,
                CountTable,
                FunctionTable,
                Stubs,
                address,
                highCq,
                _ptc.State != PtcState.Disabled,
                mode: Aarch32Mode.User);

            Logger.StartPass(PassName.Decoding);

            Block[] blocks = Decoder.Decode(Memory, address, mode, highCq, singleStep ? DecoderMode.SingleInstruction : DecoderMode.MultipleBlocks);

            Logger.EndPass(PassName.Decoding);

            Logger.StartPass(PassName.Translation);

            EmitSynchronization(context);

            if (blocks[0].Address != address)
            {
                context.Branch(context.GetLabel(address));
            }

            ControlFlowGraph cfg = EmitAndGetCFG(context, blocks, out Range funcRange, out Counter<uint> counter);

            if (cfg == null)
            {
                return null;
            }

            ulong funcSize = funcRange.End - funcRange.Start;

            Logger.EndPass(PassName.Translation, cfg);

            Logger.StartPass(PassName.RegisterUsage);

            RegisterUsage.RunPass(cfg, mode);

            Logger.EndPass(PassName.RegisterUsage);

            OperandType retType = OperandType.I64;
            OperandType[] argTypes = [OperandType.I64];

            CompilerOptions options = highCq ? CompilerOptions.HighCq : CompilerOptions.None;

            if (context.HasPtc && !singleStep)
            {
                options |= CompilerOptions.Relocatable;
            }

            CompiledFunction compiledFunc = Compiler.Compile(cfg, argTypes, retType, options, RuntimeInformation.ProcessArchitecture);

            if (context.HasPtc && !singleStep)
            {
                Hash128 hash = Ptc.ComputeHash(Memory, address, funcSize);

                _ptc.WriteCompiledFunction(address, funcSize, hash, highCq, compiledFunc);
            }

            GuestFunction func = compiledFunc.MapWithPointer<GuestFunction>(out nint funcPointer);

            Allocators.ResetAll();

            return new TranslatedFunction(func, funcPointer, counter, funcSize, highCq);
        }

        private void BackgroundTranslate()
        {
            while (_threadCount != 0 && Queue.TryDequeue(out RejitRequest request))
            {
                TranslatedFunction func = Translate(request.Address, request.Mode, highCq: true);

                Functions.AddOrUpdate(request.Address, func.GuestSize, func, (key, oldFunc) =>
                {
                    EnqueueForDeletion(key, oldFunc);
                    return func;
                });

                if (_ptc.Profiler.Enabled)
                {
                    _ptc.Profiler.UpdateEntry(request.Address, request.Mode, highCq: true);
                }

                RegisterFunction(request.Address, func);
            }
        }

        private readonly struct Range
        {
            public ulong Start { get; }
            public ulong End { get; }

            public Range(ulong start, ulong end)
            {
                Start = start;
                End = end;
            }
        }

        private static ControlFlowGraph EmitAndGetCFG(
            ArmEmitterContext context,
            Block[] blocks,
            out Range range,
            out Counter<uint> counter)
        {
            counter = null;

            ulong rangeStart = ulong.MaxValue;
            ulong rangeEnd = 0;

            for (int blkIndex = 0; blkIndex < blocks.Length; blkIndex++)
            {
                Block block = blocks[blkIndex];

                if (!block.Exit)
                {
                    if (rangeStart > block.Address)
                    {
                        rangeStart = block.Address;
                    }

                    if (rangeEnd < block.EndAddress)
                    {
                        rangeEnd = block.EndAddress;
                    }
                }

                if (block.Address == context.EntryAddress)
                {
                    if (!context.HighCq)
                    {
                        EmitRejitCheck(context, out counter);
                    }

                    context.ClearQcFlag();
                }

                context.CurrBlock = block;

                context.MarkLabel(context.GetLabel(block.Address));

                if (block.Exit)
                {
                    // Left option here as it may be useful if we need to return to managed rather than tail call in
                    // future. (eg. for debug)
                    bool useReturns = false;

                    InstEmitFlowHelper.EmitVirtualJump(context, Const(block.Address), isReturn: useReturns);
                }
                else
                {
                    for (int opcIndex = 0; opcIndex < block.OpCodes.Count; opcIndex++)
                    {
                        OpCode opCode = block.OpCodes[opcIndex];

                        context.CurrOp = opCode;

                        bool isLastOp = opcIndex == block.OpCodes.Count - 1;

                        if (isLastOp)
                        {
                            context.SyncQcFlag();

                            if (block.Branch != null && !block.Branch.Exit && block.Branch.Address <= block.Address)
                            {
                                EmitSynchronization(context);
                            }
                        }

                        Operand lblPredicateSkip = default;

                        if (context.IsInIfThenBlock && context.CurrentIfThenBlockCond != Condition.Al)
                        {
                            lblPredicateSkip = Label();

                            InstEmitFlowHelper.EmitCondBranch(context, lblPredicateSkip, context.CurrentIfThenBlockCond.Invert());
                        }

                        if (opCode is OpCode32 op && op.Cond < Condition.Al)
                        {
                            lblPredicateSkip = Label();

                            InstEmitFlowHelper.EmitCondBranch(context, lblPredicateSkip, op.Cond.Invert());
                        }

                        if (opCode.Instruction.Emitter != null)
                        {
                            opCode.Instruction.Emitter(context);

                            if (single_instruction)
                            {
                                context.StoreToContext();

                                context.Return(Const(opCode.Address + 4));

                                break;
                            }

                            if (opCode.Instruction.Name == InstName.Und && blkIndex == 0)
                            {
                                range = new Range(rangeStart, rangeEnd);
                                return null;
                            }
                        }
                        else
                        {
                            throw new InvalidOperationException($"Invalid instruction \"{opCode.Instruction.Name}\".");
                        }

                        if (lblPredicateSkip != default)
                        {
                            context.MarkLabel(lblPredicateSkip);
                        }

                        if (context.IsInIfThenBlock && opCode.Instruction.Name != InstName.It)
                        {
                            context.AdvanceIfThenBlockState();
                        }
                    }
                }
            }

            range = new Range(rangeStart, rangeEnd);

            return context.GetControlFlowGraph();
        }

        internal static void EmitRejitCheck(ArmEmitterContext context, out Counter<uint> counter)
        {
            const int MinsCallForRejit = 100;

            counter = new Counter<uint>(context.CountTable);

            Operand lblEnd = Label();

            Operand address = !context.HasPtc ?
                Const(ref counter.Value) :
                Const(ref counter.Value, Ptc.CountTableSymbol);

            Operand curCount = context.Load(OperandType.I32, address);
            Operand count = context.Add(curCount, Const(1));
            context.Store(address, count);
            context.BranchIf(lblEnd, curCount, Const(MinsCallForRejit), Comparison.NotEqual, BasicBlockFrequency.Cold);

            context.Call(typeof(NativeInterface).GetMethod(nameof(NativeInterface.EnqueueForRejit)), Const(context.EntryAddress));

            context.MarkLabel(lblEnd);
        }

        internal static void EmitSynchronization(EmitterContext context)
        {
            long countOffs = NativeContext.GetCounterOffset();

            Operand lblNonZero = Label();
            Operand lblExit = Label();

            Operand countAddr = context.Add(context.LoadArgument(OperandType.I64, 0), Const(countOffs));
            Operand count = context.Load(OperandType.I32, countAddr);
            context.BranchIfTrue(lblNonZero, count, BasicBlockFrequency.Cold);

            Operand running = context.Call(typeof(NativeInterface).GetMethod(nameof(NativeInterface.CheckSynchronization)));
            context.BranchIfTrue(lblExit, running, BasicBlockFrequency.Cold);

            context.Return(Const(0L));

            context.MarkLabel(lblNonZero);
            count = context.Subtract(count, Const(1));
            context.Store(countAddr, count);

            context.MarkLabel(lblExit);
        }

        public void InvalidateJitCacheRegion(ulong address, ulong size)
        {
            ulong[] overlapAddresses = [];

            int overlapsCount = Functions.GetOverlaps(address, size, ref overlapAddresses);

            if (overlapsCount != 0)
            {
                // If rejit is running, stop it as it may be trying to rejit a function on the invalidated region.
                ClearRejitQueue(allowRequeue: true);
            }

            for (int index = 0; index < overlapsCount; index++)
            {
                ulong overlapAddress = overlapAddresses[index];

                if (Functions.TryGetValue(overlapAddress, out TranslatedFunction overlap))
                {
                    Functions.Remove(overlapAddress);
                    Volatile.Write(ref FunctionTable.GetValue(overlapAddress), FunctionTable.Fill);
                    EnqueueForDeletion(overlapAddress, overlap);
                }
            }

            // TODO: Remove overlapping functions from the JitCache aswell.
            // This should be done safely, with a mechanism to ensure the function is not being executed.
        }

        internal void EnqueueForRejit(ulong guestAddress, ExecutionMode mode)
        {
            Queue.Enqueue(guestAddress, mode);
        }

        private void EnqueueForDeletion(ulong guestAddress, TranslatedFunction func)
        {
            _oldFuncs.Enqueue(new(guestAddress, func));
        }

        private void ClearJitCache()
        {
            // Ensure no attempt will be made to compile new functions due to rejit.
            ClearRejitQueue(allowRequeue: false);

            List<TranslatedFunction> functions = Functions.AsList();

            foreach (TranslatedFunction func in functions)
            {
                JitCache.Unmap(func.FuncPointer);

                func.CallCounter?.Dispose();
            }

            Functions.Clear();

            while (_oldFuncs.TryDequeue(out KeyValuePair<ulong, TranslatedFunction> kv))
            {
                JitCache.Unmap(kv.Value.FuncPointer);

                kv.Value.CallCounter?.Dispose();
            }
        }

        private void ClearRejitQueue(bool allowRequeue)
        {
            if (!allowRequeue)
            {
                Queue.Clear();

                return;
            }

            lock (Queue.Sync)
            {
                while (Queue.Count > 0 && Queue.TryDequeue(out RejitRequest request))
                {
                    if (Functions.TryGetValue(request.Address, out TranslatedFunction func) && func.CallCounter != null)
                    {
                        Volatile.Write(ref func.CallCounter.Value, 0);
                    }
                }
            }
        }
    }
}
