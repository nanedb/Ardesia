using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Ardesia.Services;

/// <summary>
/// Ardesia si apre una volta sola per utente: un secondo avvio (doppio clic sull'icona o su un file .ardesia) passa il
/// file all'Ardesia già aperta, che viene in primo piano, e si chiude. Due Ardesia sullo stesso blocco su Google Drive
/// si comporterebbero come due PC e creerebbero una copia da conflitto.
/// </summary>
public static class SingleInstance
{
    static readonly string Name = $"Ardesia-{Process.GetCurrentProcess().SessionId}-{Environment.UserName}";
    static Mutex? _mutex;

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>
    /// Vero se questa è la prima Ardesia: allora <paramref name="onRequest"/> riceverà (sul thread della UI, tramite il
    /// chiamante) i file degli avvii successivi, o "" per «vieni in primo piano». Falso se un'altra Ardesia è già aperta:
    /// il file le è stato passato e questa deve chiudersi.
    /// </summary>
    public static bool TryClaim(string? file, Action<string> onRequest)
    {
        // Prove dal vivo con un «secondo PC» sullo stesso computer (docs/google-drive.md): niente controllo.
        if (Environment.GetEnvironmentVariable("ARDESIA_PROVA_CACHE") is { Length: > 0 }) return true;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var mutex = new Mutex(true, @"Local\" + Name, out bool created);
            if (created)
            {
                _mutex = mutex; // resta acquisito finché Ardesia è aperta
                Listen(onRequest);
                return true;
            }
            mutex.Dispose();
            if (Send(file)) return false;
            // l'altra Ardesia si sta chiudendo (o non risponde): si aspetta un attimo e si riprova
            Thread.Sleep(1500);
        }
        return false;
    }

    /// <summary>Manda il file (o "") all'Ardesia già aperta e le permette di venire in primo piano.</summary>
    static bool Send(string? file)
    {
        try
        {
            AllowSetForegroundWindow(-1); // ASFW_ANY: questo avvio l'ha chiesto l'utente, il permesso passa all'altra
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.Out);
            pipe.Connect(2000);
            var data = Encoding.UTF8.GetBytes(file != null ? Path.GetFullPath(file) : "");
            pipe.Write(data);
            pipe.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    static void Listen(Action<string> onRequest)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(Name, PipeDirection.In, 1);
                    pipe.WaitForConnection();
                    using var ms = new MemoryStream();
                    pipe.CopyTo(ms);
                    onRequest(Encoding.UTF8.GetString(ms.ToArray()));
                }
                catch
                {
                    Thread.Sleep(500); // pipe interrotta: si torna in ascolto
                }
            }
        }) { IsBackground = true, Name = "Ardesia: apertura unica" };
        thread.Start();
    }
}
