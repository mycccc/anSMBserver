// On-device self-probe.
// Uses SMBLibrary's own SMB2 client against 127.0.0.1 to verify the layered
// chain on the device: TCP -> NEGOTIATE -> SESSION_SETUP (NTLM) ->
// TREE_CONNECT -> QUERY_DIR -> CREATE/WRITE/READ -> RENAME -> MKDIR -> DELETE.
// Ported from the earlier desktop probe; the point here is the Android runtime.
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using SMBLibrary;
using SMBLibrary.Client;

namespace anSMBserver
{
    public static class SelfProbe
    {
        private const int MaxFailures = 99;

        public static bool Run(IPAddress address, int port, string user, string password, List<string> steps)
        {
            int failures = 0;
            void Line(string text) => steps.Add(text);
            bool Step(string name, bool ok, string detail)
            {
                Line((ok ? "PASS " : "FAIL ") + name + (detail != null ? " -> " + detail : ""));
                if (!ok) failures++;
                return ok;
            }

            var client = new PortableSmbClient();
            if (!Step("1 TCP connect", client.ConnectOnPort(address, SMBTransportType.DirectTCPTransport, port), null))
            {
                return false;
            }

            NTStatus status = client.Login("", user, password);
            if (!Step("2 SESSION_SETUP + NTLM auth", status == NTStatus.STATUS_SUCCESS, status.ToString()))
            {
                client.Disconnect();
                return false;
            }

            List<string> shares = client.ListShares(out status);
            if (!Step("3 IPC TREE_CONNECT + share list", status == NTStatus.STATUS_SUCCESS && shares.Count > 0,
                status + " [" + string.Join(",", shares) + "]"))
            {
                client.Disconnect();
                return false;
            }

            ISMBFileStore store = client.TreeConnect("Internal", out status);
            if (!Step("4 TREE_CONNECT share=Internal", status == NTStatus.STATUS_SUCCESS, status.ToString()))
            {
                client.Disconnect();
                return false;
            }

            // Idempotence: remove leftovers from previous runs so the probe
            // always starts from a clean share (all cleanup through SMB).
            Cleanup(store, "\\probe.txt");
            Cleanup(store, "\\probe-renamed.txt");
            Cleanup(store, "\\中文 测试😀.txt");
            Cleanup(store, "\\newdir");

            // Write a file, read it back, then list the directory.
            status = WriteFile(store, "\\probe.txt", "p11 on-device probe\n");
            if (!Step("5 CREATE + WRITE probe.txt", status == NTStatus.STATUS_SUCCESS, status.ToString()))
            {
                store.Disconnect();
                client.Disconnect();
                return false;
            }

            byte[] data;
            status = ReadFile(store, "\\probe.txt", out data);
            string text = data != null ? Encoding.UTF8.GetString(data).Trim() : "";
            if (!Step("6 OPEN + READ probe.txt", status == NTStatus.STATUS_SUCCESS && text == "p11 on-device probe",
                status + " [" + text + "]"))
            {
                store.Disconnect();
                client.Disconnect();
                return false;
            }

            // Unicode + space filenames (smoke level).
            status = WriteFile(store, "\\中文 测试😀.txt", "unicode\n");
            if (!Step("7 CREATE + WRITE 中文 测试😀.txt", status == NTStatus.STATUS_SUCCESS, status.ToString()))
            {
                store.Disconnect();
                client.Disconnect();
                return false;
            }

            List<string> entries = QueryDir(store, out status);
            if (!Step("8 QUERY_DIR root", status == NTStatus.STATUS_SUCCESS && entries != null && entries.Count >= 2,
                status + " [" + string.Join(",", entries ?? new List<string>()) + "]"))
            {
                store.Disconnect();
                client.Disconnect();
                return false;
            }

            status = RenameFile(store, "\\probe.txt", "\\probe-renamed.txt");
            if (!Step("9 RENAME probe.txt -> probe-renamed.txt", status == NTStatus.STATUS_SUCCESS, status.ToString()))
            {
                store.Disconnect();
                client.Disconnect();
                return false;
            }

            status = CreateDir(store, "\\newdir");
            if (!Step("10 MKDIR newdir", status == NTStatus.STATUS_SUCCESS, status.ToString()))
            {
                store.Disconnect();
                client.Disconnect();
                return false;
            }

            status = DeleteFile(store, "\\probe-renamed.txt");
            if (!Step("11 DELETE probe-renamed.txt", status == NTStatus.STATUS_SUCCESS, status.ToString()))
            {
                store.Disconnect();
                client.Disconnect();
                return false;
            }

            store.Disconnect();
            client.Disconnect();
            Step("12 disconnect", true, null);
            Line(failures == 0 ? "SELF-PROBE: ALL PASS" : "SELF-PROBE: FAILURES=" + failures);
            return failures == 0;
        }

