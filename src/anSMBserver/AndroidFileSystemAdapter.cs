// AndroidFileSystemAdapter - the INTFileStore adapter for the Android SMB server.
// ALL storage mechanics route through an IAndroidStorageBackend (SMB semantics
// stay here; storage mechanics move out).
//
// Behaviour preservation:
//   SMB semantics, NTSTATUS mapping, FileInformationClass handling, exception
//   discipline and the disposition logic are strictly preserved. Disk mechanics
//   (open/read/write/flush/close/enumerate/stat/delete/move/set-length/free-space)
//   are delegated to the injected IAndroidStorageBackend instead of direct
//   System.IO/StatFs, so the SMB request actually reaches the backend.
//
// Storage model: the backend is PathStorageBackend; the share root is supplied
// to the backend by the host. The adapter itself never resolves host paths.
//
// Exception discipline: an adapter must only return NTSTATUS and must never let
// an exception escape into the SMB server's async pipeline.
using System;
using System.Collections.Generic;
using System.IO;
using SMBLibrary;
using IOFileAttributes = System.IO.FileAttributes;

namespace anSMBserver
{
    public class AndroidFileSystemAdapter : INTFileStore
    {
        private readonly IAndroidStorageBackend m_backend;

        public AndroidFileSystemAdapter(IAndroidStorageBackend backend)
        {
            m_backend = backend;
        }

        /// <summary>Convert an SMB share-relative path ('\' separated, may be empty) to a
        /// backend share-relative path ('/' separated, no leading separator). "" == root.</summary>
        private static string Rel(string smbPath)
        {
            string rel = (smbPath ?? "").TrimStart('\\', '/');
            return rel.Replace('\\', '/');
        }

        private static string ChildRel(string dirRel, string name)
        {
            return string.IsNullOrEmpty(dirRel) ? name : dirRel + "/" + name;
        }

        // An open object handed back to SMB as an opaque handle. Directories carry
        // no backend file handle (the backend addresses dirs by relative path).
        private class StoreHandle
        {
            public bool IsDir;
            public string RelPath;   // backend share-relative path ('/' separated, "" == root)
            public object FileHandle; // opaque backend file handle; null for directories
        }

        private static StoreHandle AsHandle(object handle)
        {
            return handle as StoreHandle;
        }

        public NTStatus CreateFile(out object handle, out FileStatus fileStatus, string path,
            AccessMask desiredAccess, SMBLibrary.FileAttributes fileAttributes, ShareAccess shareAccess,
            CreateDisposition createDisposition, CreateOptions createOptions, SecurityContext securityContext)
        {
            handle = null;
            fileStatus = FileStatus.FILE_DOES_NOT_EXIST;
            try
            {
                return CreateFileInternal(out handle, out fileStatus, Rel(path), createDisposition, createOptions);
            }
            catch (FileNotFoundException)
            {
                return NTStatus.STATUS_NO_SUCH_FILE;
            }
            catch (DirectoryNotFoundException)
            {
                return NTStatus.STATUS_NO_SUCH_FILE;
            }
            catch (UnauthorizedAccessException)
            {
                return NTStatus.STATUS_ACCESS_DENIED;
            }
            catch (ArgumentException)
            {
                return NTStatus.STATUS_OBJECT_PATH_INVALID;
            }
            catch (IOException)
            {
                // Create races (target appeared between exists-check and open) and
                // full-disk/device errors both surface as IOException; report an
                // I/O-level failure rather than letting it escape.
                return NTStatus.STATUS_DATA_ERROR;
            }
        }

