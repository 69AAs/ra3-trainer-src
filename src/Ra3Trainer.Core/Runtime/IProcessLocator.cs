namespace Ra3Trainer.Core.Runtime;

public interface IProcessLocator
{
    bool AllowNonStandardVersion { get; set; }
    IReadOnlyList<TrainerProcessCandidate> Snapshot();
    TrainerTarget? Find(string processNameOrPath);
}
