namespace BBC
{
    /// <summary>The Model B can be fitted with either the original 8271 or Acorn's later 1770 upgrade.</summary>
    public interface IDiscController
    {
        bool HasMountedDisc { get; }
        string? MountedFileName { get; }
        bool ImageDirty { get; }
        bool MountedMediaIsAdfs { get; }
        string MountedDriveSummary { get; }
        bool NmiLineAsserted { get; }
        bool TickRequired { get; }
        event Action<int>? DriveMotorStarted;
        event Action<int>? DriveMotorStopped;
        event Action<int, int>? DriveSeek;
        void Mount(string path, int drive = 0);
        void MountImage(byte[] image, int drive, string? sourcePath, string displayName, bool readOnly);
        void EjectPhysicalDrive(int drive);
        bool Flush();
        void Reset();
        void PowerOff();
        void Tick(int cycles);
        byte Read(ushort address);
        void Write(ushort address, byte value);
        bool IsPhysicalDriveMounted(int drive);
        bool IsPhysicalDriveActivityLedActive(int drive);
        bool IsPhysicalDriveDoubleSided(int drive);
        string? GetPhysicalDriveLabel(int drive);
        bool TryGetBootExecScript(out string? script);
        void SaveState(BinaryWriter writer);
        void LoadState(BinaryReader reader);
        void SaveMediaState(BinaryWriter writer);
        void LoadMediaState(BinaryReader reader);
    }

    public enum DiscInterface
    {
        Intel8271,
        Wd1770
    }
}
