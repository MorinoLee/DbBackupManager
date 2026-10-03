using System.ComponentModel;
using DbBackupManager.Application.FileStorage;

namespace DbBackupManager.Infrastructure.FileStorage;

internal static class SmbNativeFailure
{
    public static FileStorageAdapterException Translate(
        Exception exception,
        BackupFileStorageFailurePhase phase)
    {
        if (exception is FileStorageAdapterException adapter)
        {
            return adapter;
        }

        var code = Map(exception) ?? exception switch
        {
            UnauthorizedAccessException => BackupFileStorageFailureCode.AuthorizationDenied,
            DirectoryNotFoundException => BackupFileStorageFailureCode.PathRejected,
            FileNotFoundException => BackupFileStorageFailureCode.FileNotFound,
            IOException => BackupFileStorageFailureCode.ConnectionFailed,
            _ => BackupFileStorageFailureCode.InvalidResponse,
        };
        return new FileStorageAdapterException(code, phase);
    }

    private static BackupFileStorageFailureCode? Map(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is Win32Exception win32)
            {
                var mapped = MapWin32(NormalizeWin32(win32.NativeErrorCode));
                if (mapped is not null)
                {
                    return mapped;
                }
            }

            var mappedHresult = MapHresult(unchecked((uint)current.HResult));
            if (mappedHresult is not null)
            {
                return mappedHresult;
            }
        }

        return null;
    }

    private static BackupFileStorageFailureCode? MapHresult(uint hresult)
    {
        if (hresult == 0x8009030Cu)
        {
            return BackupFileStorageFailureCode.AuthenticationFailed;
        }

        if ((hresult & 0xFFFF0000u) == 0x80070000u)
        {
            return MapWin32((int)(hresult & 0xFFFFu));
        }

        var status = hresult;
        if ((status & 0xF0000000u) == 0xD0000000u)
        {
            status &= ~0x10000000u;
        }

        return (status & 0xF0000000u) == 0xC0000000u
            ? MapNtStatus(status)
            : null;
    }

    private static int NormalizeWin32(int code)
    {
        var unsigned = unchecked((uint)code);
        return (unsigned & 0xFFFF0000u) == 0x80070000u
            ? (int)(unsigned & 0xFFFFu)
            : code;
    }

    private static BackupFileStorageFailureCode? MapWin32(int native) => native switch
    {
        86 or 1244 or 1245 or 1265 or 1312 or 1317 or 1326 or 1327 or 1328 or 1329
            or 1330 or 1331 or 1332 or 1385 or 1396 or 1397 or 1788 or 1789 or 1790
            or 1793 or 1907 or 1909 or 2202 or 1219 =>
            BackupFileStorageFailureCode.AuthenticationFailed,
        5 or 19 or 21 or 32 or 33 or 65 => BackupFileStorageFailureCode.AuthorizationDenied,
        2 => BackupFileStorageFailureCode.FileNotFound,
        3 or 161 => BackupFileStorageFailureCode.PathRejected,
        51 or 53 or 59 or 64 or 67 or 1231 or 1232 or 1238 =>
            BackupFileStorageFailureCode.ConnectionFailed,
        _ => null,
    };

    private static BackupFileStorageFailureCode? MapNtStatus(uint status) => status switch
    {
        0xC0000064 or 0xC000006A or 0xC000006D or 0xC000006E or 0xC000006F
            or 0xC0000070 or 0xC0000071 or 0xC0000072 or 0xC000015B or 0xC0000192
            or 0xC0000193 or 0xC0000224 or 0xC0000234 or 0xC0000388 or 0xC000018D
            or 0xC0000190 =>
            BackupFileStorageFailureCode.AuthenticationFailed,
        0xC0000022 or 0xC00000CA => BackupFileStorageFailureCode.AuthorizationDenied,
        0xC00000BE or 0xC00000CC or 0xC000005E =>
            BackupFileStorageFailureCode.ConnectionFailed,
        _ => null,
    };
}
