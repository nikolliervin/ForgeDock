using ForgeDock.Domain;
namespace ForgeDock.Infrastructure;

public enum BackupRetentionAction { Keep, RemoveLocal, Expire }
public static class BackupRetention
{
    public static BackupRetentionAction Decide(DatabaseBackup backup, int position, int retained, int localRetained, bool remoteEnabled, bool restoring)
    {
        if (restoring || backup.RemoteState is "Pending" or "Failed" || (backup.RemoteState == "Uploaded" && !remoteEnabled)) return BackupRetentionAction.Keep;
        if (position >= retained) return BackupRetentionAction.Expire;
        return position >= localRetained && backup.RemoteState == "Uploaded" ? BackupRetentionAction.RemoveLocal : BackupRetentionAction.Keep;
    }
}
