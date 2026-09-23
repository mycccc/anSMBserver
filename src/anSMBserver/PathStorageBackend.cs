// PathStorageBackend — IAndroidStorageBackend over a local filesystem path
// via System.IO, plus Android.OS.StatFs for real free space.
//
// Scope: this is the sole storage backend. It implements STORAGE MECHANICS ONLY
// — no SMB semantics. All paths here are share-relative ('/' separated) and
// resolved against the injected root, so AndroidFileSystemAdapter does not need
// to touch System.IO directly.
//
// Exception discipline: methods throw plain System.IO exceptions; the ADAPTER is
// responsible for mapping them to NTSTATUS. This backend never returns/decides an
// NTSTATUS (thin-interface rule). The single Android touch-point is StatFs for
// free space (DriveInfo is unreliable on Android).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IOFileAttributes = System.IO.FileAttributes;

namespace anSMBserver
{
    public class PathStorageBackend : IAndroidStorageBackend
    {
        private readonly string m_root;

        public PathStorageBackend(string root)
        {
            m_root = Path.GetFullPath(root);
            Directory.CreateDirectory(m_root);
        }

        private string Resolve(string relativePath)
        {
            string relative = (relativePath ?? "").TrimStart('/');
            // Reject any attempt to escape the root.
            string combined = Path.GetFullPath(Path.Combine(m_root, relative.Replace('/', Path.DirectorySeparatorChar)));
            string rootFull = Path.GetFullPath(m_root);
            if (!combined.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) &&
                combined != rootFull)
            {
                throw new UnauthorizedAccessException("Path escapes share root: " + relativePath);
            }
            return combined;
        }

        // ---- path existence / kind ----

        public bool PathExists(string relativePath, out bool isDirectory)
        {
            string p = Resolve(relativePath);
            if (Directory.Exists(p)) { isDirectory = true; return true; }
            if (File.Exists(p)) { isDirectory = false; return true; }
            isDirectory = false;
            return false;
        }

        public bool DirectoryExists(string relativePath) => Directory.Exists(Resolve(relativePath));

        public bool FileExists(string relativePath) => File.Exists(Resolve(relativePath));

        // ---- open / create ----

        public object OpenFile(string relativePath, FileMode mode, FileAccess access, FileShare share)
        {
            return new FileStream(Resolve(relativePath), mode, access, share);
        }

        public void CreateDirectory(string relativePath)
        {
            Directory.CreateDirectory(Resolve(relativePath));
        }

        // ---- file-handle ops ----

        public int Read(object fileHandle, long offset, byte[] buffer, int count)
        {
            FileStream fs = (FileStream)fileHandle;
            fs.Seek(offset, SeekOrigin.Begin);
            return fs.Read(buffer, 0, count);
        }

        public void Write(object fileHandle, long offset, byte[] data, int dataOffset, int count)
        {
            FileStream fs = (FileStream)fileHandle;
            fs.Seek(offset, SeekOrigin.Begin);
            fs.Write(data, dataOffset, count);
        }

        public void Flush(object fileHandle)
        {
            ((FileStream)fileHandle).Flush();
        }

        public void SetLength(object fileHandle, long length)
        {
            ((FileStream)fileHandle).SetLength(length);
        }

        public long GetLength(object fileHandle)
        {
            return ((FileStream)fileHandle).Length;
        }

        public void CloseFile(object fileHandle)
        {
            ((FileStream)fileHandle).Dispose();
        }

        // ---- delete / move / set-times ----

        public void DeletePath(string relativePath, bool isDirectory)
        {
            string p = Resolve(relativePath);
            if (isDirectory) Directory.Delete(p, false);
            else File.Delete(p);
        }

        public void MovePath(string fromRelative, string toRelative, bool replaceIfExists)
        {
            string from = Resolve(fromRelative);
            string to = Resolve(toRelative);
            if (replaceIfExists && File.Exists(to)) File.Delete(to);
            if (Directory.Exists(from)) Directory.Move(from, to);
            else File.Move(from, to);
        }