        private NTStatus CreateFileInternal(out object handle, out FileStatus fileStatus, string relPath,
            CreateDisposition createDisposition, CreateOptions createOptions)
        {
            handle = null;
            fileStatus = FileStatus.FILE_DOES_NOT_EXIST;
            bool forceDirectory = (createOptions & CreateOptions.FILE_DIRECTORY_FILE) != 0;
            bool forceFile = (createOptions & CreateOptions.FILE_NON_DIRECTORY_FILE) != 0;
            bool exists = m_backend.PathExists(relPath, out bool existingIsDir);

            if (forceDirectory && forceFile)
            {
                return NTStatus.STATUS_INVALID_PARAMETER;
            }

            StoreHandle sh = new StoreHandle { IsDir = existingIsDir, RelPath = relPath };

            if (createDisposition == CreateDisposition.FILE_SUPERSEDE)
            {
                if (exists)
                {
                    if (existingIsDir)
                    {
                        return NTStatus.STATUS_FILE_IS_A_DIRECTORY;
                    }
                    m_backend.DeletePath(relPath, false);
                }
                sh.FileHandle = m_backend.OpenFile(relPath, System.IO.FileMode.CreateNew, System.IO.FileAccess.ReadWrite, System.IO.FileShare.ReadWrite);
                handle = sh;
                fileStatus = FileStatus.FILE_SUPERSEDED;
                return NTStatus.STATUS_SUCCESS;
            }
            else if (createDisposition == CreateDisposition.FILE_OPEN)
            {
                if (!exists)
                {
                    return NTStatus.STATUS_NO_SUCH_FILE;
                }
                if (existingIsDir && forceFile)
                {
                    return NTStatus.STATUS_FILE_IS_A_DIRECTORY;
                }
                if (!existingIsDir && forceDirectory)
                {
                    return NTStatus.STATUS_NOT_A_DIRECTORY;
                }
                fileStatus = FileStatus.FILE_EXISTS;
                sh.IsDir = existingIsDir;
                sh.FileHandle = existingIsDir ? null : m_backend.OpenFile(relPath, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite, System.IO.FileShare.ReadWrite);
                handle = sh;
                return NTStatus.STATUS_SUCCESS;
            }
            else if (createDisposition == CreateDisposition.FILE_CREATE)
            {
                if (exists)
                {
                    return NTStatus.STATUS_OBJECT_NAME_COLLISION;
                }
                if (forceDirectory)
                {
                    m_backend.CreateDirectory(relPath);
                    sh.IsDir = true;
                    sh.FileHandle = null;
                }
                else
                {
                    sh.FileHandle = m_backend.OpenFile(relPath, System.IO.FileMode.CreateNew, System.IO.FileAccess.ReadWrite, System.IO.FileShare.ReadWrite);
                    sh.IsDir = false;
                }
                handle = sh;
                fileStatus = FileStatus.FILE_CREATED;
                return NTStatus.STATUS_SUCCESS;
            }
            else if (createDisposition == CreateDisposition.FILE_OPEN_IF)
            {
                if (!exists)
                {
                    if (forceDirectory)
                    {
                        m_backend.CreateDirectory(relPath);
                        sh.IsDir = true;
                        sh.FileHandle = null;
                        fileStatus = FileStatus.FILE_CREATED;
                    }
                    else
                    {
                        sh.FileHandle = m_backend.OpenFile(relPath, System.IO.FileMode.CreateNew, System.IO.FileAccess.ReadWrite, System.IO.FileShare.ReadWrite);
                        sh.IsDir = false;
                        fileStatus = FileStatus.FILE_CREATED;
                    }
                }
                else
                {
                    fileStatus = FileStatus.FILE_EXISTS;
                    sh.IsDir = existingIsDir;
                    sh.FileHandle = existingIsDir ? null : m_backend.OpenFile(relPath, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite, System.IO.FileShare.ReadWrite);
                }
                handle = sh;
                return NTStatus.STATUS_SUCCESS;
            }
            else if (createDisposition == CreateDisposition.FILE_OVERWRITE)
            {
                if (!exists)
                {
                    return NTStatus.STATUS_NO_SUCH_FILE;
                }
                if (existingIsDir)
                {
                    return NTStatus.STATUS_FILE_IS_A_DIRECTORY;
                }
                sh.FileHandle = m_backend.OpenFile(relPath, System.IO.FileMode.Truncate, System.IO.FileAccess.ReadWrite, System.IO.FileShare.ReadWrite);
                sh.IsDir = false;
                handle = sh;
                fileStatus = FileStatus.FILE_OVERWRITTEN;
                return NTStatus.STATUS_SUCCESS;
            }
            else if (createDisposition == CreateDisposition.FILE_OVERWRITE_IF)
            {
                if (!exists)
                {
                    sh.FileHandle = m_backend.OpenFile(relPath, System.IO.FileMode.CreateNew, System.IO.FileAccess.ReadWrite, System.IO.FileShare.ReadWrite);
                    sh.IsDir = false;
                    fileStatus = FileStatus.FILE_CREATED;
                }
                else
                {
                    sh.FileHandle = m_backend.OpenFile(relPath, System.IO.FileMode.Truncate, System.IO.FileAccess.ReadWrite, System.IO.FileShare.ReadWrite);
                    sh.IsDir = false;
                    fileStatus = FileStatus.FILE_OVERWRITTEN;
                }
                handle = sh;
                return NTStatus.STATUS_SUCCESS;
            }

            return NTStatus.STATUS_INVALID_PARAMETER;
        }