        private static NTStatus WriteFile(ISMBFileStore store, string path, string content)
        {
            object handle;
            NTStatus status = store.CreateFile(out handle, out _, path, AccessMask.GENERIC_WRITE,
                SMBLibrary.FileAttributes.Normal, ShareAccess.Read | ShareAccess.Write,
                CreateDisposition.FILE_OVERWRITE_IF, CreateOptions.FILE_NON_DIRECTORY_FILE, null);
            if (status != NTStatus.STATUS_SUCCESS) return status;
            status = store.WriteFile(out int written, handle, 0, Encoding.UTF8.GetBytes(content));
            store.CloseFile(handle);
            return status == NTStatus.STATUS_SUCCESS && written > 0 ? NTStatus.STATUS_SUCCESS : status;
        }

        private static NTStatus ReadFile(ISMBFileStore store, string path, out byte[] data)
        {
            data = null;
            object handle;
            NTStatus status = store.CreateFile(out handle, out _, path, AccessMask.GENERIC_READ,
                SMBLibrary.FileAttributes.Normal, ShareAccess.Read | ShareAccess.Write,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, null);
            if (status != NTStatus.STATUS_SUCCESS) return status;
            status = store.ReadFile(out data, handle, 0, 4096);
            store.CloseFile(handle);
            return status;
        }

        private static List<string> QueryDir(ISMBFileStore store, out NTStatus status)
        {
            var entries = new List<string>();
            object dirHandle;
            status = store.CreateFile(out dirHandle, out _, "\\", AccessMask.GENERIC_READ,
                SMBLibrary.FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);
            if (status != NTStatus.STATUS_SUCCESS) return entries;
            status = store.QueryDirectory(out List<QueryDirectoryFileInformation> result, dirHandle, "*",
                FileInformationClass.FileDirectoryInformation);
            if (status == NTStatus.STATUS_SUCCESS || status == NTStatus.STATUS_NO_MORE_FILES)
            {
                foreach (QueryDirectoryFileInformation e in result)
                {
                    entries.Add(((FileDirectoryInformation)e).FileName);
                }
                status = NTStatus.STATUS_SUCCESS;
            }
            store.CloseFile(dirHandle);
            return entries;
        }

        private static NTStatus RenameFile(ISMBFileStore store, string from, string to)
        {
            object handle;
            NTStatus status = store.CreateFile(out handle, out _, from, AccessMask.GENERIC_ALL,
                SMBLibrary.FileAttributes.Normal, ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, null);
            if (status != NTStatus.STATUS_SUCCESS) return status;
            status = store.SetFileInformation(handle, new FileRenameInformationType2 { FileName = to });
            store.CloseFile(handle);
            return status;
        }

        private static NTStatus CreateDir(ISMBFileStore store, string path)
        {
            object handle;
            NTStatus status = store.CreateFile(out handle, out _, path, AccessMask.GENERIC_ALL,
                SMBLibrary.FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write,
                CreateDisposition.FILE_CREATE, CreateOptions.FILE_DIRECTORY_FILE, null);
            if (status != NTStatus.STATUS_SUCCESS) return status;
            store.CloseFile(handle);
            return NTStatus.STATUS_SUCCESS;
        }

        private static NTStatus DeleteFile(ISMBFileStore store, string path)
        {
            object handle;
            NTStatus status = store.CreateFile(out handle, out _, path, AccessMask.GENERIC_ALL,
                SMBLibrary.FileAttributes.Normal, ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, null);
            if (status != NTStatus.STATUS_SUCCESS) return status;
            status = store.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true });
            store.CloseFile(handle);
            return status;
        }

        private static void Cleanup(ISMBFileStore store, string path)
        {
            // Best-effort: deletes either a file or an (empty) directory.
            object handle;
            NTStatus status = store.CreateFile(out handle, out _, path, AccessMask.GENERIC_ALL,
                SMBLibrary.FileAttributes.Normal, ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, null);
            if (status == NTStatus.STATUS_SUCCESS)
            {
                store.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true });
                store.CloseFile(handle);
            }
            else
            {
                // Might be a directory (or already gone); try directory semantics.
                status = store.CreateFile(out handle, out _, path, AccessMask.GENERIC_ALL,
                    SMBLibrary.FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write | ShareAccess.Delete,
                    CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);
                if (status == NTStatus.STATUS_SUCCESS)
                {
                    store.SetFileInformation(handle, new FileDispositionInformation { DeletePending = true });
                    store.CloseFile(handle);
                }
            }
        }
    }
}
