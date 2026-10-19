using HddDefrag.Core;

namespace HddDefrag.Native;

// PLATZHALTER – wird durch die echte Implementierung ersetzt.
public sealed class VolumeAccess : IVolumeAccess
{
    public static VolumeAccess Open(char letter) => throw new VolumeAccessException("Noch nicht implementiert.");

    public char Letter => throw new NotImplementedException();
    public NtfsVolumeInfo Info => throw new NotImplementedException();
    public VolumeBitmap ReadBitmap() => throw new NotImplementedException();
    public IReadOnlyList<Extent> GetExtents(string fullPath) => throw new NotImplementedException();
    public void MoveFile(string fullPath, long startVcn, long targetLcn, long clusterCount) => throw new NotImplementedException();
    public void Dispose() { }
}
