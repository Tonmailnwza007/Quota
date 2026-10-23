using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace costats.Infrastructure.Security;

public static class WindowsCredentialHelper
{
    private const int CRED_TYPE_GENERIC = 1;

    [DllImport("Advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredReadW(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("Advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr credential);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    /// <summary>
    /// Reads a Windows Generic Credential by target name, safely decoding UTF-8 or UTF-16 content.
    /// Does not log secrets or credential contents.
    /// </summary>
    public static string? ReadGenericCredential(string targetName)
    {
        if (string.IsNullOrWhiteSpace(targetName) || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        if (!CredReadW(targetName, CRED_TYPE_GENERIC, 0, out var credPtr) || credPtr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            if (cred.CredentialBlobSize <= 0 || cred.CredentialBlob == IntPtr.Zero)
            {
                return null;
            }

            var blobBytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, blobBytes, 0, cred.CredentialBlobSize);

            // Antigravity CLI credentials are UTF-8 JSON.
            // Copilot and standard Windows credentials may be UTF-16 (Unicode).
            // Check for valid UTF-8 JSON first.
            try
            {
                var utf8String = Encoding.UTF8.GetString(blobBytes);
                // Simple heuristic: if it parses as JSON or starts with '{', UTF-8 is intended.
                if (utf8String.TrimStart().StartsWith('{'))
                {
                    return utf8String;
                }
            }
            catch
            {
                // Fall back to UTF-16
            }

            // UTF-16 fallback
            return Encoding.Unicode.GetString(blobBytes);
        }
        catch
        {
            return null;
        }
        finally
        {
            CredFree(credPtr);
        }
    }
}