        public void SetTimes(string relativePath, bool isDirectory,
            DateTime? creationUtc, DateTime? lastWriteUtc, DateTime? lastAccessUtc)
        {
            string p = Resolve(relativePath);
            if (creationUtc.HasValue)
            {
                if (isDirectory) Directory.SetCreationTimeUtc(p, creationUtc.Value);
                else File.SetCreationTimeUtc(p, creationUtc.Value);
            }
            if (lastWriteUtc.HasValue)
            {
                if (isDirectory) Directory.SetLastWriteTimeUtc(p, lastWriteUtc.Value);
                else File.SetLastWriteTimeUtc(p, lastWriteUtc.Value);
            }
            if (lastAccessUtc.HasValue)
            {
                if (isDirectory) Directory.SetLastAccessTimeUtc(p, lastAccessUtc.Value);
                else File.SetLastAccessTimeUtc(p, lastAccessUtc.Value);
            }
        }

        // ---- enumeration + stat ----

        public IEnumerable<string> EnumerateEntries(string relativeDir, string pattern)
        {
            string dir = Resolve(relativeDir);
            string pat = string.IsNullOrEmpty(pattern) || pattern == "*" || pattern == "\\*" || pattern == "/"
                ? "*"
                : pattern.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            return Directory.EnumerateFileSystemEntries(dir, pat)
                .Select(e => Path.GetFileName(e));
        }

        public void GetMetadata(string relativePath, out bool isDirectory, out long length,
            out DateTime creationTime, out DateTime lastWriteTime, out DateTime lastAccessTime,
            out StorageEntryAttributes attributes)
        {
            string p = Resolve(relativePath);
            isDirectory = Directory.Exists(p);
            if (isDirectory)
            {
                length = 0;
                creationTime = Directory.GetCreationTime(p);
                lastWriteTime = Directory.GetLastWriteTime(p);
                lastAccessTime = Directory.GetLastAccessTime(p);
            }
            else
            {
                FileInfo fi = new FileInfo(p);
                length = fi.Length;
                creationTime = fi.CreationTime;
                lastWriteTime = fi.LastWriteTime;
                lastAccessTime = fi.LastAccessTime;
            }
            attributes = ToStorageAttributes(File.GetAttributes(p));
        }

        private static StorageEntryAttributes ToStorageAttributes(IOFileAttributes attrs)
        {
            StorageEntryAttributes result = StorageEntryAttributes.None;
            if ((attrs & IOFileAttributes.Directory) != 0) result |= StorageEntryAttributes.Directory;
            if ((attrs & IOFileAttributes.ReadOnly) != 0) result |= StorageEntryAttributes.ReadOnly;
            if ((attrs & IOFileAttributes.Hidden) != 0) result |= StorageEntryAttributes.Hidden;
            return result;
        }

        // ---- volume / free space ----

        public void GetFreeSpace(out long totalBytes, out long availableBytes)
        {
            try
            {
                // Android.OS.StatFs reads the real statfs() values for the volume the
                // root sits on (DriveInfo reports bogus values on Android's /data).
                Android.OS.StatFs stat = new Android.OS.StatFs(m_root);
                totalBytes = stat.TotalBytes;
                availableBytes = stat.AvailableBytes;
            }
            catch
            {
                DriveInfo drive = new DriveInfo(Path.GetPathRoot(m_root));
                totalBytes = drive.TotalSize;
                availableBytes = drive.AvailableFreeSpace;
            }
        }

        public string GetVolumeLabel()
        {
            try
            {
                string label = new DriveInfo(Path.GetPathRoot(m_root)).VolumeLabel;
                return string.IsNullOrEmpty(label) ? "Android" : label;
            }
            catch
            {
                return "Android";
            }
        }
    }
}
