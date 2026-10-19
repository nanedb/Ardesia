using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Ardesia.Services.Google;

/// <summary>Account salvato su questo PC: refresh token e dati da mostrare.</summary>
public sealed class StoredAccount
{
    public string RefreshToken { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Name { get; set; }
    public string? HostedDomain { get; set; }
    public string Scope { get; set; } = "";
    /// <summary>Quando è stato fatto l'accesso (ora locale).</summary>
    public DateTime SignedInAt { get; set; }
}

/// <summary>
/// Conserva l'account Google in %LocalAppData%\Ardesia\google-account.bin, cifrato con DPAPI (ambito utente
/// corrente + entropia fissa di Ardesia): solo lo stesso utente Windows sullo stesso PC può leggerlo.
/// Local e non Roaming: il gettone non deve viaggiare con il profilo. DPAPI è chiamata direttamente in
/// crypt32.dll (niente pacchetti NuGet).
/// </summary>
public static class TokenStore
{
    public static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ardesia", "google-account.bin");

    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Ardesia · account Google · 2026");

    /// <summary>L'account salvato, o null se non c'è o non si può decifrare (altro utente, file rovinato).</summary>
    public static StoredAccount? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var json = Unprotect(File.ReadAllBytes(FilePath));
            var a = JsonSerializer.Deserialize<StoredAccount>(json);
            return a is { RefreshToken.Length: > 0, Email.Length: > 0 } ? a : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Salva (cifrato) con scrittura atomica: file temporaneo e poi sostituzione.</summary>
    public static void Save(StoredAccount account)
    {
        var data = Protect(JsonSerializer.SerializeToUtf8Bytes(account));
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, FilePath, overwrite: true);
    }

    public static void Delete()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch
        {
            // già cancellato o bloccato: alla prossima lettura non si decifra comunque senza refresh token valido
        }
    }

    // ───────────── DPAPI (crypt32.dll) ─────────────

    const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);

    public static byte[] Protect(byte[] plain) => Run(plain, protect: true);

    public static byte[] Unprotect(byte[] cipher) => Run(cipher, protect: false);

    static byte[] Run(byte[] input, bool protect)
    {
        var inBlob = ToBlob(input);
        var entropy = ToBlob(Entropy);
        var outBlob = default(DataBlob);
        try
        {
            bool ok = protect
                ? CryptProtectData(ref inBlob, "Ardesia", ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[outBlob.Size];
            Marshal.Copy(outBlob.Data, result, 0, outBlob.Size);
            return result;
        }
        finally
        {
            if (inBlob.Data != IntPtr.Zero)
            {
                Marshal.Copy(new byte[inBlob.Size], 0, inBlob.Data, inBlob.Size); // non lasciare il testo in chiaro in memoria
                Marshal.FreeHGlobal(inBlob.Data);
            }
            if (entropy.Data != IntPtr.Zero) Marshal.FreeHGlobal(entropy.Data);
            if (outBlob.Data != IntPtr.Zero) LocalFree(outBlob.Data);
        }
    }

    static DataBlob ToBlob(byte[] bytes)
    {
        var p = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
        Marshal.Copy(bytes, 0, p, bytes.Length);
        return new DataBlob { Size = bytes.Length, Data = p };
    }
}
