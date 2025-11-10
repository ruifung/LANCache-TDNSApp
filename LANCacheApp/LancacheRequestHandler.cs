using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using TechnitiumLibrary.Net.Dns;
using TechnitiumLibrary.Net.Dns.ResourceRecords;

namespace LanCache;

public enum OperatingMode
{
    Blocking,
    Authoritative
}

public partial class App
{
    private async Task<DnsDatagram> HandleLancacheRequest(DnsDatagram request, IPEndPoint remoteEp, OperatingMode mode)
    {
        if (!Config.LanCacheEnabled || mode != Config.OperatingMode ||
            IgnoredClientNetworkAddresses.Any(netAddr => netAddr.Contains(remoteEp.Address)))
        {
            return null!;
        }

        var question = request.Question[0];
        var foundDirect = IsZoneFound(LanCacheDomains, question.Name, out var foundZone, out var cacheTarget);
        // Use short-circuiting to skip the wildcard search if it matches a direct domain.
        var foundWild = !foundDirect && IsZoneFound(WildcardDomains, question.Name, out foundZone, out cacheTarget);

        if ((!foundDirect && !foundWild) || cacheTarget == null)
        {
            return null!;
        }

        // Skip processing if the enabled cache whitelist is set, and the cache isn't enabled.
        if (Config.EnabledCaches.Count > 0 && !Config.EnabledCaches.Contains(cacheTarget))
        {
            return null!;
        }

        // Skip processing if the disabled cache blacklist is set, and the cache is disabled.
        if (Config.DisabledCaches.Count > 0 && Config.DisabledCaches.Contains(cacheTarget))
        {
            return null!;
        }

        WriteDebugLog("Resolving cache addresses for cache: " + cacheTarget);
        var hasOverride = Config.CacheAddresses.TryGetValue(cacheTarget, out var cacheAddresses);
        if (!hasOverride || cacheAddresses == null)
        {
            cacheAddresses = Config.GlobalCacheAddresses;
        }

        // Skip processing if there are no cache addresses specified for the cache. (Effectively disabled)
        if (cacheAddresses.Count == 0)
        {
            DnsServer.WriteLog("Skipping cache as they are no cache addresses specified for cache: " + cacheTarget);
            return null!;
        }

        WriteDebugLog("Resolved cache addresses: " + string.Join(", ", cacheAddresses));
        var answers = new List<DnsResourceRecord>();
        var additional = new List<DnsResourceRecord>();
        var domainZone = foundZone ?? question.Name;
        var authority = new[]
        {
            new DnsResourceRecord(domainZone, DnsResourceRecordType.SOA, question.Class, Config.RecordTtl, SoaRecord)
        };
        
        additional.Add(new DnsResourceRecord(question.Name, DnsResourceRecordType.TXT, question.Class, 60,
            new DnsTXTRecordData($"source=lancache-app; cache={cacheTarget}, cache-domain={domainZone}")));

        if (question.Type == DnsResourceRecordType.NS)
            answers.Add(new DnsResourceRecord(question.Name, DnsResourceRecordType.NS, question.Class, Config.RecordTtl,
                NsRecord));
        else if (question.Type == DnsResourceRecordType.SOA)
            answers.Add(new DnsResourceRecord(question.Name, DnsResourceRecordType.SOA, question.Class,
                Config.RecordTtl, SoaRecord));
        else
            foreach (var cacheAddress in cacheAddresses.Where(t => t != DUMMY_LANCACHE_ADDRESS))
            {
                var isIpAddress = IPAddress.TryParse(cacheAddress, out var ipAddress);
                if (isIpAddress)
                {
                    // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
                    switch (ipAddress?.AddressFamily)
                    {
                        case AddressFamily.InterNetwork:
                            if (question.Type == DnsResourceRecordType.A)
                            {
                                WriteDebugLog("Creating A record");
                                answers.Add(new DnsResourceRecord(question.Name, DnsResourceRecordType.A,
                                    question.Class, Config.RecordTtl, new DnsARecordData(ipAddress)));
                            }

                            break;
                        case AddressFamily.InterNetworkV6:
                            if (question.Type == DnsResourceRecordType.AAAA)
                            {
                                WriteDebugLog("Creating AAAA record");
                                answers.Add(new DnsResourceRecord(question.Name, DnsResourceRecordType.AAAA,
                                    question.Class, Config.RecordTtl, new DnsAAAARecordData(ipAddress)));
                            }

                            break;
                        default:
                            DnsServer.WriteLog("Found non v4/v6 IP address somehow: " + cacheAddress);
                            break;
                    }
                }
                else
                {
                    if (cacheAddress == question.Name)
                        DnsServer.WriteLog($"Attempted to cache {cacheAddress} using itself!");
                    else
                        try
                        {
                            WriteDebugLog("Creating CNAME record");
                            answers.Add(new DnsResourceRecord(question.Name, DnsResourceRecordType.CNAME,
                                question.Class,
                                Config.RecordTtl,
                                new DnsCNAMERecordData(cacheAddress)));
                            var newQuestion = new DnsQuestionRecord(cacheAddress, question.Type, question.Class);
                            
                            var cacheAddressIsInCachedDomains = IsZoneFound(LanCacheDomains, cacheAddress) || IsZoneFound(WildcardDomains, cacheAddress);
                            if (!cacheAddressIsInCachedDomains)
                            {
                                WriteDebugLog($"Querying for {question.Type} records for cache server {cacheAddress}");
                                var newResponse = await DnsServer.ResolveAsync(newQuestion);
                                if (newResponse.RCODE is DnsResponseCode.NoError)
                                    answers.AddRange(newResponse.Answer);
                                else
                                    DnsServer.WriteLog(
                                        $"Error querying cache target {cacheAddress} for QTYPE {question.Type} with RCODE {newResponse.RCODE}");
                            }
                            else
                            {
                                WriteDebugLog($"Cache address is itself, present in cached domains, skipping CNAME resolution.");
                                additional.Add(new DnsResourceRecord(question.Name, DnsResourceRecordType.TXT, question.Class, 60,
                                    new DnsTXTRecordData("source=lancache-app; warning=cache-address-is-cached-domain")));
                            }
                        }
                        catch (DnsClientException)
                        {
                            DnsServer.WriteLog("Invalid CNAME target: " + cacheAddress);
                        }
                }
            }

        // No valid cache targets found, handle normally to not break the cached urls.
        if (answers.Count > 0)
        {
            WriteDebugLog($"Returning DNSDatagram with {answers.Count} answers");
            return new DnsDatagram(request.Identifier, true, request.OPCODE, true, false,
                request.RecursionDesired, true, false, false, DnsResponseCode.NoError, request.Question, answers,
                authority, additional);
        }

        if (Config.LegacyUpstreamBehavior)
        {
            if (question.Type is DnsResourceRecordType.A or DnsResourceRecordType.AAAA)
                DnsServer.WriteLog($"No valid targets found for cache: {cacheTarget}, skipping processing");

            return null!;
        }

        // If legacy behavior is not enabled, return NXDomain for intercepted cache domains.
        // This aligns with the official lancache-dns as that configures a zone per cached domain, which would effectively result in this.
        WriteDebugLog("Returning DNSDatagram with NXDOMAIN response");
        return new DnsDatagram(request.Identifier, true, request.OPCODE, true, false,
            request.RecursionDesired, true, false, false, DnsResponseCode.NxDomain, request.Question, null, authority);
    }
}