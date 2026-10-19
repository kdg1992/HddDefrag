using HddDefrag.Core;

namespace HddDefrag.Engine;

// PLATZHALTER – wird durch die echte Implementierung ersetzt.
public sealed class DefragEngine : IDefragEngine
{
    public DefragEngine(DriveDescriptor drive, IVolumeAccess volume)
    {
        Drive = drive;
    }

    public DriveDescriptor Drive { get; }
    public NtfsVolumeInfo? VolumeInfo => null;
    public EnginePhase Phase => EnginePhase.Idle;
    public AnalysisResult? LastAnalysis => null;

    public event Action<string>? Log;
    public event Action<EngineProgress>? Progress;
    public event Action? MapChanged;

    public Task<AnalysisResult> AnalyzeAsync(CancellationToken ct) => throw new NotImplementedException();
    public Task DefragmentAsync(DefragOptions options, PauseController pause, CancellationToken ct) => throw new NotImplementedException();
    public ClusterState[] RenderMap(int blockCount) => Array.Empty<ClusterState>();
    public void Dispose() { }
}
