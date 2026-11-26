namespace MangaCli;

/// <summary>
/// Fixed MangaCli exit codes. <see cref="StopUnverified"/> always wins over the others:
/// an unverified stop means the cloud runtime may still be billing.
/// </summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int UnexpectedError = 1;
    public const int Usage = 2;
    public const int Environment = 3;
    public const int Locked = 4;
    public const int DeployFailed = 5;
    public const int TaskFailed = 6;
    public const int StopUnverified = 7;
    public const int OrphanRefused = 8;
    public const int ProbeFailed = 9;
    public const int Interrupted = 130;
}
