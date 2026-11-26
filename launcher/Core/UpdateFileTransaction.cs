using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XC2.Core;

/// <summary>Stages updates beside their destinations and recovers an interrupted multi-file replacement.</summary>
sealed class UpdateFileTransaction : IDisposable
{
    sealed record Replacement(string Destination, bool HadOriginal, bool Launcher);
    sealed record Journal(string Id, string Phase, Replacement[] Files, string[]? NewDirectories = null);

    readonly string journalPath;
    readonly Mutex mutex;
    readonly List<(Replacement File, Action<string> Stage)> pending = [];

    public UpdateFileTransaction(string projectRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        journalPath = Path.Combine(root, ".xc2-update.json");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
        mutex = new Mutex(false, $"XC2PCEdition.Update.{key}");
        bool acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Outra atualização está em andamento nesta instalação.");
            Recover();
        }
        catch
        {
            if (acquired) mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }

    public void Copy(string source, string destination, bool launcher = false) =>
        Add(destination, path => File.Copy(source, path), launcher);

    public void WriteHints(string destination, List<string> lines) =>
        Add(destination, path => File.WriteAllLines(path, lines, new UTF8Encoding(false)));

    void Add(string destination, Action<string> stage, bool launcher = false)
    {
        var path = Path.GetFullPath(destination);
        if (pending.Any(p => p.File.Destination.Equals(path, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"O pacote contém destinos de atualização duplicados: {path}");
        pending.Add((new Replacement(path, File.Exists(path), launcher), stage));
    }

    public void Commit(CancellationToken ct = default)
    {
        if (pending.Count == 0) return;
        ct.ThrowIfCancellationRequested();
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, _) in pending)
            for (var dir = Path.GetDirectoryName(file.Destination); dir != null && !Directory.Exists(dir); dir = Path.GetDirectoryName(dir))
                directories.Add(dir);
        var record = new Journal(Guid.NewGuid().ToString("N"), "staging", pending.Select(p => p.File).ToArray(),
            directories.OrderByDescending(d => d.Length).ToArray());
        Save(record);
        try
        {
            foreach (var (file, stage) in pending)
            {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(file.Destination)!);
                stage(Sibling(record, file, "new"));
                if (file.Launcher && File.Exists(Backup(record, file)))
                    File.Delete(Backup(record, file));
            }

            if (GameRunner.IsGameRunning())
                throw new InvalidOperationException("Feche o jogo antes de instalar a atualização.");
            Save(record with { Phase = "applying" });
            foreach (var file in record.Files)
            {
                ct.ThrowIfCancellationRequested();
                if (file.HadOriginal) File.Move(file.Destination, Backup(record, file));
                File.Move(Sibling(record, file, "new"), file.Destination);
            }
            Save(record with { Phase = "committed" });
        }
        catch (Exception applyError)
        {
            try { Recover(); }
            catch (Exception recoveryError)
            {
                throw new IOException($"A atualização falhou. A recuperação está pendente em {journalPath}.",
                    new AggregateException(applyError, recoveryError));
            }
            throw;
        }

        // The commit is durable. Keep the journal if cleaning up fails so the next launch can retry.
        try { Recover(); }
        catch (IOException ex) { LauncherLog.Write($"update cleanup pending: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { LauncherLog.Write($"update cleanup pending: {ex.Message}"); }
    }

    void Save(Journal record)
    {
        var temp = journalPath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, record);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, journalPath, overwrite: true);
    }

    void Recover()
    {
        if (!File.Exists(journalPath)) return;
        var record = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(journalPath));
        if (record is null || !Guid.TryParseExact(record.Id, "N", out _) ||
            record.Phase is not ("staging" or "applying" or "committed"))
            throw new InvalidDataException($"Registro de atualização inválido: {journalPath}");
        if (record.Phase == "applying" && GameRunner.IsGameRunning())
            throw new IOException("Feche o jogo antes de recuperar a atualização interrompida.");

        foreach (var file in record.Files.Reverse())
        {
            var staged = Sibling(record, file, "new");
            var backup = Backup(record, file);
            if (record.Phase == "applying")
            {
                if (File.Exists(backup))
                {
                    if (File.Exists(file.Destination)) File.Move(file.Destination, staged);
                    File.Move(backup, file.Destination);
                }
                else if (!file.HadOriginal && !File.Exists(staged))
                {
                    File.Delete(file.Destination);
                }
            }
            else if (record.Phase == "committed" && !file.Launcher)
            {
                File.Delete(backup);
            }
            if (File.Exists(staged)) File.Delete(staged);
        }
        if (record.Phase != "committed" && record.NewDirectories is not null)
        {
            foreach (var dir in record.NewDirectories)
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
        }
        File.Delete(journalPath + ".tmp");
        File.Delete(journalPath);
    }

    static string Sibling(Journal record, Replacement file, string extension) =>
        $"{file.Destination}.xc2-{record.Id}.{extension}";

    static string Backup(Journal record, Replacement file) =>
        file.Launcher ? file.Destination + ".old" : Sibling(record, file, "bak");

    public void Dispose()
    {
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