        public NTStatus CloseFile(object handle)
        {
            StoreHandle sh = AsHandle(handle);
            if (sh == null)
            {
                return NTStatus.STATUS_INVALID_HANDLE;
            }
            try
            {
                if (sh.FileHandle != null)
                {
                    m_backend.CloseFile(sh.FileHandle);
                }
            }
            catch (IOException)
            {
            }
            return NTStatus.STATUS_SUCCESS;
        }

        public NTStatus ReadFile(out byte[] data, object handle, long offset, int maxCount)
        {
            data = null;
            StoreHandle sh = AsHandle(handle);
            if (sh == null || sh.FileHandle == null)
            {
                return NTStatus.STATUS_INVALID_HANDLE;
            }
            try
            {
                if (offset >= m_backend.GetLength(sh.FileHandle))
                {
                    data = new byte[0];
                    return NTStatus.STATUS_SUCCESS;
                }
                long fileLength = m_backend.GetLength(sh.FileHandle);
                int toRead = (int)Math.Min(maxCount, Math.Max(0, fileLength - offset));
                data = new byte[toRead];
                int read = m_backend.Read(sh.FileHandle, offset, data, toRead);
                if (read < data.Length)
                {
                    Array.Resize(ref data, read);
                }
                return NTStatus.STATUS_SUCCESS;
            }
            catch (IOException)
            {
                return NTStatus.STATUS_DATA_ERROR;
            }
        }

        public NTStatus WriteFile(out int numberOfBytesWritten, object handle, long offset, byte[] data)
        {
            numberOfBytesWritten = 0;
            StoreHandle sh = AsHandle(handle);
            if (sh == null || sh.FileHandle == null)
            {
                return NTStatus.STATUS_INVALID_HANDLE;
            }
            try
            {
                m_backend.Write(sh.FileHandle, offset, data, 0, data.Length);
                m_backend.Flush(sh.FileHandle);
                numberOfBytesWritten = data.Length;
                return NTStatus.STATUS_SUCCESS;
            }
            catch (IOException)
            {
                // Includes full-disk / device errors on the device.
                return NTStatus.STATUS_DATA_ERROR;
            }
        }

        public NTStatus FlushFileBuffers(object handle)
        {
            StoreHandle sh = AsHandle(handle);
            if (sh == null || sh.FileHandle == null)
            {
                return NTStatus.STATUS_INVALID_HANDLE;
            }
            try
            {
                m_backend.Flush(sh.FileHandle);
                return NTStatus.STATUS_SUCCESS;
            }
            catch (IOException)
            {
                return NTStatus.STATUS_DATA_ERROR;
            }
        }

        public NTStatus LockFile(object handle, long byteOffset, long length, bool exclusiveLock)
        {
            // Simple store: byte-range locking is not enforced on the filesystem side.
            return NTStatus.STATUS_SUCCESS;
        }

        public NTStatus UnlockFile(object handle, long byteOffset, long length)
        {
            return NTStatus.STATUS_SUCCESS;
        }

