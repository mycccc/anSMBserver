// Port-parameterized wrappers for SMBServer.Start and SMB2Client.Connect.
// Upstream exposes only fixed ports (445/139) via its public overloads; the
// port-taking overloads are `protected internal`, so a derived class can expose
// them without modifying SMBLibrary source. On Android (non-root) we need an
// unprivileged port such as 4450.
using System;
using System.Net;
using SMBLibrary;
using SMBLibrary.Client;
using SMBLibrary.Server;

namespace anSMBserver
{
    public class PortableSmbServer : SMBServer
    {
        public PortableSmbServer(SMBShareCollection shares, SMBLibrary.Authentication.GSSAPI.GSSProvider securityProvider)
            : base(shares, securityProvider)
        {
        }

        public void StartOnPort(IPAddress address, SMBTransportType transport, int port,
                                bool enableSMB1, bool enableSMB2, bool enableSMB3,
                                TimeSpan? connectionInactivityTimeout)
        {
            Start(address, transport, port, enableSMB1, enableSMB2, enableSMB3, connectionInactivityTimeout);
        }
    }

    public class PortableSmbClient : SMB2Client
    {
        public PortableSmbClient() : base()
        {
        }

        public bool ConnectOnPort(IPAddress address, SMBTransportType transport, int port)
        {
            return Connect(address, transport, port);
        }
    }
}
