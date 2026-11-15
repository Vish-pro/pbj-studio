using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using KcsPdfSigner.Core;

namespace KcsPdfSigner.App.Documents;

/// <summary>
/// One open PDF. The original file is never touched until Save: every change is made by a Core
/// operation that reads the current working file and writes a brand-new file in a private temp
/// folder (%TEMP%\KcsPdfStudio\&lt;guid&gt;\). Undo and redo just move between those files.
/// </summary>
/// <remarks>
/// Tool panels change a document only through <see cref="Apply"/> or <see cref="ApplyAsync"/>, so
/// every change is undoable. Example (Phase C):
/// <code>
/// session.Apply((input, output) => PdfPageOps.RotatePages(input, output, pages, 90), "Rotate pages");
/// </code>
/// The operation must write <c>output</c> and must not modify <c>input</c>.
/// </remarks>
public sealed class DocumentSession : INotifyPropertyChanged, IDisposable
{
    private sealed record Version(string File, string? Password, string Description);

    private readonly Stack<Version> _undo = new();
    private readonly Stack<Version> _redo = new();
    private Version _current;
    private Version _saved;
    private int _fileCounter;
    private bool _disposed;

    private DocumentSession(string originalPath, string workingFolder, Version first, List<PdfPageInfo> pages)
    {
        OriginalPath = originalPath;
        WorkingFolder = workingFolder;
        _current = first;
        _saved = first;
        Pages = pages;
    }

