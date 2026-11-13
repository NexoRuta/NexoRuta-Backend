namespace NexoRuta.Worker;

public sealed record ProofDeadlineSweepResult(string Status, int Affected, bool MoreWork);