        public NTStatus QueryDirectory(out List<QueryDirectoryFileInformation> result, object handle, string fileName, FileInformationClass informationClass)
        {
            result = new List<QueryDirectoryFileInformation>();
            StoreHandle sh = AsHandle(handle);
            if (sh == null)
            {
                return NTStatus.STATUS_INVALID_HANDLE;
            }
            // The dir to enumerate is the handle itself when it's a dir; when it's a
            // file (server may query a file's parent), use the parent directory.
            string dirRel;
            if (sh.IsDir)
            {
                dirRel = sh.RelPath;
            }
            else
            {
                int slash = sh.RelPath.LastIndexOf('/');
                dirRel = slash < 0 ? "" : sh.RelPath.Substring(0, slash);
            }

            bool all = string.IsNullOrEmpty(fileName) || fileName == "*" || fileName == "\\*";
            string pattern = all ? "*" : Rel(fileName);

            try
            {
                foreach (string name in m_backend.EnumerateEntries(dirRel, pattern))
                {
                    string childRel = ChildRel(dirRel, name);
                    m_backend.GetMetadata(childRel, out bool isDir, out long length,
                        out DateTime creation, out DateTime lastAccess, out DateTime lastWrite,
                        out StorageEntryAttributes attrs);
                    SMBLibrary.FileAttributes smbAttrs = ToSMBFileAttributes(attrs, isDir);

                    // The entry class MUST match the client-requested
                    // FileInformationClass, otherwise SMBLibrary rejects the
                    // query with STATUS_INVALID_PARAMETER (macOS smbfs requests
                    // FileIdBothDirectoryInformation). See QueryDirectoryHelper.
                    switch (informationClass)
                    {
                        case FileInformationClass.FileDirectoryInformation:
                            result.Add(new FileDirectoryInformation
                            {
                                FileName = name,
                                CreationTime = creation,
                                LastAccessTime = lastAccess,
                                LastWriteTime = lastWrite,
                                ChangeTime = lastWrite,
                                EndOfFile = length,
                                AllocationSize = Align(length),
                                FileAttributes = smbAttrs
                            });
                            break;
                        case FileInformationClass.FileFullDirectoryInformation:
                            result.Add(new FileFullDirectoryInformation
                            {
                                FileName = name,
                                CreationTime = creation,
                                LastAccessTime = lastAccess,
                                LastWriteTime = lastWrite,
                                ChangeTime = lastWrite,
                                EndOfFile = length,
                                AllocationSize = Align(length),
                                FileAttributes = smbAttrs,
                                EaSize = 0
                            });
                            break;
                        case FileInformationClass.FileBothDirectoryInformation:
                            result.Add(new FileBothDirectoryInformation
                            {
                                FileName = name,
                                CreationTime = creation,
                                LastAccessTime = lastAccess,
                                LastWriteTime = lastWrite,
                                ChangeTime = lastWrite,
                                EndOfFile = length,
                                AllocationSize = Align(length),
                                FileAttributes = smbAttrs,
                                EaSize = 0
                            });
                            break;
                        case FileInformationClass.FileIdBothDirectoryInformation:
                            result.Add(new FileIdBothDirectoryInformation
                            {
                                FileName = name,
                                CreationTime = creation,
                                LastAccessTime = lastAccess,
                                LastWriteTime = lastWrite,
                                ChangeTime = lastWrite,
                                EndOfFile = length,
                                AllocationSize = Align(length),
                                FileAttributes = smbAttrs,
                                EaSize = 0,
                                FileId = ComputeFileId(name)
                            });
                            break;
                        case FileInformationClass.FileIdFullDirectoryInformation:
                            result.Add(new FileIdFullDirectoryInformation
                            {
                                FileName = name,
                                CreationTime = creation,
                                LastAccessTime = lastAccess,
                                LastWriteTime = lastWrite,
                                ChangeTime = lastWrite,
                                EndOfFile = length,
                                AllocationSize = Align(length),
                                FileAttributes = smbAttrs,
                                EaSize = 0,
                                FileId = ComputeFileId(name)
                            });
                            break;
                        case FileInformationClass.FileNamesInformation:
                            result.Add(new FileNamesInformation { FileName = name });
                            break;
                        default:
                            return NTStatus.STATUS_NOT_SUPPORTED;
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                return NTStatus.STATUS_ACCESS_DENIED;
            }
            catch (DirectoryNotFoundException)
            {
                return NTStatus.STATUS_OBJECT_PATH_NOT_FOUND;
            }
            catch (IOException)
            {
                return NTStatus.STATUS_DATA_ERROR;
            }
            return NTStatus.STATUS_SUCCESS;
        }

        // Stable 64-bit FNV-1a hash over the UTF-16 name. Directory-local
        // uniqueness is sufficient for FileIdBoth/FileIdFull FileId fields.
        private static ulong ComputeFileId(string name)
        {
            ulong hash = 14695981039346656037UL;
            foreach (char c in name)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return hash;
        }

        private static long Align(long length)
        {
            return (length + 4095) / 4096 * 4096;
        }

        private static SMBLibrary.FileAttributes ToSMBFileAttributes(StorageEntryAttributes attrs, bool isDir)
        {
            SMBLibrary.FileAttributes result = isDir ? SMBLibrary.FileAttributes.Directory : SMBLibrary.FileAttributes.Normal;
            if ((attrs & StorageEntryAttributes.ReadOnly) != 0)
            {
                result |= SMBLibrary.FileAttributes.ReadOnly;
            }
            if ((attrs & StorageEntryAttributes.Hidden) != 0)
            {
                result |= SMBLibrary.FileAttributes.Hidden;
            }
            return result;
        }

        public NTStatus GetFileInformation(out FileInformation result, object handle, FileInformationClass informationClass)
        {
            result = null;
            StoreHandle sh = AsHandle(handle);
            if (sh == null)
            {
                return NTStatus.STATUS_INVALID_HANDLE;
            }

            try
            {
                m_backend.GetMetadata(sh.RelPath, out bool isDir, out long length,
                    out DateTime creation, out DateTime lastWrite, out DateTime lastAccess,
                    out StorageEntryAttributes rawAttrs);
                SMBLibrary.FileAttributes attrs = ToSMBFileAttributes(rawAttrs, isDir);

                if (informationClass == FileInformationClass.FileBasicInformation)
                {
                    FileBasicInformation info = new FileBasicInformation();
                    info.CreationTime = new SetFileTime(creation);
                    info.LastAccessTime = new SetFileTime(lastAccess);
                    info.LastWriteTime = new SetFileTime(lastWrite);
                    info.ChangeTime = new SetFileTime(lastWrite);
                    info.FileAttributes = attrs;
                    result = info;
                    return NTStatus.STATUS_SUCCESS;
                }
                else if (informationClass == FileInformationClass.FileStandardInformation)
                {
                    FileStandardInformation info = new FileStandardInformation
                    {
                        AllocationSize = Align(length),
                        EndOfFile = length,
                        DeletePending = false,
                        Directory = isDir
                    };
                    result = info;
                    return NTStatus.STATUS_SUCCESS;
                }
                else if (informationClass == FileInformationClass.FileNetworkOpenInformation)
                {
                    FileNetworkOpenInformation info = new FileNetworkOpenInformation
                    {
                        CreationTime = creation,
                        LastAccessTime = lastAccess,
                        LastWriteTime = lastWrite,
                        ChangeTime = lastWrite,
                        AllocationSize = Align(length),
                        EndOfFile = length,
                        FileAttributes = attrs,
                        IsDirectory = isDir
                    };
                    result = info;
                    return NTStatus.STATUS_SUCCESS;
                }
                return NTStatus.STATUS_NOT_SUPPORTED;
            }
            catch (FileNotFoundException)
            {
                return NTStatus.STATUS_NO_SUCH_FILE;
            }
            catch (DirectoryNotFoundException)
            {
                return NTStatus.STATUS_NO_SUCH_FILE;
            }
            catch (UnauthorizedAccessException)
            {
                return NTStatus.STATUS_ACCESS_DENIED;
            }
            catch (IOException)
            {
                return NTStatus.STATUS_DATA_ERROR;
            }
        }

        public NTStatus SetFileInformation(object handle, FileInformation information)
        {
            StoreHandle sh = AsHandle(handle);
            if (sh == null)
            {
                return NTStatus.STATUS_INVALID_HANDLE;
            }
            string relPath = sh.RelPath;

            if (information is FileDispositionInformation disposition)
            {
                if (disposition.DeletePending)
                {
                    try
                    {
                        bool isDir = DirectoryExistsFor(relPath);
                        m_backend.DeletePath(relPath, isDir);
                    }
                    catch (FileNotFoundException)
                    {
                        return NTStatus.STATUS_NO_SUCH_FILE;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        return NTStatus.STATUS_NO_SUCH_FILE;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return NTStatus.STATUS_ACCESS_DENIED;
                    }
                    catch (IOException)
                    {
                        return NTStatus.STATUS_DIRECTORY_NOT_EMPTY;
                    }
                    return NTStatus.STATUS_SUCCESS;
                }
                return NTStatus.STATUS_SUCCESS;
            }
            else if (information is FileRenameInformationType2 rename)
            {
                string newRel = Rel(rename.FileName);
                try
                {
                    // SMB rename semantics: if the client requests replace-if-exists and
                    // the target already exists, the target is replaced.
                    m_backend.MovePath(relPath, newRel, rename.ReplaceIfExists);
                }
                catch (FileNotFoundException)
                {
                    return NTStatus.STATUS_NO_SUCH_FILE;
                }
                catch (DirectoryNotFoundException)
                {
                    return NTStatus.STATUS_NO_SUCH_FILE;
                }
                catch (UnauthorizedAccessException)
                {
                    return NTStatus.STATUS_ACCESS_DENIED;
                }
                catch (IOException)
                {
                    return NTStatus.STATUS_OBJECT_NAME_COLLISION;
                }
                return NTStatus.STATUS_SUCCESS;
            }
            else if (information is FileEndOfFileInformation eof)
            {
                try
                {
                    object fh = m_backend.OpenFile(relPath, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite, System.IO.FileShare.ReadWrite);
                    try
                    {
                        m_backend.SetLength(fh, eof.EndOfFile);
                    }
                    finally
                    {
                        m_backend.CloseFile(fh);
                    }
                }
                catch (FileNotFoundException)
                {
                    return NTStatus.STATUS_NO_SUCH_FILE;
                }
                catch (DirectoryNotFoundException)
                {
                    return NTStatus.STATUS_NO_SUCH_FILE;
                }
                catch (UnauthorizedAccessException)
                {
                    return NTStatus.STATUS_ACCESS_DENIED;
                }
                catch (IOException)
                {
                    return NTStatus.STATUS_DATA_ERROR;
                }
                return NTStatus.STATUS_SUCCESS;
            }
            else if (information is FileAllocationInformation alloc)
            {
                // Preallocation only ever extends the file; it never truncates.
                // Android has no separate allocation/EOF split, so the practical
                // equivalent is extending EndOfFile. A client that later wants a
                // smaller size sends FileEndOfFileInformation.
                // macOS smbfs sends this before the first WRITE when uploading;
                // returning NOT_SUPPORTED here makes Finder abort the copy
                // ("operation not supported") leaving a zero-length file.
                try
                {
                    bool isDir;
                    bool exists = m_backend.PathExists(relPath, out isDir);
                    if (exists && !isDir)
                    {
                        object fh = m_backend.OpenFile(relPath, System.IO.FileMode.Open, System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite);
                        try
                        {
                            if (alloc.AllocationSize > m_backend.GetLength(fh))
                            {
                                m_backend.SetLength(fh, alloc.AllocationSize);
                            }
                        }
                        finally
                        {
                            m_backend.CloseFile(fh);
                        }
                    }
                }
                catch (FileNotFoundException)
                {
                    return NTStatus.STATUS_NO_SUCH_FILE;
                }
                catch (DirectoryNotFoundException)
                {
                    return NTStatus.STATUS_NO_SUCH_FILE;
                }
                catch (UnauthorizedAccessException)
                {
                    return NTStatus.STATUS_ACCESS_DENIED;
                }
                catch (IOException)
                {
                    return NTStatus.STATUS_DATA_ERROR;
                }
                return NTStatus.STATUS_SUCCESS;
            }
            else if (information is FileBasicInformation basic)
            {
                // macOS smbfs sets timestamps after an upload. Android cannot set
                // every attribute (e.g. change time), so timestamp application is
                // best-effort; never fail the whole operation over it.
                try
                {
                    bool isDir = DirectoryExistsFor(relPath);
                    m_backend.SetTimes(relPath, isDir,
                        !basic.CreationTime.MustNotChange ? basic.CreationTime.Time : null,
                        !basic.LastWriteTime.MustNotChange ? basic.LastWriteTime.Time : null,
                        !basic.LastAccessTime.MustNotChange ? basic.LastAccessTime.Time : null);
                }
                catch (FileNotFoundException)
                {
                    return NTStatus.STATUS_NO_SUCH_FILE;
                }
                catch (DirectoryNotFoundException)
                {
                    return NTStatus.STATUS_NO_SUCH_FILE;
                }
                catch (Exception)
                {
                    // Attribute setting is best-effort on Android; ignore
                    // UnauthorizedAccess/IO/etc so the upload still completes.
                }
                return NTStatus.STATUS_SUCCESS;
            }
            return NTStatus.STATUS_NOT_SUPPORTED;
        }

        private bool DirectoryExistsFor(string relPath)
        {
            return m_backend.PathExists(relPath, out bool isDir) && isDir;
        }

        public NTStatus GetFileSystemInformation(out FileSystemInformation result, FileSystemInformationClass informationClass)
        {
            result = null;
            try
            {
                // Real free space from the backend (StatFs on Android; DriveInfo is
                // unreliable there and made macOS report the share as full).
                m_backend.GetFreeSpace(out long total, out long available);

                if (informationClass == FileSystemInformationClass.FileFsVolumeInformation)
                {
                    FileFsVolumeInformation info = new FileFsVolumeInformation
                    {
                        VolumeLabel = m_backend.GetVolumeLabel(),
                        VolumeSerialNumber = 0x20260831,
                        SupportsObjects = false
                    };
                    result = info;
                    return NTStatus.STATUS_SUCCESS;
                }
                else if (informationClass == FileSystemInformationClass.FileFsSizeInformation)
                {
                    FileFsSizeInformation info = new FileFsSizeInformation();
                    info.TotalAllocationUnits = total / 4096;
                    info.AvailableAllocationUnits = available / 4096;
                    info.SectorsPerAllocationUnit = 8;
                    info.BytesPerSector = 512;
                    result = info;
                    return NTStatus.STATUS_SUCCESS;
                }
                else if (informationClass == FileSystemInformationClass.FileFsFullSizeInformation)
                {
                    // macOS smbfs queries the free space via FileFsFullSizeInformation
                    // before an upload; NOT_SUPPORTED here made Finder report
                    // "disk full" even though the device had plenty of space.
                    FileFsFullSizeInformation info = new FileFsFullSizeInformation();
                    info.TotalAllocationUnits = total / 4096;
                    info.CallerAvailableAllocationUnits = available / 4096;
                    info.ActualAvailableAllocationUnits = available / 4096;
                    info.SectorsPerAllocationUnit = 8;
                    info.BytesPerSector = 512;
                    result = info;
                    return NTStatus.STATUS_SUCCESS;
                }
                else if (informationClass == FileSystemInformationClass.FileFsAttributeInformation)
                {
                    FileFsAttributeInformation info = new FileFsAttributeInformation
                    {
                        FileSystemAttributes = FileSystemAttributes.CaseSensitiveSearch |
                                              FileSystemAttributes.CasePreservedNames |
                                              FileSystemAttributes.UnicodeOnDisk,
                        MaximumComponentNameLength = 255,
                        FileSystemName = "Android"
                    };
                    result = info;
                    return NTStatus.STATUS_SUCCESS;
                }
                else if (informationClass == FileSystemInformationClass.FileFsDeviceInformation)
                {
                    FileFsDeviceInformation info = new FileFsDeviceInformation
                    {
                        DeviceType = DeviceType.Disk,
                        Characteristics = 0
                    };
                    result = info;
                    return NTStatus.STATUS_SUCCESS;
                }
                return NTStatus.STATUS_NOT_SUPPORTED;
            }
            catch (IOException)
            {
                return NTStatus.STATUS_DATA_ERROR;
            }
        }

        public NTStatus SetFileSystemInformation(FileSystemInformation information)
        {
            return NTStatus.STATUS_NOT_SUPPORTED;
        }

        public NTStatus GetSecurityInformation(out SecurityDescriptor result, object handle, SecurityInformation securityInformation)
        {
            result = new SecurityDescriptor();
            return NTStatus.STATUS_SUCCESS;
        }

        public NTStatus SetSecurityInformation(object handle, SecurityInformation securityInformation, SecurityDescriptor securityDescriptor)
        {
            return NTStatus.STATUS_SUCCESS;
        }

        public NTStatus NotifyChange(out object ioRequest, object handle, NotifyChangeFilter completionFilter, bool watchTree, int outputBufferSize, OnNotifyChangeCompleted onNotifyChangeCompleted, object context)
        {
            ioRequest = null;
            return NTStatus.STATUS_NOT_SUPPORTED;
        }

        public NTStatus Cancel(object ioRequest)
        {
            // The adapter never truly pends
            // an async operation, so Cancel is a semantic no-op. STATUS_NOT_SUPPORTED
            // lets the server respond with a deterministic STATUS_CANCELLED (vs
            // STATUS_NOT_IMPLEMENTED which yields no response). Do NOT expand this to
            // NotifyChange/locking/security-descriptor deferred semantics.
            return NTStatus.STATUS_NOT_SUPPORTED;
        }

        public NTStatus DeviceIOControl(object handle, uint ctlCode, byte[] input, out byte[] output, int maxOutputLength)
        {
            output = null;
            return NTStatus.STATUS_NOT_SUPPORTED;
        }
    }
}