    /// <summary>Root of every session's temp folder.</summary>
    public static string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "KcsPdfStudio");

    /// <summary>
    /// Copies <paramref name="path"/> into a fresh working folder and reads its pages.
    /// Throws <see cref="PasswordRequiredException"/> when the file is encrypted and
    /// <paramref name="password"/> is missing or wrong; ask the user and call again.
    /// </summary>
    public static DocumentSession Open(string path, string? password = null)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The file does not exist.", fullPath);

        var folder = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            var working = Path.Combine(folder, "000.pdf");
            File.Copy(fullPath, working, overwrite: true);
            File.SetAttributes(working, FileAttributes.Normal);

            var pages = PdfPageReader.ReadPages(working, password);
            var first = new Version(working, string.IsNullOrEmpty(password) ? null : password, "Open");
            return new DocumentSession(fullPath, folder, first, pages);
        }
        catch
        {
            TryDeleteFolder(folder);
            throw;
        }
    }

    // ---- State -------------------------------------------------------------

    /// <summary>Where the document lives on disk; Save writes here. Changes after Save As.</summary>
    public string OriginalPath { get; private set; }

    /// <summary>Private temp folder holding every version of this document.</summary>
    public string WorkingFolder { get; }

    /// <summary>The working file that reflects every applied change. Read it; never write it.</summary>
    public string CurrentFile => _current.File;

    /// <summary>Password needed to open <see cref="CurrentFile"/>; null when not encrypted.</summary>
    public string? Password => _current.Password;

    public bool IsEncrypted => _current.Password is not null;

    public string FileName => Path.GetFileName(OriginalPath);

    /// <summary>File name plus " *" when there are unsaved changes.</summary>
    public string DisplayName => IsDirty ? FileName + " *" : FileName;

    /// <summary>Page geometry of <see cref="CurrentFile"/>, refreshed after every change.</summary>
    public IReadOnlyList<PdfPageInfo> Pages { get; private set; }

    public int PageCount => Pages.Count;

    public bool IsDirty => !ReferenceEquals(_current, _saved);

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>What Undo would take back, e.g. "Rotate pages"; null when nothing.</summary>
    public string? UndoDescription => _undo.Count > 0 ? _current.Description : null;

    public string? RedoDescription => _redo.Count > 0 ? _redo.Peek().Description : null;

    /// <summary>Raised after the working file changes (apply, undo, redo). Viewers reload on it.</summary>
    public event EventHandler? DocumentChanged;

    // ---- Changes -----------------------------------------------------------

    /// <summary>
    /// Runs <paramref name="operation"/>(currentFile, newTempFile) and makes the new file current.
    /// On failure nothing changes and the exception propagates. Call on the UI thread.
    /// </summary>
    /// <param name="passwordAfter">
    /// Password of the output file. Leave null to keep the current one; pass "" for an operation
    /// that removes the password (decrypt), or the new password for one that adds it (encrypt).
    /// </param>
    public void Apply(Action<string, string> operation, string description, string? passwordAfter = null)
    {
        ThrowIfDisposed();
        var output = NextFilePath();

        try
        {
            operation(_current.File, output);
        }
        catch
        {
            TryDeleteFile(output);
            throw;
        }

        Commit(output, description, passwordAfter);
    }

    /// <summary>
    /// Same as <see cref="Apply"/>, but runs the operation on a worker thread so the UI stays
    /// responsive. Await it from the UI thread.
    /// </summary>
    public async Task ApplyAsync(Action<string, string> operation, string description, string? passwordAfter = null)
    {
        ThrowIfDisposed();
        var input = _current.File;
        var output = NextFilePath();

        try
        {
            await Task.Run(() => operation(input, output));
        }
        catch
        {
            TryDeleteFile(output);
            throw;
        }

        Commit(output, description, passwordAfter);
    }

    /// <summary>Adopts a file produced elsewhere (copied in) as the next version.</summary>
    public void ApplyFile(string producedFile, string description, string? passwordAfter = null)
        => Apply((_, output) => File.Copy(producedFile, output, overwrite: true), description, passwordAfter);

    private void Commit(string output, string description, string? passwordAfter)
    {
        if (!File.Exists(output))
            throw new InvalidOperationException($"'{description}' did not produce an output file.");

        var password = passwordAfter is null
            ? _current.Password
            : passwordAfter.Length == 0 ? null : passwordAfter;

        // Read pages first: if the result cannot be opened, the change is rejected.
        var pages = PdfPageReader.ReadPages(output, password);

        _undo.Push(_current);
        foreach (var dropped in _redo) TryDeleteFile(dropped.File);
        _redo.Clear();

        _current = new Version(output, password, description);
        Pages = pages;
        RaiseChanged();
    }

    public void Undo()
    {
        if (!CanUndo) return;
        _redo.Push(_current);
        _current = _undo.Pop();
        Pages = PdfPageReader.ReadPages(_current.File, _current.Password);
        RaiseChanged();
    }

    public void Redo()
    {
        if (!CanRedo) return;
        _undo.Push(_current);
        _current = _redo.Pop();
        Pages = PdfPageReader.ReadPages(_current.File, _current.Password);
        RaiseChanged();
    }

    // ---- Saving ------------------------------------------------------------

    /// <summary>Writes the current version over <see cref="OriginalPath"/>.</summary>
    public void Save() => SaveAs(OriginalPath);

    /// <summary>Writes the current version to <paramref name="path"/>, which becomes the document's path.</summary>
    public void SaveAs(string path)
    {
        ThrowIfDisposed();
        var target = Path.GetFullPath(path);

        // Write next to the target first, so a failed copy never leaves a half-written PDF.
        var staging = target + ".kcs-saving";
        File.Copy(_current.File, staging, overwrite: true);
        File.Move(staging, target, overwrite: true);

        OriginalPath = target;
        _saved = _current;
        OnPropertyChanged(nameof(OriginalPath));
        OnPropertyChanged(nameof(FileName));
        RaiseStateChanged();
    }

    /// <summary>Writes a copy of the current version without changing the document's path.</summary>
    public void SaveCopy(string path) => File.Copy(_current.File, Path.GetFullPath(path), overwrite: true);

    // ---- Helpers -----------------------------------------------------------

    private string NextFilePath()
    {
        var number = Interlocked.Increment(ref _fileCounter);
        return Path.Combine(WorkingFolder, number.ToString("000") + ".pdf");
    }

    private void RaiseChanged()
    {
        RaiseStateChanged();
        OnPropertyChanged(nameof(CurrentFile));
        OnPropertyChanged(nameof(Pages));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(Password));
        OnPropertyChanged(nameof(IsEncrypted));
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoDescription));
        OnPropertyChanged(nameof(RedoDescription));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DocumentSession));
    }

    /// <summary>Deletes the temp folder. The original file is untouched.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        TryDeleteFolder(WorkingFolder);
    }

    /// <summary>Removes temp folders left behind by sessions that crashed. Safe to call at startup.</summary>
    public static void CleanStaleFolders(TimeSpan olderThan)
    {
        try
        {
            if (!Directory.Exists(TempRoot)) return;
            foreach (var folder in Directory.GetDirectories(TempRoot))
            {
                if (DateTime.Now - Directory.GetLastWriteTime(folder) > olderThan) TryDeleteFolder(folder);
            }
        }
        catch
        {
            // Housekeeping only.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* temp file, ignore */ }
    }

    private static void TryDeleteFolder(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* ignore */ }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
