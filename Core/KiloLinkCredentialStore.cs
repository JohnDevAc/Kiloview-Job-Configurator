using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;

namespace KiloviewSetup.Core;

public sealed record KiloLinkCredential(string Username, string Password);
public sealed record KiloLinkCredentialStatus(bool Stored, string? Username);

/// <summary>
/// Stores KiloLink credentials in the current Windows user's Credential Manager vault.
/// Passwords never enter application state.json and cannot be read through the web API.
/// </summary>
public sealed class KiloLinkCredentialStore
{
    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;
    private const int NotFound = 1168;

    public KiloLinkCredentialStatus GetStatus(string serverIp)
    {
        InputValidation.Ip(serverIp, "KiloLink Server IP");
        var credential = Read(serverIp);
        return new(credential is not null, credential?.Username);
    }

    public KiloLinkCredential ResolveAndStore(string serverIp, string? username, string? password)
    {
        InputValidation.Ip(serverIp, "KiloLink Server IP");
        username = username?.Trim() ?? "";
        password ??= "";
        var stored = Read(serverIp);

        if (!string.IsNullOrEmpty(password))
        {
            if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("KiloLink server username is required when entering a new password.");
            var credential = new KiloLinkCredential(username, password);
            Write(serverIp, credential);
            return credential;
        }

        if (stored is null) throw new ArgumentException("KiloLink server username and password are required. No stored credentials exist for this server IP.");
        if (!string.IsNullOrWhiteSpace(username) && !string.Equals(username, stored.Username, StringComparison.Ordinal))
            throw new ArgumentException("Enter the password for the new KiloLink username, or use the stored username shown by the application.");
        return stored;
    }

    private static string Target(string serverIp) => $"KiloviewSetup/KiloLink/{serverIp}";

    private static void Write(string serverIp, KiloLinkCredential value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("KiloLink credential storage requires Windows Credential Manager.");
        var passwordBytes = Encoding.Unicode.GetBytes(value.Password);
        if (passwordBytes.Length > 2560) throw new ArgumentException("KiloLink password is too long for Windows Credential Manager.");
        var blob = Marshal.AllocHGlobal(passwordBytes.Length);
        try
        {
            Marshal.Copy(passwordBytes, 0, blob, passwordBytes.Length);
            var native = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = Target(serverIp),
                CredentialBlobSize = (uint)passwordBytes.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = value.Username,
                Comment = "Kiloview Setup KiloLink server credential"
            };
            if (!CredWrite(ref native, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows Credential Manager could not store the KiloLink credential.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            if (blob != IntPtr.Zero) Marshal.FreeHGlobal(blob);
        }
    }

    private static KiloLinkCredential? Read(string serverIp)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("KiloLink credential storage requires Windows Credential Manager.");
        if (!CredRead(Target(serverIp), GenericCredential, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == NotFound) return null;
            throw new Win32Exception(error, "Windows Credential Manager could not read the KiloLink credential.");
        }
        try
        {
            var native = Marshal.PtrToStructure<NativeCredential>(pointer);
            var bytes = new byte[native.CredentialBlobSize];
            if (bytes.Length > 0) Marshal.Copy(native.CredentialBlob, bytes, 0, bytes.Length);
            try { return new(native.UserName ?? "", Encoding.Unicode.GetString(bytes).TrimEnd('\0')); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("Advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr buffer);
}
