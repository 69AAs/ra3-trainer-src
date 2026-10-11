using Ra3Trainer.Core.Memory;

namespace Ra3Trainer.Core.Runtime;

/// <summary>Implemented by a live suspension scope; validation and free must occur before resume.</summary>
public interface IRemoteCodeQuiescence
{
    void EnsureUnreferenced(IProcessMemory memory, ulong address, ulong size);
}
