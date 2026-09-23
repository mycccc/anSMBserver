// Thin Android storage-backend abstraction.
//
// DESIGN RULE:
//   This interface expresses Android storage backend ATOMIC MECHANICS ONLY.
//   It MUST NOT carry SMB/Windows semantics:
//     - no CreateDisposition / AccessMask / ShareAccess / SMB CreateOptions
//     - no SMB FileInformationClass / FileSystemInformationClass
//     - no NTSTATUS policy / status decisions
//   Those belong to AndroidFileSystemAdapter (the SMB semantic layer), which
//   composes these atomic primitives into SMB behaviour.
//
//   It is NOT a speculative "universal URI/path filesystem interface": it is the
//   minimal set the PathBackend needs. A future URI backend (SAF/MediaStore) is a
//   separate, later concern; do not broaden this interface speculatively.
//
// Scope / conventions:
//   - All paths are SHARE-RELATIVE, using '/' as the separator, relative to the
//     backend root. The backend resolves and owns them; the caller never sees or
//     touches host paths.
//   - FILE handles are opaque (object). The caller holds what OpenFile returned
//     and passes it back for read/write/flush/setlength/length. The caller never
//     reaches into a System.IO stream — that is what removes direct System.IO from
//     the adapter.
//   - A DIRECTORY is not given an opaque handle in this minimal shape: it is
//     addressed by its relative path for open/enumerate/stat. (PathBackend needs
//     nothing more; a future URI backend can adapt.)
//   - .NET System.IO enum values (FileMode/FileAccess/FileShare) are generic OS
//     open mechanics, NOT SMB enums, and are permitted at this boundary.
namespace anSMBserver
{
    using System;
    using System.Collections.Generic;

    /// <summary>Minimal attributes a storage backend can report for an entry.</summary>
    [Flags]
    public enum StorageEntryAttributes
    {
        None = 0,
        Directory = 1,
        ReadOnly = 2,
        Hidden = 4,
    }

    public interface IAndroidStorageBackend
    {
        // ---- path existence / kind ----

        /// <summary>True if the share-relative path exists; out isDirectory set when it is a directory.</summary>
        bool PathExists(string relativePath, out bool isDirectory);

        // ---- open / create (generic IO mechanics, opaque FILE handle) ----

        /// <summary>Open (or create per mode/access/share) a FILE. Returns an opaque handle. Throws on IO error; the adapter maps exceptions.</summary>
        object OpenFile(string relativePath, System.IO.FileMode mode, System.IO.FileAccess access, System.IO.FileShare share);

        /// <summary>Create a directory (non-recursive; parent must exist). Throws on error.</summary>
        void CreateDirectory(string relativePath);

        // ---- file-handle ops (opaque; no direct System.IO by the caller) ----

        /// <summary>Read up to count bytes at the absolute byte offset into buffer; returns bytes actually read.</summary>
        int Read(object fileHandle, long offset, byte[] buffer, int count);

        /// <summary>Write count bytes of data (starting at data[dataOffset]) at the absolute byte offset.</summary>
        void Write(object fileHandle, long offset, byte[] data, int dataOffset, int count);

        /// <summary>Flush buffered bytes of this handle to durable storage.</summary>
        void Flush(object fileHandle);

        /// <summary>Set the logical length of the open file (extend or truncate).</summary>
        void SetLength(object fileHandle, long length);

        /// <summary>Current logical length of the open file.</summary>
        long GetLength(object fileHandle);

        /// <summary>Release the file handle.</summary>
        void CloseFile(object fileHandle);

        // ---- delete / move / set-times (path primitives) ----

        /// <summary>Delete a file, or a directory non-recursively (directory must be empty or throw).</summary>
        void DeletePath(string relativePath, bool isDirectory);

        /// <summary>Rename/move (share-relative -> share-relative). Overwrites a file target if replaceIfExists.</summary>
        void MovePath(string fromRelative, string toRelative, bool replaceIfExists);

        /// <summary>Best-effort set timestamps on a path. Must not corrupt data; caller treats as non-fatal.</summary>
        void SetTimes(string relativePath, bool isDirectory,
            DateTime? creationUtc, DateTime? lastWriteUtc, DateTime? lastAccessUtc);

        // ---- enumeration + stat ----

        /// <summary>Enumerate child entry names (single share-relative components) of a directory matching pattern ("*" = all).</summary>
        IEnumerable<string> EnumerateEntries(string relativeDir, string pattern);

        /// <summary>Stat a path (must exist): kind, length, timestamps, generic attributes.</summary>
        void GetMetadata(string relativePath, out bool isDirectory, out long length,
            out DateTime creationTime, out DateTime lastWriteTime, out DateTime lastAccessTime,
            out StorageEntryAttributes attributes);

        // ---- volume / free space (backend-specific; NOT System.IO DriveInfo on Android) ----

        /// <summary>Report real total + available bytes of the backend volume.</summary>
        void GetFreeSpace(out long totalBytes, out long availableBytes);

        /// <summary>Report a stable volume label.</summary>
        string GetVolumeLabel();
    }
}
