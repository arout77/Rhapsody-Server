using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace RhapsodyServer.Core.Ai;

/// <summary>Encrypts a secret so only the same user on the same machine can read it back.</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plain);
    byte[] Unprotect(byte[] protectedBytes);
}

/// <summary>
/// Windows DPAPI (current user), called directly so the app needs no extra package.
/// This is the same protection Chrome and Windows Credential Manager rely on: the ciphertext is useless on another
/// computer or under another Windows account.
/// </summary>
public sealed class DpapiProtector : ISecretProtector
{
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RhapsodyServer.AiKeys.v1");
    const int UiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    struct DataBlob { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);

    delegate bool Op(ref DataBlob input, ref DataBlob entropy, out DataBlob output);

    static byte[] Run(byte[] data, Op op)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Saving API keys needs Windows.");
        var inPtr = Marshal.AllocHGlobal(Math.Max(data.Length, 1));
        var entPtr = Marshal.AllocHGlobal(Entropy.Length);
        try
        {
            Marshal.Copy(data, 0, inPtr, data.Length);
            Marshal.Copy(Entropy, 0, entPtr, Entropy.Length);
            var input = new DataBlob { cbData = data.Length, pbData = inPtr };
            var ent = new DataBlob { cbData = Entropy.Length, pbData = entPtr };
            if (!op(ref input, ref ent, out var output))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally { LocalFree(output.pbData); }
        }
        finally { Marshal.FreeHGlobal(inPtr); Marshal.FreeHGlobal(entPtr); }
    }

    public byte[] Protect(byte[] plain) => Run(plain, (ref DataBlob i, ref DataBlob e, out DataBlob o) =>
        CryptProtectData(ref i, "Rhapsody Server AI key", ref e, IntPtr.Zero, IntPtr.Zero, UiForbidden, out o));

    public byte[] Unprotect(byte[] protectedBytes) => Run(protectedBytes, (ref DataBlob i, ref DataBlob e, out DataBlob o) =>
        CryptUnprotectData(ref i, IntPtr.Zero, ref e, IntPtr.Zero, IntPtr.Zero, UiForbidden, out o));
}

/// <summary>
/// API keys, kept apart from settings.json (which people copy, diff and paste into bug reports) and encrypted.
/// Each key is stored together with the endpoint it was issued for and is only ever handed back for that same
/// endpoint, so editing a provider's URL can never send one company's key to another company's server.
/// </summary>
public sealed class AiKeyStore
{
    readonly string _path;
    readonly ISecretProtector _protector;
    readonly object _lock = new();
    sealed record Entry(string Endpoint, string Data);
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public AiKeyStore(string path, ISecretProtector? protector = null)
    {
        _path = path;
        _protector = protector ?? new DpapiProtector();
    }

    Dictionary<string, Entry> Read()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path), Opts) ?? new()
                : new();
        }
        catch (JsonException) { return new(); }   // a damaged file just means "no keys saved"
    }

    void Write(Dictionary<string, Entry> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all, Opts));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>The key for this slot, or null if none is saved for exactly this endpoint (or it can't be decrypted here).</summary>
    public string? Get(string slot, string endpoint)
    {
        lock (_lock)
        {
            if (!Read().TryGetValue(slot, out var e) || !AiUrl.Same(e.Endpoint, endpoint)) return null;
            try { return Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(e.Data))); }
            catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException
                or System.ComponentModel.Win32Exception or PlatformNotSupportedException) { return null; }
        }
    }

    public bool Has(string slot, string endpoint) => Get(slot, endpoint) != null;

    public void Set(string slot, string endpoint, string key)
    {
        lock (_lock)
        {
            var all = Read();
            all[slot] = new Entry(AiUrl.Normalize(endpoint), Convert.ToBase64String(_protector.Protect(Encoding.UTF8.GetBytes(key))));
            Write(all);
        }
    }

    public void Remove(string slot)
    {
        lock (_lock) { var all = Read(); if (all.Remove(slot)) Write(all); }
    }

    /// <summary>Forgets the slot's key when the person pointed the slot at a different endpoint.</summary>
    public void DropIfEndpointDiffers(string slot, string endpoint)
    {
        lock (_lock)
        {
            var all = Read();
            if (all.TryGetValue(slot, out var e) && !AiUrl.Same(e.Endpoint, endpoint)) { all.Remove(slot); Write(all); }
        }
    }

    /// <summary>Every saved key in the clear, so the redactor can scrub them from anything that is sent out.</summary>
    public IReadOnlyList<string> AllKeys()
    {
        lock (_lock)
        {
            var list = new List<string>();
            foreach (var e in Read().Values)
            {
                try { list.Add(Encoding.UTF8.GetString(_protector.Unprotect(Convert.FromBase64String(e.Data)))); }
                catch { /* unreadable here: nothing to scrub */ }
            }
            return list;
        }
    }
}
