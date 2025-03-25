using System;
using System.Net;
using System.Threading.Tasks;
using DnsServerCore.ApplicationCommon;
using TechnitiumLibrary.Net.Dns;

namespace LanCache;

public partial class App: IDnsAuthoritativeRequestHandler, IDnsRequestBlockingHandler
{
    public Task<DnsDatagram> ProcessRequestAsync(DnsDatagram request, IPEndPoint remoteEp, DnsTransportProtocol protocol,
        bool isRecursionAllowed)
    {
        return HandleLancacheRequest(request, remoteEp, OperatingMode.Authoritative);
    }
    
    public Task<DnsDatagram> ProcessRequestAsync(DnsDatagram request, IPEndPoint remoteEp)
    {
        return HandleLancacheRequest(request, remoteEp, OperatingMode.Blocking);
    }
}