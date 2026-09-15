using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Tessera.Services;

public interface ICredentialVault
{
    string Name { get; }
    Task<string?> ReadAsync(string key, CancellationToken token = default);
    Task WriteAsync(string key, string secret, CancellationToken token = default);
    Task DeleteAsync(string key, CancellationToken token = default);
}

/// <summary>Native user credential storage. No file-based or plaintext fallback.</summary>
public sealed class OsCredentialVault : ICredentialVault
{
    public string Name => OperatingSystem.IsWindows() ? "Windows Credential Manager" :
        OperatingSystem.IsMacOS() ? "macOS Keychain" : "Freedesktop Secret Service";

    public Task<string?> ReadAsync(string key, CancellationToken token = default) => ExecuteAsync(key, null, false, token);
    public async Task WriteAsync(string key, string secret, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (Encoding.UTF8.GetByteCount(secret) > 2400) throw new ArgumentException("Vault entries are limited to 2,400 UTF-8 bytes.", nameof(secret));
        await ExecuteAsync(key, secret, false, token);
    }
    public async Task DeleteAsync(string key, CancellationToken token = default) => await ExecuteAsync(key, null, true, token);

    private static async Task<string?> ExecuteAsync(string key, string? value, bool delete, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 512 || key.Any(char.IsControl)) throw new ArgumentException("Invalid secret identifier.", nameof(key));
        token.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows()) return await Task.Run(() => Windows(key, value, delete), token);
        if (OperatingSystem.IsMacOS()) return await Task.Run(() => Mac(key, value, delete), token);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("No OS credential vault is available on this platform.");
        return await LinuxAsync(key, value, delete, token);
    }

    private static async Task<string?> LinuxAsync(string key, string? value, bool delete, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var info = new ProcessStartInfo("secret-tool") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(delete ? "clear" : value is not null ? "store" : "lookup");
        if (value is not null) info.ArgumentList.Add("--label=Tessera credential");
        info.ArgumentList.Add("application"); info.ArgumentList.Add("Tessera");
        info.ArgumentList.Add("identifier"); info.ArgumentList.Add(key);
        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (Win32Exception ex) { throw new InvalidOperationException("Install libsecret-tools and unlock a Secret Service collection. Tessera will not save plaintext secrets.", ex); }
        using var stop = deadline.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        if (value is not null) await process.StandardInput.WriteAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).AsMemory(), deadline.Token);
        process.StandardInput.Close();
        await process.WaitForExitAsync(deadline.Token);
        var result = await output; var failure = await error;
        if (process.ExitCode != 0)
        {
            if (value is null && process.ExitCode == 1 && string.IsNullOrWhiteSpace(failure)) return null;
            throw new InvalidOperationException("Secret Service is unavailable, locked, or denied access. No credential was saved to disk.");
        }
        if(value is not null || delete)return null;
        var decoded=Convert.FromBase64String(result.TrimEnd('\r','\n'));
        try {return Encoding.UTF8.GetString(decoded);}finally {CryptographicOperations.ZeroMemory(decoded);}
    }

    private static string? Windows(string key, string? value, bool delete)
    {
        const int Generic = 1;
        var target = "Tessera/" + key;
        if (delete)
        {
            if (!CredDelete(target, Generic, 0) && Marshal.GetLastWin32Error() != 1168) throw new Win32Exception();
            return null;
        }
        if (value is null)
        {
            if (!CredRead(target, Generic, 0, out var pointer))
            {
                if (Marshal.GetLastWin32Error() == 1168) return null;
                throw new Win32Exception();
            }
            try
            {
                var credential = Marshal.PtrToStructure<Credential>(pointer);
                var bytes = new byte[credential.BlobSize];
                try { Marshal.Copy(credential.Blob, bytes, 0, bytes.Length); return Encoding.UTF8.GetString(bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            finally { CredFree(pointer); }
        }
        var data = Encoding.UTF8.GetBytes(value);
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var credential = new Credential { Type = Generic, Target = target, BlobSize = data.Length,
                Blob = handle.AddrOfPinnedObject(), Persist = 2, UserName = Environment.UserName };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception();
        }
        finally { handle.Free(); CryptographicOperations.ZeroMemory(data); }
        return null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags; public int Type; public string Target; public string? Comment;
        public long LastWritten; public int BlobSize; public IntPtr Blob; public int Persist;
        public int AttributeCount; public IntPtr Attributes; public string? TargetAlias; public string? UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, int flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, int type, int flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr buffer);

    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly Lazy<IntPtr> SecurityHandle = new(() => NativeLibrary.Load(Security));
    private static readonly Lazy<IntPtr> CfHandle = new(() => NativeLibrary.Load(CoreFoundation));
    private static IntPtr Sec(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(SecurityHandle.Value, name));
    private static IntPtr Cf(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(CfHandle.Value, name));
    private static IntPtr Dictionary() => CFDictionaryCreateMutable(IntPtr.Zero, 0,
        NativeLibrary.GetExport(CfHandle.Value, "kCFTypeDictionaryKeyCallBacks"),
        NativeLibrary.GetExport(CfHandle.Value, "kCFTypeDictionaryValueCallBacks"));
    private static void StringAttribute(IntPtr dictionary, string key, string value)
    {
        var text = CFStringCreateWithCString(IntPtr.Zero, value, 0x08000100);
        if (text == IntPtr.Zero) throw new InvalidOperationException("Keychain string allocation failed.");
        try { CFDictionarySetValue(dictionary, Sec(key), text); } finally { CFRelease(text); }
    }
    private static string? Mac(string key, string? value, bool delete)
    {
        var query = Dictionary();
        try
        {
            CFDictionarySetValue(query, Sec("kSecClass"), Sec("kSecClassGenericPassword"));
            StringAttribute(query, "kSecAttrService", "Tessera");
            StringAttribute(query, "kSecAttrAccount", key);
            if (delete) { CheckMac(SecItemDelete(query), allowMissing: true); return null; }
            if (value is null)
            {
                CFDictionarySetValue(query, Sec("kSecReturnData"), Cf("kCFBooleanTrue"));
                CFDictionarySetValue(query, Sec("kSecMatchLimit"), Sec("kSecMatchLimitOne"));
                var status = SecItemCopyMatching(query, out var result);
                if (status == -25300) return null;
                CheckMac(status);
                try
                {
                    var length = CFDataGetLength(result);
                    if (length < 0 || length > 8192) throw new InvalidDataException("Invalid Keychain entry.");
                    var bytes = new byte[(int)length];
                    try { Marshal.Copy(CFDataGetBytePtr(result), bytes, 0, bytes.Length); return Encoding.UTF8.GetString(bytes); }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
                finally { CFRelease(result); }
            }
            var data = Encoding.UTF8.GetBytes(value);
            try
            {
                var cfData = CFDataCreate(IntPtr.Zero, data, data.Length);
                var attributes = Dictionary();
                try
                {
                    CFDictionarySetValue(attributes, Sec("kSecValueData"), cfData);
                    var status = SecItemUpdate(query, attributes);
                    if (status == -25300)
                    {
                        CFDictionarySetValue(query, Sec("kSecValueData"), cfData);
                        status = SecItemAdd(query, IntPtr.Zero);
                    }
                    CheckMac(status);
                }
                finally { CFRelease(attributes); CFRelease(cfData); }
            }
            finally { CryptographicOperations.ZeroMemory(data); }
            return null;
        }
        finally { CFRelease(query); }
    }
    private static void CheckMac(int status, bool allowMissing = false)
    {
        if (status != 0 && !(allowMissing && status == -25300)) throw new InvalidOperationException($"Keychain operation failed (OSStatus {status}). No plaintext fallback was used.");
    }
    [DllImport(Security)] private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
    [DllImport(Security)] private static extern int SecItemAdd(IntPtr attributes, IntPtr result);
    [DllImport(Security)] private static extern int SecItemUpdate(IntPtr query, IntPtr attributes);
    [DllImport(Security)] private static extern int SecItemDelete(IntPtr query);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity, IntPtr keys, IntPtr values);
    [DllImport(CoreFoundation)] private static extern void CFDictionarySetValue(IntPtr dictionary, IntPtr key, IntPtr value);
    [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);
    [DllImport(CoreFoundation)] private static extern nint CFDataGetLength(IntPtr data);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDataGetBytePtr(IntPtr data);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr value);
}
