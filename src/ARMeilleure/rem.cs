using System.Runtime.InteropServices;

public static unsafe class rem_imports
{
    public struct aarch64_context_offsets
    {
        public int x_offset;
        public int q_offset;

        public int n_offset;
        public int z_offset;
        public int c_offset;
        public int v_offset;

        public int exclusive_address_offset;
        public int exclusive_value_offset;

        public int fpcr_offset;
        public int fpsr_offset;

        public int thread_local_0;
        public int thread_local_1;

        public int context_size;
    };

    const string path = "/media/linvirt/partish/rem/build/librem.so";

    [DllImport(path)]
    public static extern void* create_rem_context(void* memory, aarch64_context_offsets* context_offsets, void* svc, void* get_counter, void* undefined_instruction);

    [DllImport(path)]
    public static extern void destroy_rem_context(void* context);

    [DllImport(path)]
    public static extern ulong interperate_until_long_jump(void* context, ulong virtual_address, void* guest_context, bool* interperate_until_long_jump);
    
    [DllImport(path)]
    public static extern ulong jit_until_long_jump(void* context, ulong virtual_address, void* guest_context, bool* is_running, void* log_native);

    [DllImport(path)]
    public static extern void invalidate_jit_region(void* context, ulong virtual_address, ulong size);
}